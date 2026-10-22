"""Almacén opaco local cifrado + outbox transaccional. Sin esquema de restaurante."""
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import time
import uuid
from securedata import canonical

class VersionConflict(ValueError):pass
class EventConflict(ValueError):pass
class StorageFull(OSError):pass

SCHEMA_VERSION=1
def identity(value):
    if not isinstance(value,str) or not re.fullmatch(r"[a-zA-Z0-9_-]{1,96}",value):
        raise ValueError("Identificador inválido")
    return value
class Store:
    def __init__(self,path,site_id,keyring,min_free_bytes=128*1024*1024):
        self.path=Path(path);self.site=identity(site_id);self.keys=keyring
        self.min_free_bytes=min_free_bytes
        self.path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
        if self.path.is_symlink():raise ValueError("Base de datos no puede ser enlace")
        with self.connection() as db:
            db.execute("PRAGMA journal_mode=WAL")
            version=db.execute("PRAGMA user_version").fetchone()[0]
            if version not in (0,SCHEMA_VERSION):raise ValueError("Versión de almacén desconocida")
            db.executescript("""
            CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS records (
              id TEXT PRIMARY KEY, version INTEGER NOT NULL, envelope TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events (
              id TEXT PRIMARY KEY, record_id TEXT NOT NULL, version INTEGER NOT NULL,
              envelope TEXT NOT NULL, digest TEXT NOT NULL, created REAL NOT NULL,
              acked REAL, attempts INTEGER NOT NULL DEFAULT 0, next_attempt REAL NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS event_delivery ON events(acked,next_attempt,created);
            """)
            existing=db.execute("SELECT value FROM metadata WHERE key='site_id'").fetchone()
            if existing and existing[0]!=self.site:raise ValueError("La base pertenece a otra sucursal")
            check=db.execute("SELECT value FROM metadata WHERE key='key_check'").fetchone()
            context={"site_id":self.site,"purpose":"key-check-v1"}
            if existing:
                if check is None or self.keys.open(json.loads(check[0]),context)!={"schema":1}:
                    raise ValueError("Claves o integridad del almacén no válidas")
            else:
                if db.execute("SELECT count(*) FROM records").fetchone()[0]:raise ValueError("Falta identidad en almacén existente")
                db.execute("INSERT INTO metadata VALUES('key_check',?)",(canonical(self.keys.seal({"schema":1},context)).decode(),))
            db.execute("INSERT OR IGNORE INTO metadata VALUES('site_id',?)",(self.site,))
            db.execute("PRAGMA user_version=1")
        os.chmod(self.path,0o600)
    @contextmanager
    def connection(self):
        db=sqlite3.connect(self.path,timeout=3)
        db.row_factory=sqlite3.Row
        db.execute("PRAGMA synchronous=FULL")
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("PRAGMA busy_timeout=3000")
        try:
            yield db
            db.commit()
        except Exception:
            db.rollback();raise
        finally:db.close()
    def context(self,purpose,record_id,version,event_id=None):
        value={"site_id":self.site,"purpose":purpose,"record_id":record_id,"version":version}
        if event_id:value["event_id"]=event_id
        return value
    def put(self,record_id,version,data,event_id,expected_version):
        identity(record_id);identity(event_id)
        if type(version) is not int or version<1 or type(expected_version) is not int or expected_version<0 or version!=expected_version+1:
            raise ValueError("Versión nueva debe ser anterior + 1")
        wire={"protocol":"culinaryos-events-v1","site_id":self.site,"event_id":event_id,
              "record_id":record_id,"version":version,"data":data}
        digest=hashlib.sha256(canonical(wire)).hexdigest()
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            prior=db.execute("SELECT digest FROM events WHERE id=?",(event_id,)).fetchone()
            if prior:
                if prior["digest"]!=digest:raise EventConflict("ID de evento reutilizado con otro contenido")
                return {"event_id":event_id,"duplicate":True}
            current=db.execute("SELECT version FROM records WHERE id=?",(record_id,)).fetchone()
            if (current["version"] if current else 0)!=expected_version:
                raise VersionConflict("Conflicto de versión")
            if shutil.disk_usage(self.path.parent).free<self.min_free_bytes:
                raise StorageFull("Espacio insuficiente; operación no confirmada")
            envelope=self.keys.seal(data,self.context("record",record_id,version))
            event=self.keys.seal(wire,self.context("event",record_id,version,event_id))
            db.execute("INSERT INTO records VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET version=excluded.version,envelope=excluded.envelope",
                       (record_id,version,canonical(envelope).decode()))
            db.execute("INSERT INTO events(id,record_id,version,envelope,digest,created) VALUES(?,?,?,?,?,?)",
                       (event_id,record_id,version,canonical(event).decode(),digest,time.time()))
        return {"event_id":event_id,"duplicate":False}
    def get(self,record_id):
        identity(record_id)
        with self.connection() as db:row=db.execute("SELECT * FROM records WHERE id=?",(record_id,)).fetchone()
        if row is None:return None
        return {"record_id":record_id,"version":row["version"],
                "data":self.keys.open(json.loads(row["envelope"]),self.context("record",record_id,row["version"]))}
    def pending(self,now=None,limit=8):
        now=time.time() if now is None else now
        with self.connection() as db:
            # FIFO estricto: un evento pendiente bloquea posteriores hasta su ACK.
            rows=db.execute("SELECT * FROM events WHERE acked IS NULL ORDER BY rowid LIMIT ?",(limit,)).fetchall()
        values=[]
        for row in rows:
            if now<row["next_attempt"]<=now+3605:break
            wire=self.keys.open(json.loads(row["envelope"]),self.context("event",row["record_id"],row["version"],row["id"]))
            if hashlib.sha256(canonical(wire)).hexdigest()!=row["digest"]:raise ValueError("Integridad de evento inválida")
            values.append((wire,row["digest"]))
        return values
    def acknowledge(self,event_id,digest):
        with self.connection() as db:
            changed=db.execute("UPDATE events SET acked=? WHERE id=? AND digest=? AND acked IS NULL",
                               (time.time(),event_id,digest)).rowcount
            if changed!=1:raise ValueError("Confirmación no corresponde a un evento pendiente")
    def retry(self,event_id,now=None):
        now=time.time() if now is None else now
        with self.connection() as db:
            row=db.execute("SELECT attempts FROM events WHERE id=? AND acked IS NULL",(event_id,)).fetchone()
            if row:
                delay=min(3600,5*(2**min(row["attempts"],10)))
                db.execute("UPDATE events SET attempts=attempts+1,next_attempt=? WHERE id=?",(now+delay,event_id))
    def status(self):
        with self.connection() as db:
            counts=db.execute("SELECT count(*),sum(acked IS NULL),min(CASE WHEN acked IS NULL THEN created END) FROM events").fetchone()
            valid=db.execute("PRAGMA quick_check").fetchone()[0]=="ok"
        return {"ready":valid,"site_id":self.site,"events":counts[0],"pending":counts[1] or 0,
                "oldest_pending":counts[2],"synchronous":"FULL","schema":SCHEMA_VERSION}
    def snapshot(self,destination):
        destination=Path(destination)
        if destination.exists():raise FileExistsError(destination)
        with self.connection() as db:
            copy=sqlite3.connect(destination)
            try:db.backup(copy)
            finally:copy.close()
        os.chmod(destination,0o600)
        with open(destination,"rb+") as f:os.fsync(f.fileno())
        if os.name=="posix":
            fd=os.open(destination.parent,os.O_RDONLY|os.O_DIRECTORY)
            try:os.fsync(fd)
            finally:os.close(fd)
        return destination
    def rotate(self):
        """Recifra en una transacción con active; conservar todas las claves durante recuperación."""
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            ctx={"site_id":self.site,"purpose":"key-check-v1"}
            db.execute("UPDATE metadata SET value=? WHERE key='key_check'",(canonical(self.keys.seal({"schema":1},ctx)).decode(),))
            for row in db.execute("SELECT * FROM records").fetchall():
                ctx=self.context("record",row["id"],row["version"])
                clear=self.keys.open(json.loads(row["envelope"]),ctx)
                db.execute("UPDATE records SET envelope=? WHERE id=?",(canonical(self.keys.seal(clear,ctx)).decode(),row["id"]))
            for row in db.execute("SELECT * FROM events").fetchall():
                ctx=self.context("event",row["record_id"],row["version"],row["id"])
                clear=self.keys.open(json.loads(row["envelope"]),ctx)
                db.execute("UPDATE events SET envelope=? WHERE id=?",(canonical(self.keys.seal(clear,ctx)).decode(),row["id"]))

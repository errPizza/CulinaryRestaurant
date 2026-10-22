#!/usr/bin/python3
"""Servicio IPC local de almacenamiento y cliente para la app nativa futura."""
import argparse
import json
import os
from pathlib import Path
import signal
import socket
import socketserver
import stat
import sys
from securedata import Keyring,canonical
from store import Store,VersionConflict,EventConflict,StorageFull

class DataError(ValueError):
    def __init__(self,code):
        super().__init__(code);self.code=code

SOCKET="/run/culinaryos-data/store.sock"
LIMIT=128*1024
def notify(message):
    target=os.environ.get("NOTIFY_SOCKET")
    if not target:return
    if target.startswith("@"):target="\0"+target[1:]
    with socket.socket(socket.AF_UNIX,socket.SOCK_DGRAM) as s:s.connect(target);s.sendall(message.encode())

class Client:
    def __init__(self,path=SOCKET):self.path=str(path)
    def call(self,operation,**kwargs):
        request=canonical({"op":operation,**kwargs})+b"\n"
        if len(request)>LIMIT:raise ValueError("Solicitud demasiado grande")
        with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as conn:
            conn.settimeout(4);conn.connect(self.path);conn.sendall(request)
            with conn.makefile("rb") as f:raw=f.readline(LIMIT+1)
        if len(raw)>LIMIT:raise ValueError("Respuesta demasiado grande")
        result=json.loads(raw)
        if not result.get("ok"):raise DataError(result.get("error","COS-DATA-ERROR"))
        return result["result"]

def dispatch(store,request):
    if not isinstance(request,dict):raise ValueError()
    op=request.get("op")
    if op=="health":return store.status()
    if op=="get":return store.get(request["record_id"])
    if op=="put":
        return store.put(request["record_id"],request["version"],request["data"],request["event_id"],request["expected_version"])
    raise ValueError("Operación no admitida")

def persistent_root():
    if sys.platform!="linux":return
    if "boot=live" in Path("/proc/cmdline").read_text().split():
        raise RuntimeError("No almacenar operaciones en Live sin persistencia instalada")
    # /proc/1/mounts evita el namespace RO de ProtectSystem.
    for line in Path("/proc/1/mounts").read_text().splitlines():
        fields=line.split()
        if fields[1]=="/" and fields[2] in ("overlay","tmpfs","squashfs"):
            raise RuntimeError("Raíz volátil no admitida para operaciones")

def configured_store():
    persistent_root()
    cfg=json.loads(Path("/etc/culinaryos/branch.json").read_text())
    if cfg.get("version")!=1:raise ValueError("Configuración de sucursal incompatible")
    return Store("/var/lib/culinaryos-data/store.sqlite",cfg["site_id"],Keyring.load_systemd_credential())

def serve(store,path=SOCKET,stop_event=None):
    path=Path(path)
    if path.exists() or path.is_symlink():
        if not stat.S_ISSOCK(path.lstat().st_mode):raise ValueError("Ruta IPC ocupada")
        path.unlink()
    class Handler(socketserver.StreamRequestHandler):
        def handle(self):
            self.connection.settimeout(3)
            try:
                raw=self.rfile.readline(LIMIT+1)
                if len(raw)>LIMIT or not raw.endswith(b"\n"):raise ValueError()
                result=dispatch(store,json.loads(raw))
                response={"ok":True,"result":result}
            except Exception as error:
                code="COS-DATA-CONFLICT" if isinstance(error,VersionConflict) else "COS-DATA-IDEMPOTENCY" if isinstance(error,EventConflict) else "COS-DATA-FULL" if isinstance(error,StorageFull) else "COS-DATA-INVALID" if isinstance(error,(ValueError,KeyError,TypeError)) else "COS-DATA-UNAVAILABLE"
                response={"ok":False,"error":code}
            self.wfile.write(canonical(response)+b"\n")
    if not store.status()["ready"]:raise RuntimeError("Almacén requiere recuperación")
    with socketserver.UnixStreamServer(str(path),Handler) as server:
        os.chmod(path,0o660)
        server.timeout=1
        stopping=False
        def stop(signum,frame):
            nonlocal stopping
            stopping=True
        if __import__("threading").current_thread() is __import__("threading").main_thread():
            signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
        notify("READY=1\nSTATUS=Almacén local disponible")
        while not stopping and not (stop_event and stop_event.is_set()):server.handle_request()
    path.unlink(missing_ok=True)

def sync():
    import fcntl
    from syncworker import HTTPSCentral,deliver
    cfg=json.loads(Path("/etc/culinaryos/branch.json").read_text())
    if not cfg.get("central"):return {"state":"not_configured"}
    # Un solo emisor conserva FIFO e impide carreras en ACK/reintentos.
    with open("/var/lib/culinaryos-data/sync.lock","a") as lock:
        fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        config=dict(cfg["central"])
        credentials=Path(os.environ["CREDENTIALS_DIRECTORY"])
        config.update(ca_file=str(credentials/"central-ca"),client_certificate=str(credentials/"central-cert"),client_key=str(credentials/"central-key"))
        return deliver(configured_store(),HTTPSCentral(config))

def main():
    p=argparse.ArgumentParser()
    p.add_argument("command",choices=("serve","sync","health","rotate","snapshot"))
    p.add_argument("--output")
    args=p.parse_args()
    if args.command=="serve":serve(configured_store())
    elif args.command=="sync":print(json.dumps(sync()))
    elif args.command=="health":print(json.dumps(Client().call("health")))
    elif args.command=="rotate":configured_store().rotate()
    elif args.command=="snapshot":
        import uuid,hashlib
        destination=Path(args.output) if args.output else Path("/var/lib/culinaryos-data/snapshots")/(str(uuid.uuid4())+".sqlite")
        destination.parent.mkdir(mode=0o700,parents=True,exist_ok=True)
        configured_store().snapshot(destination)
        digest=hashlib.sha256()
        with open(destination,"rb") as file:
            for chunk in iter(lambda:file.read(1024*1024),b""):digest.update(chunk)
        with open(destination.with_suffix(".sha256"),"x") as file:
            file.write(digest.hexdigest()+"\n");file.flush();os.fsync(file.fileno())
        print(destination)
if __name__=="__main__":main()

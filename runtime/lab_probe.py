"""Read-only checks for the interactive local laboratory launcher."""
import argparse
import json
from pathlib import Path
import socket
import sqlite3
import ssl
import urllib.error
import urllib.request


def database_status(directory, site):
    # The lab branch lives in its private PostgreSQL cluster; the backend leaves this marker once it is initialized.
    marker = Path(directory).resolve() / 'initialized'
    if marker.exists():
        if marker.read_text(encoding='utf-8').strip() != site:
            raise ValueError('La base existente pertenece a otro sitio; no se modifica.')
        return {'ok': True, 'initialized': True, 'legacy': False}
    # A lab from before PostgreSQL: its SQLite store is migrated once, then kept as it was.
    database = Path(directory).resolve() / 'store.sqlite'
    if not database.exists():
        return {'ok': True, 'initialized': False, 'legacy': False}
    with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True, timeout=3) as db:
        tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if not tables:
            return {'ok': True, 'initialized': False, 'legacy': False}
        if 'metadata' not in tables or 'records' not in tables:
            raise ValueError('El archivo existente no es un almacén de CulinaryOS; no se modifica.')
        identity = db.execute("SELECT value FROM metadata WHERE key='site_id'").fetchone()
        if identity is None or identity[0] != site:
            raise ValueError('La base existente pertenece a otro sitio; no se modifica.')
        initialized = db.execute("SELECT EXISTS(SELECT 1 FROM records WHERE id=?)", ('mn_meta_site',)).fetchone()[0]
        return {'ok': True, 'initialized': bool(initialized), 'legacy': bool(initialized)}


def server_status(directory, site, port):
    try:
        with socket.create_connection(('127.0.0.1', port), timeout=3):
            pass
    except ConnectionRefusedError:
        return {'ok': True, 'listening': False, 'trusted': False, 'ready': False}
    except OSError:
        return {'ok': False, 'error': 'No se pudo comprobar el puerto local.'}
    ca = Path(directory).resolve() / 'lab-ca.crt'
    if not ca.is_file():
        return {'ok': True, 'listening': True, 'trusted': False, 'ready': False,
                'error': 'El puerto está ocupado y no existe la CA de este laboratorio.'}
    try:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.load_verify_locations(cafile=str(ca))
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
        base = 'https://localhost:' + str(port)
        with opener.open(base + '/api/identity', timeout=2) as response:
            identity = json.load(response)
        if identity.get('siteId') != site or identity.get('name') != 'Culinary Restaurant' or identity.get('protocol') != 1:
            raise ValueError('La identidad del servidor no corresponde a este laboratorio.')
        try:
            with opener.open(base + '/api/health', timeout=2) as response:
                health = json.load(response)
            if health.get('ready') is not True or health.get('site_id') != site:
                raise ValueError('Respuesta de disponibilidad incompatible.')
            ready = True
        except urllib.error.HTTPError as error:
            if error.code != 503:
                raise
            ready = False
        return {'ok': True, 'listening': True, 'trusted': True, 'ready': ready}
    except Exception:
        return {'ok': True, 'listening': True, 'trusted': False, 'ready': False,
                'error': 'El servicio del puerto no supera la validación TLS, nombre o identidad del laboratorio.'}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=['database', 'server', 'fingerprint'])
    parser.add_argument('--directory', required=True)
    parser.add_argument('--site', required=True)
    parser.add_argument('--port', type=int, default=8443)
    args = parser.parse_args()
    try:
        if args.operation == 'database':
            result = database_status(args.directory, args.site)
        elif args.operation == 'server':
            result = server_status(args.directory, args.site, args.port)
        else:
            import hashlib
            pem = (Path(args.directory) / 'lab-ca.crt').read_text(encoding='ascii')
            result = {'ok': True, 'sha256': hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest()}
    except ValueError as error:
        result = {'ok': False, 'error': str(error)}
    except Exception:
        result = {'ok': False, 'error': 'No se pudo comprobar el laboratorio existente; no se modifica.'}
    print(json.dumps(result, ensure_ascii=True))
    return 0 if result.get('ok') else 1


if __name__ == '__main__':
    raise SystemExit(main())

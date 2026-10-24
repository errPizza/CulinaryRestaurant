"""One-shot artifact download on branch server, reusing provisioned central mTLS.
Not an event synchronizer. Does not ACK or change branch data.
Run with central-ca/cert/key LoadCredential as the existing sync service does.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from urllib.parse import quote
sys.path.insert(0, '/usr/local/lib/culinaryos')
from syncworker import HTTPSCentral
import http.client

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--platform', choices=['win-x64', 'linux-x64', 'android-arm64'], required=True)
    p.add_argument('--output', required=True)
    a = p.parse_args()
    output = Path(a.output)
    if output.exists():
        raise ValueError('Use a new output directory')
    cfg = json.loads(Path('/etc/culinaryos/branch.json').read_text())['central']
    creds = Path(os.environ['CREDENTIALS_DIRECTORY'])
    cfg.update(ca_file=str(creds / 'central-ca'), client_certificate=str(creds / 'central-cert'), client_key=str(creds / 'central-key'))
    transport = HTTPSCentral(cfg)
    base = transport.url
    def get(path):
        conn = http.client.HTTPSConnection(base.hostname, base.port or 443, context=transport.context, timeout=15)
        conn.request('GET', path)
        reply = conn.getresponse()
        if reply.status != 200:
            conn.close()
            raise ValueError('Central did not authorize artifact')
        return conn, reply
    conn, reply = get('/v1/releases/latest?platform=' + quote(a.platform))
    raw = reply.read(65537)
    conn.close()
    if len(raw) > 65536:
        raise ValueError('Manifest too large')
    envelope = json.loads(raw)
    data = envelope['data']
    manifest = data['manifest']
    if data['siteId'] != json.loads(Path('/etc/culinaryos/site.json').read_text())['site_id']:
        raise ValueError('Wrong recipient')
    name = manifest['filename']
    if Path(name).name != name or not all(c.isalnum() or c in '._-' for c in name):
        raise ValueError('Bad artifact filename')
    output.mkdir(mode=0o700, parents=True)
    conn, reply = get('/v1/releases/' + quote(name))
    digest = hashlib.sha512()
    size = 0
    target = output / name
    try:
        with target.open('xb') as file:
            while chunk := reply.read(65536):
                size += len(chunk)
                if size > 500 * 1024 * 1024:
                    raise ValueError('Artifact too large')
                digest.update(chunk)
                file.write(chunk)
            file.flush()
            os.fsync(file.fileno())
    finally:
        conn.close()
    if digest.hexdigest() != manifest['sha512'] or size != manifest['bytes']:
        raise ValueError('Artifact hash mismatch')
    (output / 'publication.json').write_bytes(raw)
    print('Downloaded, hash checked. The native verifier must also check provider signature, recipient and compatibility before installation.')
if __name__ == '__main__':
    main()
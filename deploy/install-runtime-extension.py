#!/usr/bin/python3
"""Explicit in-VM maintenance installation. No Windows, network or key changes."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[1]
TARGET=Path('/usr/local/lib/culinaryos')
def command(*args,check=True):return subprocess.run(args,check=check)
def install_file(path,body):
    path=Path(path);temp=path.with_name(path.name+'.mesanexo-new')
    with temp.open('xb') as f:f.write(body);f.flush();os.fsync(f.fileno())
    temp.chmod(0o644);os.chown(temp,0,0);os.replace(temp,path)

def main():
    if sys.platform!='linux' or os.geteuid()!=0:raise SystemExit('Run as administrator inside installed CulinaryOS, never on Windows')
    hashes=json.loads((ROOT/'vendor'/'culinaryos'/'source-hashes.json').read_text())
    for entry in hashes:
        path=TARGET/entry['file']
        actual=hashlib.sha256(path.read_bytes()).hexdigest().lower()
        accepted={value.lower() for value in [entry['sha256'],*entry.get('compatibleSha256',[])]}
        if path.is_symlink() or actual not in accepted:
            raise SystemExit('Runtime baseline differs: '+entry['file']+' (SHA256 '+actual+'). Audit before upgrading; nothing replaced.')
    source=(ROOT/'runtime'/'mesanexo_ipc.py').read_bytes();compile(source,'mesanexo_ipc.py','exec')
    original=(TARGET/'dataio.py').read_text()
    marker='if __name__=="__main__":main()'
    if original.count(marker)!=1:raise SystemExit('Unknown entrypoint')
    patched=original.replace(marker,'import mesanexo_ipc\nmesanexo_ipc.install(sys.modules[__name__])\n'+marker)
    compile(patched,'dataio.py','exec')
    if (TARGET/'mesanexo_ipc.py').exists():raise SystemExit('Extension already exists; inspect its version before changing it')
    # The original service performs the consistent encrypted snapshot, with its own credentials.
    command('systemctl','start','culinaryos-snapshot.service')
    stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    backup=Path('/var/backups')/('mesanexo-runtime-'+stamp);backup.mkdir(mode=0o700)
    shutil.copy2(TARGET/'dataio.py',backup/'dataio.py')
    active=[unit for unit in ('culinaryos-backend.service','culinaryos-sync.timer','culinaryos-data.service') if command('systemctl','is-active','--quiet',unit,check=False).returncode==0]
    command('systemctl','stop','culinaryos-backend.service','culinaryos-sync.timer','culinaryos-sync.service',check=False)
    command('systemctl','stop','culinaryos-data.service')
    try:
        for name,body in [('mesanexo_ipc.py',source),('dataio.py',patched.encode('utf-8'))]:
            install_file(TARGET/name,body)
        command('systemctl','start','culinaryos-data.service')
        command('/usr/bin/python3',str(TARGET/'dataio.py'),'health')
        for unit in active:
            if unit!='culinaryos-data.service':command('systemctl','start',unit)
    except Exception:
        shutil.copy2(backup/'dataio.py',TARGET/'dataio.py');(TARGET/'mesanexo_ipc.py').unlink(missing_ok=True)
        command('systemctl','restart','culinaryos-data.service',check=False)
        raise
    print('Extension installed on same store. Code backup:',backup)
    print('No site identity, keys, firewall, addressing or synchronizer replaced.')
if __name__=='__main__':main()
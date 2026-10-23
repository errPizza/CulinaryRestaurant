#!/usr/bin/python3
"""Explicit in-VM maintenance installation. No Windows, network or key changes.
With --upgrade it replaces only an earlier extension listed in deploy/culinaryos/extension-history.json.
"""
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
BACKUPS=Path('/var/backups')
HOOK='import mesanexo_ipc\nmesanexo_ipc.install(sys.modules[__name__])\n'
def sha256(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest().lower()
def command(*args,check=True):return subprocess.run(args,check=check)
def install_file(path,body):
    path=Path(path);temp=path.with_name(path.name+'.mesanexo-new')
    with temp.open('xb') as f:f.write(body);f.flush();os.fsync(f.fileno())
    temp.chmod(0o644);os.chown(temp,0,0);os.replace(temp,path)

def main(upgrade=False):
    if sys.platform!='linux' or os.geteuid()!=0:raise SystemExit('Run as administrator inside installed CulinaryOS, never on Windows')
    hashes=json.loads((ROOT/'vendor'/'culinaryos'/'source-hashes.json').read_text())
    for entry in hashes:
        path=TARGET/entry['file']
        data=path.read_bytes()
        # An installed extension added only the hook to dataio.py; the audited baseline is what remains.
        if entry['file']=='dataio.py':data=data.replace(HOOK.encode(),b'')
        actual=hashlib.sha256(data).hexdigest().lower()
        accepted={value.lower() for value in [entry['sha256'],*entry.get('compatibleSha256',[])]}
        if path.is_symlink() or actual not in accepted:
            raise SystemExit('Runtime baseline differs: '+entry['file']+' (SHA256 '+actual+'). Audit before upgrading; nothing replaced.')
    source=(ROOT/'runtime'/'mesanexo_ipc.py').read_bytes();compile(source,'mesanexo_ipc.py','exec')
    original=(TARGET/'dataio.py').read_text();current=TARGET/'mesanexo_ipc.py'
    if upgrade:
        installed=sha256(current) if current.is_file() and not current.is_symlink() else 'missing'
        if installed==hashlib.sha256(source).hexdigest():print('Extension already current; nothing replaced');return
        known={value.lower() for release in json.loads((ROOT/'deploy'/'culinaryos'/'extension-history.json').read_text())['releases'] for value in release['sha256']}
        if installed not in known:raise SystemExit('Installed extension is not a known earlier version (SHA256 '+installed+'); nothing replaced')
        if original.count(HOOK)!=1:raise SystemExit('Runtime hook missing or repeated; nothing replaced')
        # dataio.py keeps the hook it already has; only the extension changes.
        replaced=[('mesanexo_ipc.py',source)]
    else:
        marker='if __name__=="__main__":main()'
        if original.count(marker)!=1:raise SystemExit('Unknown entrypoint')
        patched=original.replace(marker,HOOK+marker)
        compile(patched,'dataio.py','exec')
        if current.exists():raise SystemExit('Extension already exists; use --upgrade for a known earlier version')
        replaced=[('mesanexo_ipc.py',source),('dataio.py',patched.encode('utf-8'))]
    # The original service performs the consistent encrypted snapshot, with its own credentials.
    command('systemctl','start','culinaryos-snapshot.service')
    stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    backup=BACKUPS/('mesanexo-runtime-'+stamp);backup.mkdir(mode=0o700)
    shutil.copy2(TARGET/'dataio.py',backup/'dataio.py')
    if upgrade:shutil.copy2(current,backup/'mesanexo_ipc.py')
    active=[unit for unit in ('culinaryos-backend.service','culinaryos-sync.timer','culinaryos-data.service') if command('systemctl','is-active','--quiet',unit,check=False).returncode==0]
    command('systemctl','stop','culinaryos-backend.service','culinaryos-sync.timer','culinaryos-sync.service',check=False)
    command('systemctl','stop','culinaryos-data.service')
    try:
        for name,body in replaced:
            install_file(TARGET/name,body)
        command('systemctl','start','culinaryos-data.service')
        command('/usr/bin/python3',str(TARGET/'dataio.py'),'health')
        for unit in active:
            if unit!='culinaryos-data.service':command('systemctl','start',unit)
    except Exception:
        shutil.copy2(backup/'dataio.py',TARGET/'dataio.py')
        if upgrade:shutil.copy2(backup/'mesanexo_ipc.py',current)
        else:current.unlink(missing_ok=True)
        command('systemctl','restart','culinaryos-data.service',check=False)
        raise
    print('Extension '+('upgraded' if upgrade else 'installed')+' on same store. Code backup:',backup)
    print('No site identity, keys, firewall, addressing or synchronizer replaced.')
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--upgrade',action='store_true',help='replace a known earlier extension after a snapshot and a code backup')
    main(parser.parse_args().upgrade)
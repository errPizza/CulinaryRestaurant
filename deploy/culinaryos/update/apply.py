#!/usr/bin/python3
"""Root side of a Linux update. The kiosk user only drops files: nothing is installed until the installed backend
verifies the signed offer against a private copy of the package."""
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
SPOOL = Path('/var/lib/culinary-update')
INBOX = SPOOL / 'inbox'
WORK = SPOOL / 'work'
API = '/opt/culinaryos-app/backend/MesaNexo.Api'

def take(name):
    source = os.open(INBOX / name, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(source, 'rb') as file:
        if not stat.S_ISREG(os.fstat(file.fileno()).st_mode):
            raise ValueError('Not a regular file: ' + name)
        with (WORK / name).open('wb') as target:
            shutil.copyfileobj(file, target)
    return WORK / name

def main():
    shutil.rmtree(WORK, ignore_errors=True)
    WORK.mkdir(mode=0o700)
    try:
        try:
            offer, package = take('offer.json'), take('package.deb')
        finally:
            for entry in INBOX.iterdir():
                entry.unlink()
        site = json.loads(Path('/etc/culinaryos/site.json').read_text())['site_id']
        subprocess.run([API, '--verify-update', str(offer), '--package', str(package), '--site', site], check=True, timeout=300)
        subprocess.run(['dpkg', '-i', str(package)], check=True, env=dict(os.environ, DEBIAN_FRONTEND='noninteractive'))
    finally:
        shutil.rmtree(WORK, ignore_errors=True)

if __name__ == '__main__':
    main()

#!/usr/bin/python3
"""Root side of a Linux update. The kiosk user only drops files: nothing is installed until the installed backend
verifies the signed offer against a private copy of the package.

An update is a journaled transaction in state.json: received → verified → snapshot → installing → installed → healthy.
Before dpkg touches anything the installed application is copied aside. A dpkg that fails, a power cut in the middle
(--recover, at boot before the backend) or a backend that does not come up healthy after the reboot (--health) puts
that copy back, so the branch always boots a version that worked. The package carries no branch data and no schema
migration (that is --upgrade-schema, run by hand with its own pg_dump), so going back to the old binaries is safe."""
import json
import os
from pathlib import Path
import shutil
import ssl
import stat
import subprocess
import sys
import time
import urllib.request
SPOOL = Path('/var/lib/culinary-update')
INBOX = SPOOL / 'inbox'
WORK = SPOOL / 'work'
STATE = SPOOL / 'state.json'
PREVIOUS = SPOOL / 'previous'
APP = Path('/opt/culinaryos-app')
API = '/opt/culinaryos-app/backend/MesaNexo.Api'
SITE = Path('/etc/culinaryos/site.json')
HEALTH = 'https://127.0.0.1:8443/api/health'
CA = Path('/etc/culinaryos/restaurant-ca.crt')
HEALTH_SECONDS = 600

def run(args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)

def state():
    try:
        return json.loads(STATE.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {'stage': 'none', 'history': []}

def mark(stage, **details):
    """Each stage is written and synced before the next step starts, so a power cut leaves the last one reached."""
    current = state()
    current.update(details, stage=stage)
    current.setdefault('history', []).append({'stage': stage, 'at': time.time(), **details})
    temp = STATE.with_name('state.json.new')
    with temp.open('w', encoding='utf-8') as stream:
        json.dump(current, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, STATE)
    return current

def take(name):
    source = os.open(INBOX / name, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(source, 'rb') as file:
        if not stat.S_ISREG(os.fstat(file.fileno()).st_mode):
            raise ValueError('Not a regular file: ' + name)
        with (WORK / name).open('wb') as target:
            shutil.copyfileobj(file, target)
    return WORK / name

def snapshot():
    temp = PREVIOUS.with_name('previous.new')
    shutil.rmtree(temp, ignore_errors=True)
    shutil.copytree(APP, temp, symlinks=True)
    shutil.rmtree(PREVIOUS, ignore_errors=True)
    os.replace(temp, PREVIOUS)

def restore(reason):
    """Puts the copy taken before dpkg back in place of the application, whole."""
    if not PREVIOUS.exists():
        mark('failed', reason=reason + '; no hay copia de la versión anterior')
        return False
    failed = APP.with_name(APP.name + '.failed')
    shutil.rmtree(failed, ignore_errors=True)
    if APP.exists():
        os.replace(APP, failed)
    shutil.copytree(PREVIOUS, APP, symlinks=True)
    shutil.rmtree(failed, ignore_errors=True)
    mark('rolled-back', reason=reason)
    return True

def install(package):
    try:
        run(['dpkg', '-i', str(package)], env=dict(os.environ, DEBIAN_FRONTEND='noninteractive'))
    except subprocess.CalledProcessError:
        # A half-configured package can often finish on its own; if not, the previous version comes back.
        try:
            run(['dpkg', '--configure', '-a'], env=dict(os.environ, DEBIAN_FRONTEND='noninteractive'))
        except subprocess.CalledProcessError:
            restore('dpkg no pudo instalar el paquete')
            raise

def main():
    shutil.rmtree(WORK, ignore_errors=True)
    WORK.mkdir(mode=0o700)
    try:
        try:
            offer, package = take('offer.json'), take('package.deb')
        finally:
            for entry in INBOX.iterdir():
                entry.unlink()
        mark('received', offer=json.loads(offer.read_text(encoding='utf-8')).get('version', ''))
        site = json.loads(SITE.read_text())['site_id']
        run([API, '--verify-update', str(offer), '--package', str(package), '--site', site], timeout=300)
        mark('verified')
        snapshot()
        mark('snapshot')
        mark('installing')
        install(package)
        # The package's postinst reboots; --health decides after that boot whether it stays.
        mark('installed')
    finally:
        shutil.rmtree(WORK, ignore_errors=True)

def recover():
    """At boot, before the backend: an update cut off while dpkg was running is finished or undone."""
    if state()['stage'] != 'installing':
        return
    try:
        run(['dpkg', '--configure', '-a'], env=dict(os.environ, DEBIAN_FRONTEND='noninteractive'))
        mark('installed', recovered=True)
    except subprocess.CalledProcessError:
        restore('se interrumpió la instalación y dpkg no pudo terminarla')

def healthy(deadline):
    context = ssl.create_default_context(cafile=str(CA))
    context.check_hostname = False
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(HEALTH, timeout=5, context=context) as answer:
                if answer.status == 200:
                    return True
        except OSError:
            pass
        time.sleep(5)
    return False

def health(restart=lambda: run(['systemctl', 'restart', 'culinaryos-backend.service'])):
    """After the reboot that follows an install: the new version stays only if the backend answers ready."""
    if state()['stage'] != 'installed':
        return
    if healthy(time.monotonic() + HEALTH_SECONDS):
        shutil.rmtree(PREVIOUS, ignore_errors=True)
        mark('healthy')
    elif restore('el backend no respondió listo después de actualizar'):
        restart()

if __name__ == '__main__':
    {'--recover': recover, '--health': health}.get(sys.argv[1] if len(sys.argv) > 1 else '', main)()

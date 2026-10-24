#!/usr/bin/python3
"""Writes the machine identifiers a license binds to when there is no TPM.

Runs as root right before the backend (ExecStartPre=+), because the backend's sandbox cannot read them. Only their
hashes leave the machine. It never fails: a missing identifier only stops activation or renewal, never the backend.
"""
import json
import os
from pathlib import Path
import subprocess

OUTPUT = Path('/run/culinaryos-app/hardware.json')

def command(*argv):
    try:
        return subprocess.run(argv, capture_output=True, text=True, timeout=10, check=False).stdout
    except (OSError, subprocess.SubprocessError):
        return ''

def board():
    try:
        return Path('/sys/class/dmi/id/product_uuid').read_text().strip().lower()
    except OSError:
        return ''

def disk():
    # The physical disk under the root file system, through any LUKS or LVM layers.
    source = command('findmnt', '-no', 'SOURCE', '/').strip()
    rows = [line.split() for line in command('lsblk', '-nspo', 'NAME,TYPE', source).splitlines()]
    device = next((row[0].lstrip('`|-') for row in rows if len(row) == 2 and row[1] == 'disk'), '')
    if not device:
        return ''
    for line in command('udevadm', 'info', '--query=property', '--name=' + device).splitlines():
        key, _, value = line.partition('=')
        if key == 'ID_SERIAL_SHORT' and value.strip():
            return value.strip()
    try:
        return Path('/sys/block', os.path.basename(device), 'device/serial').read_text().strip()
    except OSError:
        return ''

def mac():
    # The interface of the default route when it is physical; otherwise the first physical one.
    physical = sorted(p.name for p in Path('/sys/class/net').iterdir() if (p / 'device').exists())
    try:
        routes = [line.split() for line in Path('/proc/net/route').read_text().splitlines()[1:]]
        default = next((row[0] for row in routes if len(row) > 1 and row[1] == '00000000'), '')
    except OSError:
        default = ''
    name = default if default in physical else (physical[0] if physical else '')
    try:
        return Path('/sys/class/net', name, 'address').read_text().strip().lower() if name else ''
    except OSError:
        return ''

def main():
    values = {'board': board(), 'disk': disk(), 'mac': mac()}
    temporary = OUTPUT.with_suffix('.tmp')
    try:
        with open(temporary, 'w', encoding='utf-8') as stream:
            json.dump(values, stream)
        user = command('id', '-u', 'culinaryos-app').strip()
        if user.isdigit():
            os.chown(temporary, int(user), -1)
        os.chmod(temporary, 0o400)
        os.replace(temporary, OUTPUT)
    except OSError:
        pass

if __name__ == '__main__':
    main()

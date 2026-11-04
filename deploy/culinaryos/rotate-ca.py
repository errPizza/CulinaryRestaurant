#!/usr/bin/python3
"""Rotates the branch's local CA without stopping the terminals, in three steps an administrator runs by hand.

announce  A new CA is created. Terminals learn it from api/trust the next time they sign in, while the server keeps
          its certificate from the old CA: for a while they trust both.
switch    The server certificate is reissued by the new CA, with the same key. Terminals that learned it keep working;
          the old CA stays published until it is retired.
retire    Only the new CA is published and the old key is destroyed. Not before the date set at switch (30 days), so
          terminals switched off for a few weeks still pick up the new CA. One that missed the whole window is
          provisioned again by hand, as with a new terminal.

Every step is recorded in /var/lib/culinaryos/ca-rotation.json."""
import importlib.util, json, os, subprocess, sys, time
from pathlib import Path
CONFIG = Path('/etc/culinaryos')
LOG = Path('/var/lib/culinaryos/ca-rotation.json')
GRACE_DAYS = 30
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('culinary_renew', HERE / 'renew-tls.py')
renewal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(renewal)

def fingerprint(openssl, certificate):
    out = subprocess.run([openssl, 'x509', '-in', str(certificate), '-noout', '-fingerprint', '-sha256'], check=True, capture_output=True, text=True).stdout
    return out.strip().split('=', 1)[1].replace(':', '').lower()

def files(config):
    return {'ca': config / 'restaurant-ca.crt', 'key': config / 'secrets/restaurant-ca.key',
            'next': config / 'restaurant-ca-next.crt', 'next_key': config / 'secrets/restaurant-ca-next.key',
            'previous': config / 'restaurant-ca-previous.crt', 'previous_key': config / 'secrets/restaurant-ca-previous.key',
            'bundle': config / 'restaurant-ca-bundle.pem', 'profile': config / 'restaurant-connection.json'}

def publish(config, openssl, cas):
    """What api/trust serves and what the kiosk on this server pins: the bundle while two CAs coexist."""
    f = files(config)
    profile = json.loads(f['profile'].read_text(encoding='utf-8'))
    if len(cas) == 1:
        f['bundle'].unlink(missing_ok=True)
        profile['CaFile'] = str(cas[0])
    else:
        renewal.replace(f['bundle'], b''.join(c.read_bytes() for c in cas))
        os.chmod(f['bundle'], 0o644)
        profile['CaFile'] = str(f['bundle'])
    profile['CaSha256'] = ','.join(fingerprint(openssl, c) for c in cas)
    renewal.replace(f['profile'], json.dumps(profile, indent=2).encode())
    os.chmod(f['profile'], 0o644)

def record(log, step, **details):
    entries = json.loads(log.read_text(encoding='utf-8')) if log.exists() else []
    entries.append({'step': step, 'at': time.time(), **details})
    log.parent.mkdir(parents=True, exist_ok=True)
    renewal.replace(log, json.dumps(entries, indent=2).encode())
    return entries

def state(log):
    entries = json.loads(log.read_text(encoding='utf-8')) if log.exists() else []
    return entries[-1] if entries else {'step': 'none'}

def announce(config=CONFIG, openssl='openssl', log=LOG):
    f = files(config)
    if f['next'].exists() or f['previous'].exists():
        raise ValueError('Ya hay una rotación en curso: ' + state(log)['step'])
    site = json.loads(f['profile'].read_text(encoding='utf-8'))['SiteId']
    subprocess.run([openssl, 'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-noenc', '-days', '3650', '-sha256',
                    '-subj', '/CN=Culinary Restaurant ' + site + ' ' + time.strftime('%Y'), '-addext', 'basicConstraints=critical,CA:TRUE,pathlen:0',
                    '-addext', 'keyUsage=critical,keyCertSign,cRLSign', '-keyout', str(f['next_key']), '-out', str(f['next'])],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    os.chmod(f['next_key'], 0o600)
    os.chmod(f['next'], 0o644)
    publish(config, openssl, [f['ca'], f['next']])
    return record(log, 'announce', old=fingerprint(openssl, f['ca']), new=fingerprint(openssl, f['next']))

def switch(config=CONFIG, openssl='openssl', restart=renewal.restart, log=LOG, now=None):
    f = files(config)
    if not f['next'].exists():
        raise ValueError('Primero anuncie la CA nueva')
    moves = [(f['ca'], f['previous']), (f['key'], f['previous_key']), (f['next'], f['ca']), (f['next_key'], f['key'])]
    for src, dst in moves:
        os.replace(src, dst)
    try:
        # Same key, new issuer; renew-tls verifies the certificate against the CA now in place before writing it.
        renewal.renew(config, openssl, lambda: None, force=True)
    except Exception:
        for src, dst in reversed(moves):
            os.replace(dst, src)
        raise
    publish(config, openssl, [f['ca'], f['previous']])
    restart()
    retire_after = (now or time.time()) + GRACE_DAYS * 86400
    return record(log, 'switch', new=fingerprint(openssl, f['ca']), old=fingerprint(openssl, f['previous']), retire_after=retire_after)

def retire(config=CONFIG, openssl='openssl', log=LOG, now=None, force=False):
    f = files(config)
    if not f['previous'].exists():
        raise ValueError('No hay una CA anterior que retirar')
    last = state(log)
    if not force and (now or time.time()) < last.get('retire_after', 0):
        raise ValueError('La CA anterior se retira después del ' + time.strftime('%Y-%m-%d', time.gmtime(last['retire_after'])) + '; antes, use --force y reaprovisione las terminales que no se hayan conectado')
    old = fingerprint(openssl, f['previous'])
    publish(config, openssl, [f['ca']])
    f['previous_key'].unlink()
    f['previous'].unlink()
    return record(log, 'retire', retired=old, forced=force)

if __name__ == '__main__':
    steps = {'announce': announce, 'switch': switch, 'retire': lambda: retire(force='--force' in sys.argv), 'status': lambda: print(json.dumps(state(LOG), indent=2))}
    if len(sys.argv) < 2 or sys.argv[1] not in steps:
        print('Uso: rotate-ca.py announce|switch|retire [--force]|status', file=sys.stderr)
        raise SystemExit(2)
    try:
        result = steps[sys.argv[1]]()
        if isinstance(result, list):
            print('Paso ' + result[-1]['step'] + ' registrado en ' + str(LOG))
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        detail = getattr(error, 'stderr', None)
        print('No se pudo rotar la CA: ' + (detail.decode(errors='replace') if isinstance(detail, bytes) else detail or str(error)), file=sys.stderr)
        raise SystemExit(1)

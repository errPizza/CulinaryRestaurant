#!/usr/bin/python3
"""Renews the branch's TLS certificate with its local CA, from a daily timer. Terminals trust the CA, so they change nothing."""
import json, os, secrets, ssl, subprocess, sys, tempfile, time, urllib.parse
from pathlib import Path
CONFIG = Path('/etc/culinaryos')
BACKEND = 'culinaryos-backend.service'
# Five daily attempts before the app starts warning at 30 days (BranchService.CertificateWarning).
RENEW_SECONDS = 35 * 86400
VALID_DAYS = 365
def ends(openssl, certificate):
    out = subprocess.run([openssl, 'x509', '-in', str(certificate), '-noout', '-enddate'], check=True, capture_output=True, text=True).stdout
    return ssl.cert_time_to_seconds(out.strip().split('=', 1)[1])
def replace(path, data):
    # The backend reads this file when it starts: it sees the old certificate or the new one, never half.
    temp = path.with_name(path.name + '.new')
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)
def restart():
    subprocess.run(['systemctl', 'try-restart', BACKEND], check=True)
def renew(config=CONFIG, openssl='openssl', restart=restart, force=False):
    """True when it renewed. The server key stays, so a failure at any step leaves a working pair."""
    ca, ca_key, cert = config / 'restaurant-ca.crt', config / 'secrets/restaurant-ca.key', config / 'restaurant-tls.crt'
    key = config / 'secrets/restaurant-tls.key'
    now = time.time()
    if ends(openssl, ca) - now < 365 * 86400:
        print('La CA de la sucursal vence en menos de un año; rotarla según docs/CERTIFICADOS-LOCALES.md', file=sys.stderr)
    if not force and ends(openssl, cert) - now > RENEW_SECONDS:
        return False
    if ends(openssl, ca) - ends(openssl, cert) < 86400:
        raise ValueError('La CA vence antes de poder extender el certificado; hay que rotarla')
    profile = json.loads((config / 'restaurant-connection.json').read_text(encoding='utf-8'))
    name, address = urllib.parse.urlsplit(profile['ServerUrl']).hostname, profile['ConnectAddress']
    def tool(*args): subprocess.run([openssl, *map(str, args)], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    # Same directory as the keys: the temporary request and certificate stay root-only.
    with tempfile.TemporaryDirectory(prefix='culinary-tls-', dir=config / 'secrets') as tmp:
        tmp = Path(tmp)
        tool('req', '-new', '-key', key, '-subj', '/CN=' + name, '-out', tmp / 'server.csr')
        (tmp / 'extensions').write_text('basicConstraints=critical,CA:false\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:' + name + ',IP:' + address + '\n')
        days = min(VALID_DAYS, int((ends(openssl, ca) - now) // 86400))
        tool('x509', '-req', '-in', tmp / 'server.csr', '-CA', ca, '-CAkey', ca_key, '-set_serial', secrets.randbits(120) + 1, '-days', days, '-sha256', '-extfile', tmp / 'extensions', '-out', tmp / 'server.crt')
        tool('verify', '-purpose', 'sslserver', '-verify_hostname', name, '-CAfile', ca, tmp / 'server.crt')
        replace(cert, (tmp / 'server.crt').read_bytes())
    restart()
    print('Certificado TLS renovado hasta ' + time.strftime('%Y-%m-%d', time.gmtime(ends(openssl, cert))))
    return True
if __name__ == '__main__':
    try:
        renew()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        detail = getattr(error, 'stderr', None)
        print('No se pudo renovar el certificado TLS: ' + (detail.decode(errors='replace') if detail else str(error)), file=sys.stderr)
        raise SystemExit(1)

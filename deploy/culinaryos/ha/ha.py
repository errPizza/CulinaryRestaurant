#!/usr/bin/python3
"""Culinary Restaurant: the branch's second server, kept as a PostgreSQL 17 streaming standby.

Phase 1 of docs/PLAN-SERVIDOR-RESPALDO.md: pair two CulinaryOS machines, synchronous replication that stops waiting
for a standby that went away and waits again once it has caught up, status, and re-cloning a standby. Nothing here
promotes a standby; promotion arrives with fencing in phase 4.

    culinaryos-ha primary --peer 10.77.0.12     on the main server; prints the pairing code
    culinaryos-ha standby --primary 10.77.0.11  on the second machine; asks for the code
    culinaryos-ha status [--json]               on either
    culinaryos-ha rejoin                        on the standby, after it lost its place in the primary's WAL
    culinaryos-ha unpair                        on the primary
"""
import argparse
import datetime
import hashlib
import ipaddress
import json
import os
import re
import secrets
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path, PurePosixPath

CONFIG = Path('/etc/culinaryos')
SITE = CONFIG / 'site.json'
# Role and peer, no secrets: the watcher reads it as postgres.
PAIR = CONFIG / 'ha.json'
SECRET = CONFIG / 'secrets/ha-replica.json'
# While it exists the backend and its announcement do not start, whatever enables them.
STANDBY_MARK = PurePosixPath('/etc/culinaryos/ha-standby')
CA, CA_KEY = CONFIG / 'restaurant-ca.crt', CONFIG / 'secrets/restaurant-ca.key'
PG = PurePosixPath('/etc/postgresql/17/main')
DATA = Path('/var/lib/postgresql/17/main')
# include_dir reads conf.d in name order: this file comes after culinaryos.conf, so its listen_addresses wins.
HA_CONF = Path(PG / 'conf.d/zz-culinaryos-ha.conf')
HBA = Path(PG / 'pg_hba.conf')
CERTS = PG / 'culinary-ha'
STATUS = Path('/run/culinaryos-ha/status.json')
NFT = Path('/etc/nftables.conf')
SYSTEMD = Path('/etc/systemd/system')
POSTGRES, WATCHER = 'postgresql.service', 'culinary-ha-watch.service'
HELD = ('culinaryos-backend.service', 'culinaryos-publish.service')
SLOT = APPLICATION = 'culinary_standby'
ROLE = 'culinary_replica'
PORT = 5432
# The primary's walsender drops a silent standby after wal_sender_timeout (5 s); this much more without a standby and
# the primary stops waiting for it, so a dead standby holds sales for about ten seconds.
DEGRADE_AFTER = 5.0
# A standby streaming this long is waited for again.
RESYNC_AFTER = 3.0
ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
SECRET_LENGTH, PRINT_LENGTH = 20, 12
HBA_BEGIN, HBA_END = '# culinaryos-ha: inicio', '# culinaryos-ha: fin'
NFT_MARK = 'comment "culinaryos-ha"'

def ha_conf(certificates=True):
    # The same file on both machines: a promotion changes no setting.
    lines = ['# Culinary Restaurant: par de servidores. Lo escribe culinaryos-ha; es igual en los dos equipos.',
             # pg_hba and nftables admit only the peer. A fixed address would fail to bind if the network comes up late.
             "listen_addresses = '*'",
             'max_wal_senders = 4', 'max_replication_slots = 2', "max_slot_wal_keep_size = '2GB'",
             "wal_sender_timeout = '5s'", "wal_receiver_status_interval = '1s'", "wal_receiver_timeout = '10s'",
             'wal_log_hints = on', 'hot_standby = on', 'hot_standby_feedback = on']
    if certificates:
        lines += ['ssl = on', f"ssl_cert_file = '{CERTS / 'server.crt'}'", f"ssl_key_file = '{CERTS / 'server.key'}'", "ssl_min_protocol_version = 'TLSv1.3'"]
    return '\n'.join(lines) + '\n'

def without_hba(text):
    return re.sub(rf'(?ms)^{re.escape(HBA_BEGIN)}$.*?^{re.escape(HBA_END)}$\n?', '', text)

def with_hba(text, peer):
    # Last in the file: Debian's own lines only match the loopback, so nothing before shadows this one.
    return without_hba(text).rstrip('\n') + f'\n{HBA_BEGIN}\nhostssl replication {ROLE} {peer}/32 scram-sha-256\n{HBA_END}\n'

def without_firewall(rules):
    return ''.join(line for line in rules.splitlines(keepends=True) if NFT_MARK not in line)

def with_firewall(rules, interface, peer):
    if not re.fullmatch(r'[A-Za-z0-9_.:-]{1,15}', interface or ''):
        raise ValueError('Interfaz de red no válida en site.json')
    rules = without_firewall(rules)
    anchor = next((line for line in rules.splitlines(keepends=True) if 'tcp dport @application_ports accept' in line), None)
    if anchor is None:
        raise ValueError('Instala primero el firewall de sitio (culinaryos-runtime deploy-site)')
    indent = anchor[:len(anchor) - len(anchor.lstrip())]
    return rules.replace(anchor, anchor + f'{indent}iifname "{interface}" ip saddr {peer} tcp dport {PORT} accept {NFT_MARK}\n', 1)

def symbols(data, count):
    number = int.from_bytes(data, 'big')
    out = ''
    for _ in range(count):
        number, digit = divmod(number, len(ALPHABET))
        out += ALPHABET[digit]
    return out

def fingerprint(ca_pem):
    return symbols(hashlib.sha256(ssl.PEM_cert_to_DER_cert(ca_pem)).digest(), PRINT_LENGTH)

def new_password():
    return ''.join(secrets.choice(ALPHABET) for _ in range(SECRET_LENGTH))

def pairing_code(password, printed):
    # The replication password and the primary's CA in one code a person can type: 100 + 60 bits.
    code = password + printed
    return '-'.join(code[i:i + 4] for i in range(0, len(code), 4))

def parse_code(code):
    value = re.sub(r'[\s-]', '', code or '').upper()
    if len(value) != SECRET_LENGTH + PRINT_LENGTH or any(c not in ALPHABET for c in value):
        raise ValueError('Código de emparejamiento no válido: cópialo tal como lo muestra el equipo principal')
    return value[:SECRET_LENGTH], value[SECRET_LENGTH:]

def conninfo(host, ca_file, sslmode='verify-ca', port=PORT):
    # Values never need quoting: an address and fixed names and paths. The password travels apart.
    values = {'host': host, 'port': port, 'user': ROLE, 'sslmode': sslmode, 'sslrootcert': ca_file,
              'application_name': APPLICATION, 'connect_timeout': 5, 'keepalives_idle': 5, 'keepalives_interval': 2, 'keepalives_count': 3}
    pairs = [f'{key}={value}' for key, value in values.items() if value is not None]
    if any(re.search(r"[\s'\\]", pair) for pair in pairs):
        raise ValueError('Parámetro de conexión no válido')
    return ' '.join(pairs)

def standby_settings(auto_conf, info):
    # pg_basebackup copies the primary's postgresql.auto.conf. Its synchronous_standby_names would make a promoted
    # standby wait for a standby of its own that does not exist.
    kept = [line for line in auto_conf.splitlines() if not re.match(r'\s*(synchronous_standby_names|primary_conninfo|primary_slot_name)\s*=', line)]
    return '\n'.join(kept + [f"primary_conninfo = '{info}'", f"primary_slot_name = '{SLOT}'"]) + '\n'

def check_peer(address, site):
    try:
        peer = ipaddress.IPv4Address(address)
        network = ipaddress.IPv4Network(site['network'])
    except (ValueError, KeyError):
        raise ValueError('Escribe la dirección IPv4 del otro equipo')
    if peer not in network:
        raise ValueError(f'El otro equipo debe estar en la red de la sucursal ({network})')
    if str(peer) in (site.get('expected_address'), site.get('server_address')):
        raise ValueError('Esa dirección es la de este equipo o la del servicio, no la del otro equipo')
    return str(peer)

def decide(memory, synchronous, streaming, now):
    """What the primary does about its standby. memory carries when the standby last changed between healthy and not."""
    if memory.get('streaming') is not streaming:
        memory = {'streaming': streaming, 'since': now}
    elapsed = now - memory['since']
    if streaming and not synchronous and elapsed >= RESYNC_AFTER:
        return 'resync', memory
    if not streaming and synchronous and elapsed >= DEGRADE_AFTER:
        return 'degrade', memory
    return None, memory

def primary_state(synchronous, state):
    if synchronous:
        return 'sync' if state == 'streaming' else 'waiting'
    return 'catching-up' if state in ('streaming', 'catchup', 'startup') else 'degraded'

PRIMARY_QUERY = f"""WITH r AS (SELECT state, pg_wal_lsn_diff(pg_current_wal_lsn(), flush_lsn)::bigint AS lag FROM pg_stat_replication
WHERE application_name = '{APPLICATION}' ORDER BY state = 'streaming' DESC LIMIT 1), s AS (SELECT wal_status, active FROM pg_replication_slots WHERE slot_name = '{SLOT}')
SELECT current_setting('synchronous_standby_names'), (SELECT state FROM r), (SELECT lag FROM r), (SELECT wal_status FROM s), (SELECT active FROM s)"""
STANDBY_QUERY = """SELECT pg_is_in_recovery(), (SELECT status FROM pg_stat_wal_receiver), (SELECT extract(epoch FROM last_msg_receipt_time) FROM pg_stat_wal_receiver),
extract(epoch FROM pg_last_xact_replay_timestamp()), pg_wal_lsn_diff(pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn())::bigint"""

def set_synchronous(cluster, on):
    cluster.sql(f"ALTER SYSTEM SET synchronous_standby_names = 'FIRST 1 ({APPLICATION})'" if on else 'ALTER SYSTEM RESET synchronous_standby_names')
    cluster.sql('SELECT pg_reload_conf()')

def create_slot(cluster):
    cluster.sql(f"SELECT pg_create_physical_replication_slot('{SLOT}') WHERE NOT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = '{SLOT}')")

def replication_role(cluster, password):
    # Through psql's input, never its arguments. A new password locks out the standby that had the old one.
    existing = cluster.sql(f"SELECT 1 FROM pg_roles WHERE rolname = '{ROLE}'")
    cluster.sql(f"SET password_encryption = 'scram-sha-256';\n{'ALTER' if existing else 'CREATE'} ROLE {ROLE} WITH LOGIN REPLICATION PASSWORD '{password}';")
    create_slot(cluster)

def number(value):
    return None if value in (None, '') else float(value)

def tick(cluster, role, memory, now, log=print):
    if role == 'primary':
        names, state, lag, slot, active = cluster.sql(PRIMARY_QUERY)[0]
        synchronous = names != ''
        action, memory = decide(memory, synchronous, state == 'streaming', now)
        if action:
            set_synchronous(cluster, action == 'resync')
            synchronous = action == 'resync'
            log('COS-HA ' + ('el respaldo volvió a estar al día; las ventas esperan otra vez su confirmación' if synchronous else 'el respaldo no responde; las ventas dejan de esperarlo'))
        # A standby off for days loses its slot to max_slot_wal_keep_size. A new slot lets `rejoin` clone again.
        if slot == 'lost' and active != 't':
            cluster.sql(f"SELECT pg_drop_replication_slot('{SLOT}')")
            create_slot(cluster)
            log('COS-HA el respaldo perdió su lugar en el WAL; necesita culinaryos-ha rejoin')
        status = {'role': role, 'state': primary_state(synchronous, state), 'standby': state or None, 'lagBytes': number(lag), 'slot': slot or None}
        return status, memory
    recovering, receiver, received, replayed, pending = cluster.sql(STANDBY_QUERY)[0]
    fresh = receiver == 'streaming' and number(received) is not None and now - number(received) <= 5
    state = 'not-standby' if recovering != 't' else 'streaming' if fresh else 'disconnected'
    return {'role': role, 'state': state, 'lastReceivedAt': number(received), 'lastReplayedAt': number(replayed), 'pendingBytes': number(pending)}, memory

def write_status(status, file=STATUS):
    with tempfile.NamedTemporaryFile('w', encoding='utf-8', dir=file.parent, delete=False) as stream:
        json.dump(status, stream, ensure_ascii=False)
        temp = stream.name
    os.chmod(temp, 0o644)
    os.replace(temp, file)

def watch(cluster, role, once=False, clock=time.time, write=write_status):
    memory = {}
    while True:
        try:
            status, memory = tick(cluster, role, memory, clock())
        except Exception as error:
            status = {'role': role, 'state': 'error', 'message': str(error)[:300]}
        status['generated_at'] = clock()
        write(status)
        if once:
            return status
        time.sleep(1)

class Cluster:
    """Debian's 17/main cluster as CulinaryOS runs it: reached over its socket as postgres."""
    data = DATA

    def command(self, name, *args):
        tool = name if name == 'psql' else '/usr/lib/postgresql/17/bin/' + name
        return (['runuser', '-u', 'postgres', '--'] if os.geteuid() == 0 else []) + [tool, *map(str, args)]

    def sql(self, query):
        reply = subprocess.run(self.command('psql', '-X', '-q', '-v', 'ON_ERROR_STOP=1', '-At', '-F', '\t', '-d', 'postgres'), input=query, text=True, capture_output=True)
        if reply.returncode:
            raise RuntimeError(reply.stderr.strip() or 'psql falló')
        return [line.split('\t') for line in reply.stdout.splitlines()]

    def tool(self, name, *args, password=None):
        # The password goes in the environment: arguments are visible to every user in ps.
        reply = subprocess.run(self.command(name, *args), text=True, capture_output=True, env=dict(os.environ, PGPASSWORD=password) if password else None)
        if reply.returncode:
            raise RuntimeError(reply.stderr.strip() or name + ' falló')
        return reply.stdout

    def start(self): run('systemctl', 'start', POSTGRES)
    def stop(self): run('systemctl', 'stop', POSTGRES)
    def restart(self): run('systemctl', 'restart', POSTGRES)

def run(*args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, **kwargs)

def write_file(path, data, mode=0o644, owner=None):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile('wb', dir=path.parent, delete=False) as stream:
        stream.write(data.encode() if isinstance(data, str) else data)
        stream.flush()
        os.fsync(stream.fileno())
        temp = stream.name
    os.chmod(temp, mode)
    if owner:
        import pwd
        account = pwd.getpwnam(owner)
        os.chown(temp, account.pw_uid, account.pw_gid)
    os.replace(temp, path)

def probe(cluster, info, password):
    """Code, TLS and firewall, checked over a replication connection before this machine changes anything."""
    return cluster.tool('psql', '-X', '-At', '-d', info + ' replication=true dbname=replication', '-c', 'IDENTIFY_SYSTEM', password=password)

def clone(cluster, info, password):
    """A fresh copy of the primary in cluster.data, which must be empty, left ready to follow it."""
    cluster.tool('pg_basebackup', '-d', info, '-D', cluster.data, '-X', 'stream', '-S', SLOT, '-c', 'fast', '--no-password', password=password)
    auto = Path(cluster.data) / 'postgresql.auto.conf'
    owner = getattr(cluster, 'owner', 'postgres')
    # postgresql.auto.conf is the postgres account's own 0600 file: the one place the password is kept on the standby.
    write_file(auto, standby_settings(auto.read_text(encoding='utf-8') if auto.exists() else '', info + ' password=' + password), 0o600, owner)
    write_file(Path(cluster.data) / 'standby.signal', '', 0o600, owner)

def wait_streaming(cluster, timeout=120, clock=time.time):
    end = clock() + timeout
    while clock() < end:
        try:
            status, _ = tick(cluster, 'standby', {}, clock())
            if status['state'] == 'streaming':
                return status
        except RuntimeError:
            pass
        time.sleep(1)
    raise RuntimeError('El respaldo no empezó a recibir del principal; revisa red, firewall y el código')

def root():
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise PermissionError('Ejecuta culinaryos-ha como root en CulinaryOS')

def confirm(message, assume):
    print(message)
    if assume:
        return
    if not sys.stdin.isatty():
        raise PermissionError('Confirma desde la consola o con --yes')
    if input('Escribe SI para continuar: ').strip().upper() != 'SI':
        raise SystemExit('Cancelado; no se cambió nada.')

def read_site():
    site = json.loads(SITE.read_text(encoding='utf-8'))
    if site.get('role') != 'servidor' or site.get('local_only') is True:
        raise ValueError('Este equipo no es un servidor con la red de sucursal configurada (culinaryos-runtime deploy-site)')
    return site

def read_pair():
    return json.loads(PAIR.read_text(encoding='utf-8')) if PAIR.exists() else None

def certificate(site):
    """PostgreSQL's own server certificate, issued by this machine's restaurant CA for its own address."""
    crt, key = Path(CERTS / 'server.crt'), Path(CERTS / 'server.key')
    if crt.exists() and key.exists() and subprocess.run(['openssl', 'x509', '-in', str(crt), '-noout', '-checkend', str(60 * 86400)], stdout=subprocess.DEVNULL).returncode == 0:
        return
    with tempfile.TemporaryDirectory(prefix='culinary-ha-', dir='/run') as tmp:
        tmp = Path(tmp)
        def openssl(*args): run('openssl', *args, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        openssl('req', '-new', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-noenc', '-subj', '/CN=' + site['server_name'], '-keyout', tmp / 'server.key', '-out', tmp / 'server.csr')
        (tmp / 'extensions').write_text('basicConstraints=critical,CA:false\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:' + site['server_name'] + ',IP:' + site['expected_address'] + '\n')
        openssl('x509', '-req', '-in', tmp / 'server.csr', '-CA', CA, '-CAkey', CA_KEY, '-set_serial', str(secrets.randbits(120) + 1), '-days', '1825', '-sha256', '-extfile', tmp / 'extensions', '-out', tmp / 'server.crt')
        write_file(key, (tmp / 'server.key').read_bytes(), 0o600, 'postgres')
        write_file(crt, (tmp / 'server.crt').read_bytes(), 0o644, 'postgres')

def apply_firewall(rules):
    with tempfile.NamedTemporaryFile('w', dir='/run', suffix='.nft', delete=False) as stream:
        stream.write(rules)
        name = stream.name
    try:
        run('/usr/local/sbin/culinaryos-firewall-apply', name)
    finally:
        Path(name).unlink(missing_ok=True)

def hold_application(on):
    """On a standby the backend and its announcement stay down: the database is read-only and the branch has one server."""
    for unit in HELD:
        dropin = SYSTEMD / (unit + '.d') / 'zz-culinaryos-ha.conf'
        if on:
            write_file(dropin, f'[Unit]\nConditionPathExists=!{STANDBY_MARK}\n')
        else:
            dropin.unlink(missing_ok=True)
    if on:
        write_file(STANDBY_MARK, 'Este equipo es el respaldo de la sucursal; la aplicación corre en el principal.\n')
        subprocess.run(['systemctl', 'disable', '--now', *HELD], check=False)
    else:
        Path(STANDBY_MARK).unlink(missing_ok=True)
    run('systemctl', 'daemon-reload')
    if not on:
        subprocess.run(['systemctl', 'enable', '--now', *HELD], check=False)

def aside_name(label):
    return DATA.with_name(DATA.name + '.' + label + '-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ'))

def set_aside(label):
    aside = aside_name(label)
    os.rename(DATA, aside)
    DATA.mkdir(mode=0o700)
    import pwd
    account = pwd.getpwnam('postgres')
    os.chown(DATA, account.pw_uid, account.pw_gid)
    return aside

def fetch_ca(primary, printed, port):
    # Nothing is trusted yet: the CA the primary publishes is taken only if it matches the print in the code.
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    with urllib.request.urlopen(f'https://{primary}:{port}/api/trust', context=context, timeout=10) as reply:
        cas = json.loads(reply.read().decode('utf-8')).get('cas') or []
    for pem in cas:
        try:
            if secrets.compare_digest(fingerprint(pem), printed):
                return pem
        except ValueError:
            continue
    raise ValueError('La CA del equipo principal no coincide con el código; revisa la dirección y vuelve a copiar el código')

def cmd_primary(args):
    root()
    site = read_site()
    peer = check_peer(args.peer, site)
    pair = read_pair()
    if pair and pair.get('role') != 'primary':
        raise ValueError('Este equipo ya es el respaldo de otro; no puede ser principal')
    rules = with_firewall(NFT.read_text(encoding='utf-8'), site.get('interface'), peer)
    confirm('PostgreSQL se reiniciará para aceptar al respaldo: unos segundos sin vender. Hazlo fuera de servicio.'
            + (' El código nuevo desconecta al respaldo actual hasta que se empareje otra vez.' if pair else ''), args.yes)
    cluster = Cluster()
    password = new_password()
    certificate(site)
    write_file(SECRET, json.dumps({'password': password, 'peer': peer}) + '\n', 0o600)
    write_file(HA_CONF, ha_conf())
    write_file(HBA, with_hba(HBA.read_text(encoding='utf-8'), peer), 0o640, 'postgres')
    replication_role(cluster, password)
    cluster.restart()
    apply_firewall(rules)
    write_file(PAIR, json.dumps({'version': 1, 'role': 'primary', 'peer': peer, 'pairedAt': datetime.datetime.now(datetime.timezone.utc).isoformat()}, indent=2) + '\n')
    run('systemctl', 'enable', '--now', WATCHER)
    print('Equipo principal listo. En el respaldo ejecuta:')
    print(f'  sudo culinaryos-ha standby --primary {site["expected_address"]}')
    print('y escribe este código cuando lo pida: ' + pairing_code(password, fingerprint(CA.read_text(encoding='ascii'))))
    print('El código sólo sirve para este emparejamiento; uno nuevo invalida el anterior.')

def cmd_standby(args):
    root()
    site = read_site()
    primary = check_peer(args.primary, site)
    # Asked for, not taken as an argument: it carries the replication password, and arguments show in ps and history.
    if args.code is None and not sys.stdin.isatty():
        raise PermissionError('Escribe el código desde la consola')
    password, printed = parse_code(args.code if args.code is not None else input('Código de emparejamiento: '))
    if read_pair():
        raise ValueError('Este equipo ya está en un par; para volver a copiar usa culinaryos-ha rejoin')
    ca = fetch_ca(primary, printed, (site.get('application') or {}).get('port', 8443))
    write_file(CERTS / 'primary-ca.crt', ca, 0o644, 'postgres')
    info = conninfo(primary, CERTS / 'primary-ca.crt')
    cluster = Cluster()
    probe(cluster, info, password)
    confirm('Este equipo dejará de ser servidor propio y pasará a copiar al principal. Su base actual se guarda aparte, sin borrarse.', args.yes)
    aside = None
    try:
        hold_application(True)
        cluster.stop()
        aside = set_aside('antes-del-par')
        certificate(site)
        write_file(HA_CONF, ha_conf())
        clone(cluster, info, password)
        cluster.start()
        wait_streaming(cluster)
    except BaseException:
        # Ctrl+C included: the machine goes back to what it was, its own database and its application.
        subprocess.run(['systemctl', 'stop', POSTGRES], check=False)
        if aside is not None:
            os.rename(DATA, aside_name('copia-fallida'))
            os.rename(aside, DATA)
        HA_CONF.unlink(missing_ok=True)
        cluster.start()
        hold_application(False)
        raise
    write_file(SECRET, json.dumps({'password': password, 'peer': primary}) + '\n', 0o600)
    write_file(PAIR, json.dumps({'version': 1, 'role': 'standby', 'peer': primary, 'pairedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'previousData': str(aside)}, indent=2) + '\n')
    run('systemctl', 'enable', '--now', WATCHER)
    print('Respaldo emparejado: copia al principal en tiempo real. Su base anterior quedó en ' + str(aside))

def cmd_rejoin(args):
    root()
    pair = read_pair()
    if not pair or pair.get('role') != 'standby':
        raise ValueError('Sólo un respaldo se vuelve a copiar del principal')
    password = json.loads(SECRET.read_text(encoding='utf-8'))['password']
    info = conninfo(pair['peer'], CERTS / 'primary-ca.crt')
    cluster = Cluster()
    probe(cluster, info, password)
    confirm('Se descarta la copia actual del respaldo y se vuelve a copiar todo del principal.', args.yes)
    cluster.stop()
    stale = set_aside('copia-anterior')
    clone(cluster, info, password)
    cluster.start()
    wait_streaming(cluster)
    # The stale copy holds nothing the primary lacks; it goes once the new one streams.
    run('rm', '-rf', '--one-file-system', stale)
    print('Respaldo copiado de nuevo y al día.')

def cmd_unpair(args):
    root()
    pair = read_pair()
    if not pair or pair.get('role') != 'primary':
        raise ValueError('Sólo se deshace el par desde el equipo principal')
    confirm('El principal deja de copiar al respaldo y PostgreSQL se reinicia: unos segundos sin vender.', args.yes)
    cluster = Cluster()
    set_synchronous(cluster, False)
    cluster.sql(f"SELECT pg_terminate_backend(pid) FROM pg_stat_replication WHERE application_name = '{APPLICATION}'")
    time.sleep(1)
    cluster.sql(f"SELECT pg_drop_replication_slot('{SLOT}') FROM pg_replication_slots WHERE slot_name = '{SLOT}'")
    cluster.sql(f'DROP ROLE IF EXISTS {ROLE}')
    subprocess.run(['systemctl', 'disable', '--now', WATCHER], check=False)
    write_file(HBA, without_hba(HBA.read_text(encoding='utf-8')), 0o640, 'postgres')
    HA_CONF.unlink(missing_ok=True)
    cluster.restart()
    apply_firewall(without_firewall(NFT.read_text(encoding='utf-8')))
    for path in (PAIR, SECRET, STATUS):
        path.unlink(missing_ok=True)
    print('Par deshecho. El respaldo conserva su copia; para reutilizarlo como servidor propio, llama a soporte.')

LABELS = {'sync': 'al día: cada venta queda en los dos equipos', 'waiting': 'el respaldo no responde; las ventas lo esperan unos segundos',
          'catching-up': 'el respaldo se está poniendo al día', 'degraded': 'SIN RESPALDO: el otro equipo no responde',
          'streaming': 'copiando al principal en tiempo real', 'disconnected': 'SIN CONEXIÓN con el principal',
          'not-standby': 'esta base ya no es una copia; llama a soporte', 'error': 'el vigilante no pudo leer PostgreSQL'}

def cmd_status(args):
    pair = read_pair()
    if not pair:
        print(json.dumps({'role': 'unpaired'}) if args.json else 'Este equipo no está en un par de servidores.')
        return
    try:
        status = json.loads(STATUS.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        status = {'role': pair['role'], 'state': 'error', 'message': 'El vigilante no ha escrito su estado'}
    if time.time() - status.get('generated_at', 0) > 30:
        status['stale'] = True
    if args.json:
        print(json.dumps(status, ensure_ascii=False))
        return
    print(('Equipo principal' if pair['role'] == 'primary' else 'Equipo de respaldo') + ' · el otro equipo: ' + pair['peer'])
    print('Estado: ' + LABELS.get(status.get('state'), status.get('state', '?')) + (' (estado viejo: el vigilante no informa)' if status.get('stale') else ''))
    if status.get('lastReceivedAt'):
        print('Último dato recibido: ' + datetime.datetime.fromtimestamp(status['lastReceivedAt']).strftime('%H:%M:%S'))
    if status.get('message'):
        print(status['message'])

def cmd_watch(args):
    pair = read_pair()
    if not pair:
        raise SystemExit(0)
    watch(Cluster(), pair['role'])

def main(argv=None):
    parser = argparse.ArgumentParser(prog='culinaryos-ha', description='Par de servidores de la sucursal (respaldo en caliente).')
    commands = parser.add_subparsers(dest='command', required=True)
    primary = commands.add_parser('primary', help='prepara este equipo como principal y muestra el código')
    primary.add_argument('--peer', required=True, help='IP del equipo de respaldo')
    standby = commands.add_parser('standby', help='convierte este equipo en respaldo del principal')
    standby.add_argument('--primary', required=True, help='IP del equipo principal')
    standby.add_argument('--code', help=argparse.SUPPRESS)
    commands.add_parser('rejoin', help='vuelve a copiar todo del principal')
    commands.add_parser('unpair', help='deshace el par desde el principal')
    status = commands.add_parser('status', help='estado del par')
    status.add_argument('--json', action='store_true')
    commands.add_parser('watch', help='vigilante (lo corre systemd)')
    for command in (primary, standby, commands.choices['rejoin'], commands.choices['unpair']):
        command.add_argument('--yes', action='store_true', help='no pedir confirmación')
    args = parser.parse_args(argv)
    handlers = {'primary': cmd_primary, 'standby': cmd_standby, 'rejoin': cmd_rejoin, 'unpair': cmd_unpair, 'status': cmd_status, 'watch': cmd_watch}
    try:
        handlers[args.command](args)
    except (ValueError, PermissionError, RuntimeError, OSError, subprocess.CalledProcessError) as error:
        print('culinaryos-ha: ' + str(error), file=sys.stderr)
        raise SystemExit(1)

if __name__ == '__main__':
    main()

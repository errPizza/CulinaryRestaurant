"""The pair of servers (deploy/culinaryos/ha/ha.py): the files it writes, the watcher's decisions, and, when the lab's
PostgreSQL 17 is present (tools/Get-Postgres.ps1), a real primary and standby on this machine."""
import importlib.util
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('culinary_ha', ROOT / 'deploy/culinaryos/ha/ha.py')
ha = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ha)
BIN = ROOT / '.tools/postgres/pgsql/bin'
SITE = {'site_id': 'local-01', 'role': 'servidor', 'network': '10.77.0.0/24', 'expected_address': '10.77.0.11', 'server_address': '10.77.0.10', 'server_name': 'culinaryos.internal', 'interface': 'enp1s0'}
# A public test CA; its key was never kept.
CA_PEM = '''-----BEGIN CERTIFICATE-----
MIIBnzCCAUWgAwIBAgIUXGhh0YhjZ6GBkPHkspsPPx3aef0wCgYIKoZIzj0EAwIw
JTEjMCEGA1UEAwwaQ3VsaW5hcnkgUmVzdGF1cmFudCBwcnVlYmEwHhcNMjYxMTA2
MTkyOTE4WhcNMzYxMTAzMTkyOTE4WjAlMSMwIQYDVQQDDBpDdWxpbmFyeSBSZXN0
YXVyYW50IHBydWViYTBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABANlgAfCXaZC
23nOwsIWyCP7e6zWa5fTXp33s3/yYvDeQmyABBd1W41auc10PRZxUFMc5LTJcU6U
R/Xu89tc+J2jUzBRMB0GA1UdDgQWBBTk+5VfDHliDprUDghMVg5Vc05K8jAfBgNV
HSMEGDAWgBTk+5VfDHliDprUDghMVg5Vc05K8jAPBgNVHRMBAf8EBTADAQH/MAoG
CCqGSM49BAMCA0gAMEUCIQD0GbeFNY1HgwSc9zCGrc00HjnO0HB/PqqjkxdEZRMG
2AIgJyw5+Lu1YG47b+D3SpN2kdkW211lKW3NhCidq5PpFkw=
-----END CERTIFICATE-----
'''
RULES = '''table inet culinaryos {
    set application_ports {
        type inet_service;
        elements = { 8443 };
    }
    chain input {
        type filter hook input priority filter; policy drop;
        iifname "enp1s0" ip saddr 10.77.0.0/24 tcp dport @application_ports accept
        limit rate 4/second burst 8 packets log prefix "CulinaryOs drop: " level info
    }
}
'''

class Files(unittest.TestCase):
    def test_same_settings_on_both_machines(self):
        text = ha.ha_conf()
        for line in ("listen_addresses = '*'", "wal_sender_timeout = '5s'", 'wal_log_hints = on', "ssl_cert_file = '/etc/postgresql/17/main/culinary-ha/server.crt'"):
            self.assertIn(line, text)
        self.assertNotIn('synchronous_standby_names', text, 'only the watcher turns waiting on, once a standby streams')
        self.assertNotIn('ssl', ha.ha_conf(certificates=False))
        self.assertGreater(ha.HA_CONF.name, 'culinaryos.conf', 'loaded after culinaryos.conf, whose listen_addresses is empty')

    def test_hba_block_is_replaced_and_removed(self):
        base = 'local all postgres peer\nhost replication all 127.0.0.1/32 scram-sha-256\n'
        once = ha.with_hba(base, '10.77.0.12')
        self.assertTrue(once.startswith(base))
        self.assertIn('hostssl replication culinary_replica 10.77.0.12/32 scram-sha-256', once)
        twice = ha.with_hba(once, '10.77.0.13')
        self.assertEqual(twice.count(ha.HBA_BEGIN), 1)
        self.assertNotIn('10.77.0.12', twice)
        self.assertEqual(ha.without_hba(twice).rstrip('\n'), base.rstrip('\n'))

    def test_firewall_admits_only_the_peer(self):
        rules = ha.with_firewall(RULES, 'enp1s0', '10.77.0.12')
        line = next(x for x in rules.splitlines() if ha.NFT_MARK in x)
        self.assertEqual(line.strip(), 'iifname "enp1s0" ip saddr 10.77.0.12 tcp dport 5432 accept comment "culinaryos-ha"')
        self.assertLess(rules.index(ha.NFT_MARK), rules.index('limit rate'), 'before the drop log, inside the input chain')
        self.assertEqual(ha.with_firewall(rules, 'enp1s0', '10.77.0.13').count(ha.NFT_MARK), 1)
        self.assertEqual(ha.without_firewall(rules), RULES)
        with self.assertRaises(ValueError):
            ha.with_firewall('table inet culinaryos {}\n', 'enp1s0', '10.77.0.12')
        with self.assertRaises(ValueError):
            ha.with_firewall(RULES, 'eth0"; drop', '10.77.0.12')

    def test_pairing_code(self):
        password = ha.new_password()
        code = ha.pairing_code(password, 'ABCDEFGHJKLM')
        self.assertEqual(len(code), 32 + 7)
        self.assertEqual(ha.parse_code(' ' + code.lower().replace('-', ' ') + '\n'), (password, 'ABCDEFGHJKLM'))
        for bad in ('', code[:-1], code.replace(code[0], '0', 1), code + 'A'):
            with self.subTest(code=bad), self.assertRaises(ValueError):
                ha.parse_code(bad)

    def test_fingerprint_follows_the_certificate(self):
        printed = ha.fingerprint(CA_PEM)
        self.assertEqual(len(printed), ha.PRINT_LENGTH)
        self.assertTrue(set(printed) <= set(ha.ALPHABET))
        self.assertEqual(printed, ha.fingerprint(CA_PEM.replace('\n', '\r\n')), 'the certificate, not its line endings')

    def test_conninfo_carries_no_password(self):
        info = ha.conninfo('10.77.0.11', '/etc/postgresql/17/main/culinary-ha/primary-ca.crt')
        self.assertIn('sslmode=verify-ca', info)
        self.assertIn('application_name=culinary_standby', info)
        self.assertNotIn('password', info)
        with self.assertRaises(ValueError):
            ha.conninfo("10.77.0.11' host=evil", None)

    def test_standby_never_inherits_waiting(self):
        copied = "# Do not edit\nsynchronous_standby_names = 'FIRST 1 (culinary_standby)'\nprimary_conninfo = 'old'\nwork_mem = '8MB'\n"
        text = ha.standby_settings(copied, 'host=10.77.0.11 password=X')
        self.assertNotIn('synchronous_standby_names', text)
        self.assertIn("work_mem = '8MB'", text)
        self.assertEqual(text.count('primary_conninfo'), 1)
        self.assertIn("primary_slot_name = 'culinary_standby'", text)

    def test_peer_must_be_another_machine_of_the_branch(self):
        self.assertEqual(ha.check_peer('10.77.0.12', SITE), '10.77.0.12')
        for bad in ('10.77.0.11', '10.77.0.10', '192.168.1.5', 'servidor', ''):
            with self.subTest(peer=bad), self.assertRaises(ValueError):
                ha.check_peer(bad, SITE)

class Decisions(unittest.TestCase):
    def test_waits_for_a_streaming_standby_after_three_seconds(self):
        action, memory = ha.decide({}, False, True, 100)
        self.assertIsNone(action)
        self.assertIsNone(ha.decide(memory, False, True, 102.9)[0])
        self.assertEqual(ha.decide(memory, False, True, 103)[0], 'resync')

    def test_stops_waiting_for_a_missing_standby(self):
        action, memory = ha.decide({}, True, False, 100)
        self.assertIsNone(action)
        self.assertIsNone(ha.decide(memory, True, False, 104.9)[0])
        self.assertEqual(ha.decide(memory, True, False, 105)[0], 'degrade')

    def test_a_flapping_standby_restarts_the_count(self):
        _, memory = ha.decide({}, True, False, 100)
        _, memory = ha.decide(memory, True, True, 104)
        _, memory = ha.decide(memory, True, False, 104.5)
        self.assertIsNone(ha.decide(memory, True, False, 109)[0])
        self.assertEqual(ha.decide(memory, True, False, 109.5)[0], 'degrade')

    def test_states(self):
        self.assertEqual(ha.primary_state(True, 'streaming'), 'sync')
        self.assertEqual(ha.primary_state(True, ''), 'waiting')
        self.assertEqual(ha.primary_state(False, 'catchup'), 'catching-up')
        self.assertEqual(ha.primary_state(False, ''), 'degraded')

class FakeCluster:
    def __init__(self, row):
        self.row, self.statements = row, []

    def sql(self, query):
        self.statements.append(query)
        return [self.row] if query == ha.PRIMARY_QUERY else []

class Watcher(unittest.TestCase):
    def test_degrades_and_reports(self):
        cluster = FakeCluster(['FIRST 1 (culinary_standby)', '', '', 'reserved', 'f'])
        status, memory = ha.tick(cluster, 'primary', {}, 100, log=lambda _: None)
        self.assertEqual(status['state'], 'waiting')
        status, _ = ha.tick(cluster, 'primary', memory, 106, log=lambda _: None)
        self.assertEqual(status['state'], 'degraded')
        self.assertIn('ALTER SYSTEM RESET synchronous_standby_names', cluster.statements)
        self.assertIn('SELECT pg_reload_conf()', cluster.statements)

    def test_a_lost_slot_is_made_again(self):
        cluster = FakeCluster(['', '', '', 'lost', 'f'])
        ha.tick(cluster, 'primary', {}, 100, log=lambda _: None)
        self.assertTrue(any('pg_drop_replication_slot' in x for x in cluster.statements))
        self.assertTrue(any('pg_create_physical_replication_slot' in x for x in cluster.statements))

    def test_a_failing_read_is_a_report(self):
        class Broken:
            def sql(self, query): raise RuntimeError('socket ausente')
        written = []
        status = ha.watch(Broken(), 'primary', once=True, clock=lambda: 50.0, write=written.append)
        self.assertEqual((status['state'], status['generated_at']), ('error', 50.0))
        self.assertEqual(written, [status])

def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

class LocalCluster(ha.Cluster):
    """The lab's PostgreSQL on this machine, standing in for Debian's 17/main."""
    owner = None

    def __init__(self, folder):
        self.data, self.port, self.log = folder / 'data', free_port(), folder / 'server.log'

    def command(self, name, *args):
        return [str(BIN / name), *map(str, args)]

    def sql(self, query):
        reply = subprocess.run(self.command('psql', '-X', '-q', '-v', 'ON_ERROR_STOP=1', '-At', '-F', '\t', '-h', '127.0.0.1', '-p', self.port, '-U', 'culinaryos', '-d', 'postgres'), input=query, text=True, capture_output=True, timeout=60)
        if reply.returncode:
            raise RuntimeError(reply.stderr.strip())
        return [line.split('\t') for line in reply.stdout.splitlines()]

    def pg_ctl(self, *args):
        # Output to a file: on Windows a captured pipe stays open in the server pg_ctl leaves running.
        subprocess.run(self.command('pg_ctl', '-D', self.data, '-l', self.log, '-o', f'-p {self.port}', '-w', '-t', '60', *args), check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=90)

    def start(self): self.pg_ctl('start')
    def stop(self, mode='fast'): self.pg_ctl('-m', mode, 'stop')
    def restart(self): self.pg_ctl('restart')

@unittest.skipUnless((BIN / 'pg_basebackup').with_suffix('.exe' if os.name == 'nt' else '').exists(), 'PostgreSQL del laboratorio ausente (tools/Get-Postgres.ps1)')
class RealPair(unittest.TestCase):
    """A primary and its standby: synchronous, a standby that dies, the primary that keeps selling, and a fresh copy."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix='culinary-ha-'))
        self.primary, self.standby = LocalCluster(self.tmp / 'primary'), LocalCluster(self.tmp / 'standby')
        self.primary.data.parent.mkdir()
        self.standby.data.parent.mkdir()
        subprocess.run(self.primary.command('initdb', '-D', self.primary.data, '-U', 'culinaryos', '-A', 'trust', '-E', 'UTF8', '--locale=C', '--no-instructions'), check=True, capture_output=True)
        with open(self.primary.data / 'postgresql.conf', 'a', encoding='utf-8') as conf:
            conf.write(ha.ha_conf(certificates=False) + "listen_addresses = '127.0.0.1'\nsynchronous_commit = on\n")
        # First, ahead of initdb's trust lines: the replication role needs its password, as on CulinaryOS.
        hba = self.primary.data / 'pg_hba.conf'
        hba.write_text(f'host replication {ha.ROLE} 127.0.0.1/32 scram-sha-256\n' + hba.read_text(encoding='utf-8'), encoding='utf-8')
        self.primary.start()

    def tearDown(self):
        for cluster in (self.standby, self.primary):
            if (cluster.data / 'postmaster.pid').exists():
                try:
                    cluster.stop('immediate')
                except subprocess.SubprocessError:
                    pass
        shutil.rmtree(self.tmp, ignore_errors=True)

    def until(self, role, cluster, state, memory, timeout=30):
        end = time.time() + timeout
        while time.time() < end:
            status, memory = ha.tick(cluster, role, memory, time.time(), log=lambda _: None)
            if status['state'] == state:
                return status, memory
            time.sleep(0.5)
        self.fail(f'{role} no llegó a {state}: {status}')

    def test_pair(self):
        password = ha.new_password()
        ha.replication_role(self.primary, password)
        info = ha.conninfo('127.0.0.1', None, sslmode='disable', port=self.primary.port)
        with self.assertRaises(RuntimeError, msg='a wrong code is caught before anything changes'):
            ha.probe(self.standby, info, ha.new_password())
        self.assertRegex(ha.probe(self.standby, info, password), r'^\d+\|1\|', 'IDENTIFY_SYSTEM answers: system id and timeline')
        with self.assertRaises(RuntimeError, msg='a wrong code copies nothing'):
            ha.clone(self.standby, info, ha.new_password())
        shutil.rmtree(self.standby.data, ignore_errors=True)
        ha.clone(self.standby, info, password)
        self.assertNotIn(password, info)
        self.assertTrue((self.standby.data / 'standby.signal').exists())
        self.standby.start()
        ha.wait_streaming(self.standby, timeout=60)
        status, memory = self.until('primary', self.primary, 'sync', {})
        self.assertEqual(self.primary.sql('SHOW synchronous_standby_names'), [['FIRST 1 (culinary_standby)']])
        self.primary.sql('CREATE TABLE venta (folio int PRIMARY KEY); INSERT INTO venta VALUES (1)')
        self.assertEqual(self.standby.sql('SELECT folio FROM venta'), [['1']], 'a commit the primary confirmed is already on the standby')
        # The standby dies. The next sale waits for it until the watcher stops waiting.
        self.standby.stop('immediate')
        sold = threading.Event()
        started = time.time()
        threading.Thread(target=lambda: (self.primary.sql('INSERT INTO venta VALUES (2)'), sold.set()), daemon=True).start()
        self.assertFalse(sold.wait(1.5), 'synchronous: the sale waits for the standby')
        status, memory = self.until('primary', self.primary, 'degraded', memory)
        self.assertTrue(sold.wait(10), 'once degraded the waiting sale goes through')
        self.assertLess(time.time() - started, 15)
        self.primary.sql('INSERT INTO venta VALUES (3)')
        # Back: it catches up from its slot and the primary waits for it again.
        self.standby.start()
        ha.wait_streaming(self.standby, timeout=60)
        status, memory = self.until('primary', self.primary, 'sync', memory)
        self.assertEqual(self.standby.sql('SELECT count(*) FROM venta'), [['3']])
        # A fresh copy over the old one (rejoin), while the primary waits: degraded first, then synchronous again.
        self.standby.stop('immediate')
        status, memory = self.until('primary', self.primary, 'degraded', memory)
        self.primary.sql('INSERT INTO venta VALUES (4)')
        shutil.rmtree(self.standby.data)
        ha.clone(self.standby, info, password)
        self.standby.start()
        ha.wait_streaming(self.standby, timeout=60)
        self.until('primary', self.primary, 'sync', memory)
        self.assertEqual(self.standby.sql('SELECT max(folio) FROM venta'), [['4']])
        with self.assertRaises(RuntimeError, msg='the standby is read-only'):
            self.standby.sql('INSERT INTO venta VALUES (5)')

if __name__ == '__main__':
    unittest.main()

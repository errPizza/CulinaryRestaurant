"""The Linux update as a transaction: each stage cut off on purpose, and the branch always left with a version that runs."""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('culinary_apply', ROOT / 'deploy/culinaryos/update/apply.py')
apply = importlib.util.module_from_spec(spec)
spec.loader.exec_module(apply)

class UpdateTransaction(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = Path(self.tmp.name)
        spool = base / 'spool'
        (spool / 'inbox').mkdir(parents=True)
        app = base / 'opt/culinaryos-app'
        (app / 'backend').mkdir(parents=True)
        (app / 'backend/MesaNexo.Api').write_text('version 0.3.03')
        (app / 'renew-tls.py').write_text('old')
        site = base / 'site.json'
        site.write_text(json.dumps({'site_id': 'sucursal-1'}))
        self.paths = dict(SPOOL=spool, INBOX=spool / 'inbox', WORK=spool / 'work', STATE=spool / 'state.json', PREVIOUS=spool / 'previous', APP=app, SITE=site)
        self.paths['BACKUPS'] = spool / 'backups'
        self.patches = [patch.object(apply, k, v) for k, v in self.paths.items()]
        # The branch database is 10 MB; pg_dump runs as postgres through runuser (faked below).
        self.patches.append(patch.object(apply, 'database_bytes', lambda: 10 * 2**20))
        # The appliance is Linux; on Windows the flag does not exist.
        self.patches.append(patch.object(apply.os, 'O_NOFOLLOW', getattr(apply.os, 'O_NOFOLLOW', 0), create=True))
        for p in self.patches:
            p.start()
        self.calls = []
        self.verify_ok, self.dpkg_ok, self.configure_ok = True, True, True
    def tearDown(self):
        for p in self.patches:
            p.stop()
        self.tmp.cleanup()
    def drop(self, offer=True, package=True):
        if offer:
            (apply.INBOX / 'offer.json').write_text(json.dumps({'version': '0.3.04'}))
        if package:
            (apply.INBOX / 'package.deb').write_bytes(b'!<arch>\n')
        (apply.INBOX / 'ready').write_text('')
    def fake(self, args, **kwargs):
        self.calls.append(args[0] if args[0] != 'dpkg' else 'dpkg ' + args[1])
        if args[0] == 'runuser':
            assert 'pg_dump' in args and kwargs.get('stdout') is not None
            kwargs['stdout'].write(b'PGDMP custom dump')
        elif args[0] == apply.API:
            if not self.verify_ok:
                raise subprocess.CalledProcessError(4, args)
        elif args[:2] == ['dpkg', '-i']:
            # dpkg replaces files one by one: a failure leaves the application half new.
            (apply.APP / 'backend/MesaNexo.Api').write_text('version 0.3.04')
            if not self.dpkg_ok:
                raise subprocess.CalledProcessError(1, args)
            (apply.APP / 'renew-tls.py').write_text('new')
        elif args[:2] == ['dpkg', '--configure']:
            if not self.configure_ok:
                raise subprocess.CalledProcessError(1, args)
            (apply.APP / 'renew-tls.py').write_text('new')
        return subprocess.CompletedProcess(args, 0)
    def version(self):
        return (apply.APP / 'backend/MesaNexo.Api').read_text(), (apply.APP / 'renew-tls.py').read_text()
    def stages(self):
        return [h['stage'] for h in apply.state().get('history', [])]

    def test_complete_update_then_healthy(self):
        self.drop()
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.main()
        self.assertEqual(self.stages(), ['received', 'verified', 'backup', 'snapshot', 'installing', 'installed'])
        dumps = list(apply.BACKUPS.glob('*.dump'))
        self.assertEqual(len(dumps), 1, 'the branch database is dumped before anything is installed')
        self.assertEqual(dumps[0].read_bytes(), b'PGDMP custom dump')
        self.assertIn('0.3.04', dumps[0].name)
        self.assertEqual(self.version(), ('version 0.3.04', 'new'))
        self.assertTrue(apply.PREVIOUS.exists(), 'the previous version is kept until the new one proves healthy')
        self.assertEqual(list(apply.INBOX.iterdir()), [])
        with patch.object(apply, 'healthy', return_value=True):
            apply.health()
        self.assertEqual(apply.state()['stage'], 'healthy')
        self.assertFalse(apply.PREVIOUS.exists())

    def test_interrupted_download(self):
        self.drop(package=False)
        with patch.object(apply, 'run', side_effect=self.fake), self.assertRaises(FileNotFoundError):
            apply.main()
        self.assertEqual((self.calls, self.version(), list(apply.INBOX.iterdir())), ([], ('version 0.3.03', 'old'), []))
        self.assertEqual(apply.state()['stage'], 'none')

    def test_failed_verification_installs_nothing(self):
        self.drop()
        self.verify_ok = False
        with patch.object(apply, 'run', side_effect=self.fake), self.assertRaises(subprocess.CalledProcessError):
            apply.main()
        self.assertEqual((self.calls, self.version()), ([apply.API], ('version 0.3.03', 'old')))
        self.assertEqual(apply.state()['stage'], 'failed', 'a refused offer ends the update so no screen keeps waiting')
        self.assertIn('no más nueva', apply.state()['reason'])
        self.assertFalse(apply.WORK.exists(), 'the private copy of the package does not stay behind')

    def test_interrupted_snapshot_is_redone(self):
        stale = apply.PREVIOUS.with_name('previous.new')
        stale.mkdir()
        (stale / 'partial').write_text('cut')
        self.drop()
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.main()
        self.assertFalse(stale.exists())
        self.assertEqual((apply.PREVIOUS / 'backend/MesaNexo.Api').read_text(), 'version 0.3.03')

    def test_failed_install_is_finished_by_dpkg_when_it_can(self):
        self.drop()
        self.dpkg_ok = False
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.main()
        self.assertEqual(self.calls[-2:], ['dpkg -i', 'dpkg --configure'])
        self.assertEqual((apply.state()['stage'], self.version()), ('installed', ('version 0.3.04', 'new')))

    def test_failed_install_goes_back_to_the_previous_version(self):
        self.drop()
        self.dpkg_ok = self.configure_ok = False
        with patch.object(apply, 'run', side_effect=self.fake), self.assertRaises(subprocess.CalledProcessError):
            apply.main()
        self.assertEqual((apply.state()['stage'], self.version()), ('rolled-back', ('version 0.3.03', 'old')))
        self.assertFalse(apply.APP.with_name('culinaryos-app.failed').exists())

    def test_power_cut_during_install(self):
        self.drop()
        self.dpkg_ok = False
        def cut(args, **kwargs):
            self.fake(args, **kwargs)
            if args[:2] == ['dpkg', '-i'] or args[:2] == ['dpkg', '--configure']:
                raise KeyboardInterrupt('power cut')
            return subprocess.CompletedProcess(args, 0)
        with patch.object(apply, 'run', side_effect=cut), self.assertRaises(KeyboardInterrupt):
            apply.main()
        self.assertEqual(apply.state()['stage'], 'installing')
        self.assertEqual(self.version()[0], 'version 0.3.04', 'the application is half new when the power comes back')
        # At boot dpkg cannot finish it either: the copy comes back before the backend starts.
        self.configure_ok = False
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.recover()
        self.assertEqual((apply.state()['stage'], self.version()), ('rolled-back', ('version 0.3.03', 'old')))

    def test_power_cut_that_dpkg_can_finish(self):
        self.drop()
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.main()
        apply.mark('installing')
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.recover()
        self.assertEqual(apply.state()['stage'], 'installed')
        self.assertTrue(apply.state()['history'][-1].get('recovered'))

    def test_recover_and_health_leave_other_stages_alone(self):
        for stage in ('none', 'received', 'verified', 'healthy', 'rolled-back'):
            with self.subTest(stage=stage):
                if stage != 'none':
                    apply.mark(stage)
                with patch.object(apply, 'run', side_effect=self.fake), patch.object(apply, 'healthy', side_effect=AssertionError):
                    apply.recover()
                    apply.health()
                self.assertEqual((apply.state()['stage'], self.calls), (stage, []))

    def test_unhealthy_after_reboot_goes_back(self):
        self.drop()
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.main()
        restarts = []
        with patch.object(apply, 'healthy', return_value=False):
            apply.health(restart=lambda: restarts.append(1))
        self.assertEqual((apply.state()['stage'], self.version(), restarts), ('rolled-back', ('version 0.3.03', 'old'), [1]))
        self.assertIn('no respondió', apply.state()['reason'])

    def test_every_stage_is_on_disk_before_the_next_step(self):
        self.drop()
        seen = {}
        def watch(args, **kwargs):
            seen[args[0] if args[0] != 'dpkg' else 'dpkg ' + args[1]] = json.loads(apply.STATE.read_text())['stage']
            return self.fake(args, **kwargs)
        with patch.object(apply, 'run', side_effect=watch):
            apply.main()
        self.assertEqual(seen, {apply.API: 'received', 'runuser': 'verified', 'dpkg -i': 'installing'})

    def test_only_the_last_three_dumps_are_kept(self):
        for i in range(5):
            self.drop()
            with patch.object(apply, 'run', side_effect=self.fake), patch.object(apply.time, 'strftime', return_value='2026110%d-120000' % i):
                apply.main()
        kept = sorted(f.name for f in apply.BACKUPS.glob('*.dump'))
        self.assertEqual(kept, ['culinaryos-antes-de-0.3.04-2026110%d-120000.dump' % i for i in (2, 3, 4)])

    def test_no_room_for_the_backup_installs_nothing(self):
        self.drop()
        full = type('Usage', (), {'free': 20 * 2**20})()
        with patch.object(apply, 'run', side_effect=self.fake), patch.object(apply.shutil, 'disk_usage', return_value=full), self.assertRaises(RuntimeError):
            apply.main()
        self.assertEqual((self.calls, self.version()), ([apply.API], ('version 0.3.03', 'old')))
        self.assertEqual(apply.state()['stage'], 'failed')
        self.assertIn('Sin espacio', apply.state()['reason'])
        self.assertEqual(list(apply.BACKUPS.glob('*')), [])

    def test_a_failed_dump_installs_nothing(self):
        self.drop()
        def broken(args, **kwargs):
            if args[0] == 'runuser':
                self.calls.append('runuser')
                raise subprocess.CalledProcessError(1, args)
            return self.fake(args, **kwargs)
        with patch.object(apply, 'run', side_effect=broken), self.assertRaises(subprocess.CalledProcessError):
            apply.main()
        self.assertEqual((apply.state()['stage'], self.version()), ('failed', ('version 0.3.03', 'old')))
        self.assertNotIn('dpkg -i', self.calls)
        self.assertEqual(list(apply.BACKUPS.glob('*.dump')), [], 'a half-written dump is never kept as a backup')

    def test_offer_version_is_read_from_the_signed_update(self):
        import base64
        body = base64.urlsafe_b64encode(json.dumps({'version': '0.3.5'}).encode()).decode().rstrip('=')
        (apply.INBOX / 'offer.json').write_text(json.dumps({'update': 'h.' + body + '.s'}))
        (apply.INBOX / 'package.deb').write_bytes(b'!<arch>\n')
        with patch.object(apply, 'run', side_effect=self.fake):
            apply.main()
        self.assertEqual(apply.state()['history'][0]['offer'], '0.3.5')

if __name__ == '__main__':
    unittest.main(verbosity=2)

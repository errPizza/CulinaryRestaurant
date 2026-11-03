import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / "deploy/culinaryos/kiosk" / file)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


session = module("kiosk_session", "session.py")
installer = module("kiosk_install", "install-kiosk.py")


class KioskRecovery(unittest.TestCase):
    def test_visible_renderer_required_before_ready(self):
        guard = session.Watchdog(100)
        self.assertFalse(guard.expired(160))
        self.assertTrue(guard.expired(161))
        guard.painted(162)
        self.assertFalse(guard.expired(200))
        self.assertTrue(guard.expired(208))

    def test_heartbeat_extends_actual_liveness(self):
        guard = session.Watchdog(0)
        for now in range(1, 601, 10):
            guard.painted(now)
            self.assertFalse(guard.expired(now + 11))
        self.assertTrue(guard.expired(650))

    def test_recovery_records_why_the_interface_went_away(self):
        import signal
        self.assertEqual(session.exit_cause(None), "sin senales de la interfaz")
        self.assertEqual(session.exit_cause(1), "codigo de salida 1")
        self.assertEqual(session.exit_cause(-signal.SIGSEGV), "senal SIGSEGV")
        self.assertEqual(session.exit_cause(-signal.SIGINT), "senal SIGINT")
        self.assertEqual(session.exit_cause(-200), "senal 200")

    def test_gpu_is_used_only_with_intel_or_amd_drivers(self):
        with tempfile.TemporaryDirectory() as temp:
            drm = Path(temp)
            self.assertFalse(session.gpu_ready(drm))
            (drm / "renderD128").mkdir()
            for driver, expected in (("vmwgfx", False), ("nouveau", False), ("i915", True), ("amdgpu", True)):
                with patch.object(Path, "resolve", lambda _: Path("/sys/bus/pci/drivers") / driver):
                    self.assertEqual(session.gpu_ready(drm), expected, driver)

    def test_only_a_crash_or_a_freeze_gives_up_the_gpu(self):
        class Child:
            def __init__(self, code):
                self.code = code

            def poll(self):
                return self.code

        with tempfile.TemporaryDirectory() as temp:
            for code, gives_up in ((0, False), (1, False), (-15, False), (-9, False), (-11, True), (-6, True), (None, True)):
                run = session.Session.__new__(session.Session)
                run.child, run.on_gpu = Child(code), True
                run.log = Path(temp) / "kiosk.log"
                run.cpu_only = Path(temp) / "cpu-rendering"
                run.cpu_only.unlink(missing_ok=True)
                run.give_up_gpu()
                self.assertEqual(run.cpu_only.exists(), gives_up, code)
            run.on_gpu = False
            run.cpu_only.unlink()
            run.give_up_gpu()
            self.assertFalse(run.cpu_only.exists())

    def test_failure_log_keeps_the_end_of_the_output_and_stays_small(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            log = root / "state/kiosk.log"
            session.append_log(log, "primer fallo\n")
            self.assertEqual(log.read_text(encoding="utf-8"), "primer fallo\n")
            log.write_bytes(b"x" * (session.LOG_LIMIT + 1))
            session.append_log(log, "segundo fallo\n")
            self.assertEqual(log.read_text(encoding="utf-8"), "segundo fallo\n")
            self.assertEqual(log.with_suffix(".log.1").stat().st_size, session.LOG_LIMIT + 1)
            output = root / "app-output.log"
            output.write_bytes(b"a" * session.TAIL_BYTES + b"\xffERROR FINAL")
            self.assertTrue(session.tail(output).endswith("ERROR FINAL"))
            self.assertLessEqual(len(session.tail(output).encode()), session.TAIL_BYTES + 3)
            self.assertEqual(session.tail(root / "absent"), "")
            blocked = root / "file"
            blocked.write_text("")
            session.append_log(blocked / "kiosk.log", "sin espacio\n")

    def test_a_failure_leaves_a_report_the_app_sends_on_its_next_start(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            run = session.Session.__new__(session.Session)
            run.runtime = root
            run.log = root / "state/kiosk.log"
            run.faults = run.log.with_name("faults.jsonl")
            run.guard = type("Guard", (), {"started": 10})
            (root / "app-output.log").write_text("fallo de WebKit\n", encoding="utf-8")
            run.record_failure(70, "senal SIGSEGV")
            run.record_failure(80, "codigo de salida 1")
            first, second = [json.loads(line) for line in run.faults.read_text(encoding="utf-8").splitlines()]
            self.assertIn("senal SIGSEGV", first["what"])
            self.assertIn("reinició", first["affected"])
            self.assertIn("fallo de WebKit", first["detail"])
            self.assertEqual((first["seconds"], second["seconds"]), ("60", "70"))

    def test_keyring_key_is_private_and_survives_restarts(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "state/keyring.key"
            first = session.keyring_key(path)
            self.assertGreaterEqual(len(first), 32)
            self.assertEqual(session.keyring_key(path), first)
            if os.name == "posix":
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            path.write_text("")
            self.assertNotEqual(session.keyring_key(path), "")

    def test_keyring_is_unlocked_with_the_stored_key_before_the_app_starts(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "keyring.key"
            with patch.object(session.subprocess, "run") as run:
                session.unlock_keyring(path)
            argv = run.call_args.args[0]
            self.assertIn("--unlock", argv)
            self.assertIn("--replace", argv)
            self.assertEqual(run.call_args.kwargs["input"].decode(), path.read_text())

    def test_prior_process_cannot_dismiss_loading_screen(self):
        nonce = "a" * 64
        value = {"protocol": 1, "pid": 123, "nonce": nonce}
        self.assertTrue(session.valid_ready(json.dumps(value).encode(), 123, nonce))
        for update in ({"pid": 122}, {"nonce": "b" * 64}, {"protocol": True}, {"pid": True}, {"protocol": 2}):
            self.assertFalse(session.valid_ready(json.dumps(dict(value, **update)).encode(), 123, nonce))
        for data in (b"not json", b"[]", b"null", b"\xff", b"x" * 513):
            self.assertFalse(session.valid_ready(data, 123, nonce))

    def test_real_stderr_only_lightdm_output_preserves_existing_operator_policy(self):
        import subprocess
        import sys
        fixture = "  [Seat:*]\nA  autologin-user=operador\nB  autologin-session=culinary-restaurant\n\nSources:\nA  /etc/lightdm/lightdm.conf.d/60-culinaryos-autologin.conf\n"
        program = "import sys; sys.stderr.write(" + repr(fixture) + ")"
        original = installer.run
        def command(*args, **kwargs):
            self.assertEqual(args, ("lightdm", "--show-config"))
            return original(sys.executable, "-c", program, **kwargs)
        raw = subprocess.run([sys.executable, "-c", program], capture_output=True, text=True, check=True)
        self.assertEqual(raw.stdout, "")
        self.assertEqual(raw.stderr, fixture)
        with patch.object(installer, "run", side_effect=command):
            actual = installer.lightdm_config()
        self.assertEqual(actual, fixture)
        self.assertEqual(installer.autologin_user(actual), "operador")
        self.assertIn("autologin-session=culinary-restaurant", actual)

    def test_lightdm_stdout_remains_supported_and_failures_are_not_accepted(self):
        import subprocess
        import sys
        original = installer.run
        for stream in ("stdout", "stderr"):
            def command(*args, **kwargs):
                return original(sys.executable, "-c", "import sys; sys." + stream + ".write('[Seat:*]\\nautologin-user=operador\\n')", **kwargs)
            with patch.object(installer, "run", side_effect=command):
                self.assertEqual(installer.autologin_user(installer.lightdm_config()), "operador")
        def fail(*args, **kwargs):
            return original(sys.executable, "-c", "import sys; sys.stderr.write('configuration error'); sys.exit(1)", **kwargs)
        with patch.object(installer, "run", side_effect=fail), self.assertRaises(subprocess.CalledProcessError):
            installer.lightdm_config()

    def test_existing_lightdm_policy_recognized(self):
        self.assertEqual(installer.autologin_user("[Seat:*]\nA  autologin-user=operador\nB  autologin-session=xfce\n"), "operador")
        self.assertEqual(installer.autologin_user("[Seat:*]\nautologin-user=laboratorio\n"), "laboratorio")
        self.assertIsNone(installer.autologin_user("[Seat:*]\nuser-session=xfce\n"))

    def test_failed_install_restores_original_binary_and_does_not_activate(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(os, "chown", create=True), patch.object(installer, "secure_parent"):
            root = Path(tmp)
            app = root / "app"
            (app / "client").mkdir(parents=True)
            binary = app / "client/culinary-restaurant"
            binary.write_bytes(b"working original")
            config = root / "lightdm.conf"
            backup = root / "backup"
            original = installer.replace_atomic
            calls = 0
            def fail_second(path, data, mode):
                nonlocal calls
                calls += 1
                if calls == 2:
                    raise OSError("injected disk error")
                return original(path, data, mode)
            with patch.object(installer, "APP", app), patch.object(installer, "LIGHTDM", config), patch.object(installer, "replace_atomic", side_effect=fail_second):
                with self.assertRaises(OSError):
                    installer.commit([(binary, b"new", 0o755), (config, b"activate", 0o644)], backup)
            self.assertEqual(binary.read_bytes(), b"working original")
            self.assertFalse(config.exists())

    def test_restoration_returns_previous_app_and_removes_only_added_config(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(os, "chown", create=True), patch.object(installer, "secure_parent"):
            root = Path(tmp)
            app = root / "app"
            (app / "client").mkdir(parents=True)
            binary = app / "client/culinary-restaurant"
            binary.write_bytes(b"old binary")
            config = root / "lightdm.conf"
            backup = root / "backup"
            with patch.object(installer, "APP", app), patch.object(installer, "LIGHTDM", config):
                installer.commit([(binary, b"new binary", 0o755), (config, b"new session", 0o644)], backup)
                self.assertEqual(binary.read_bytes(), b"new binary")
                installer.restore(backup)
            self.assertEqual(binary.read_bytes(), b"old binary")
            self.assertFalse(config.exists())

    def test_overriding_lightdm_config_rolls_back_all_destinations(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(os, "chown", create=True), patch.object(installer, "secure_parent"):
            root = Path(tmp)
            app = root / "app"
            (app / "client").mkdir(parents=True)
            binary = app / "client/culinary-restaurant"
            binary.write_bytes(b"old binary")
            config = root / "lightdm.conf"
            backup = root / "backup"
            def rejected(): raise ValueError("configuration override")
            with patch.object(installer, "APP", app), patch.object(installer, "LIGHTDM", config):
                with self.assertRaises(ValueError):
                    installer.commit([(binary, b"new", 0o755), (config, b"new session", 0o644)], backup, validate=rejected)
            self.assertEqual(binary.read_bytes(), b"old binary")
            self.assertFalse(config.exists())

    def test_restore_refuses_path_outside_application(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(ValueError):
                installer.restore(Path(tmp), [{"path": "/etc/shadow", "saved": None, "mode": None}])

    def test_manifest_rejects_changed_and_unlisted_files(self):
        import hashlib
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data = root / "safe.txt"
            data.write_bytes(b"safe")
            (root / "SHA256SUMS").write_text(hashlib.sha256(b"safe").hexdigest() + "  safe.txt\n")
            installer.verify_bundle(root)
            data.write_bytes(b"changed")
            with self.assertRaises(ValueError):
                installer.verify_bundle(root)
            data.write_bytes(b"safe")
            (root / "extra.txt").write_bytes(b"unlisted")
            with self.assertRaises(ValueError):
                installer.verify_bundle(root)

    def test_cache_requires_complete_build_owned_by_current_administrator(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            work = root / "culinary-restaurant-build.previous"
            cargo = work / "toolchain/cargo/bin/cargo"
            cargo.parent.mkdir(parents=True)
            cargo.write_bytes(b"fixture tool")
            uid = work.stat().st_uid
            with self.assertRaises(ValueError):
                installer.cache_for(uid, root)
            (work / "client-source/src-tauri/target").mkdir(parents=True)
            self.assertEqual(installer.cache_for(uid, root), work)
            with self.assertRaises(ValueError):
                installer.cache_for(uid + 1, root)

    def test_manifest_rejects_traversal_and_duplicate(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for text in ("a" * 64 + "  ../outside\n", "a" * 64 + "  /etc/shadow\n"):
                (root / "SHA256SUMS").write_text(text)
                with self.assertRaises(ValueError):
                    installer.verify_bundle(root)
            import hashlib
            (root / "file.txt").write_bytes(b"fixture")
            row = hashlib.sha256(b"fixture").hexdigest() + "  file.txt\n"
            (root / "SHA256SUMS").write_text(row + row)
            with self.assertRaises(ValueError):
                installer.verify_bundle(root)


if __name__ == "__main__":
    unittest.main()

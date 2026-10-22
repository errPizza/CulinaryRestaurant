"""Launcher smoke checks against a separate encrypted lab, never .lab/manual."""
import hashlib
import http.server
import json
import os
from pathlib import Path
import signal
import sqlite3
import subprocess
import sys
import threading
import tempfile
import urllib.parse

ROOT = Path(__file__).resolve().parents[1]
PYTHON = sys.executable
FIXTURE = ROOT / 'tests/MesaNexo.Hybrid.Tests/bin/Release/net10.0/MesaNexo.Hybrid.Tests.dll'


def check(condition, name):
    if not condition:
        raise AssertionError(name)
    evidence.append({'test': name, 'result': 'passed'})
    print('PASS', name, flush=True)


def invoke(directory, site, port, expected_success=True):
    # A detached backend may inherit duplicate Windows pipe handles. Files let
    # subprocess wait for the launcher itself without waiting for that backend.
    with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                 str(ROOT / 'tests/Invoke-LabSmoke.ps1'), '-Python', PYTHON,
                                 '-LabDirectory', str(directory), '-SiteId', site, '-Port', str(port)],
                                cwd=ROOT, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, timeout=120)
        stdout.seek(0)
        stderr.seek(0)
        result.stdout = stdout.read().decode('utf-8', errors='replace')
        result.stderr = stderr.read().decode('utf-8', errors='replace')
    if not expected_success:
        return result
    if result.returncode:
        raise AssertionError('Launcher failed: ' + result.stderr[-2500:] + '\n' + result.stdout[-1500:])
    lines = [line for line in result.stdout.splitlines() if line.startswith('LAB_RESULT_JSON:')]
    if len(lines) != 1:
        raise AssertionError('Launcher did not return one result')
    return json.loads(lines[0].split(':', 1)[1])


def record_digest(directory):
    with sqlite3.connect((directory/'store.sqlite').as_uri()+'?mode=ro', uri=True) as db:
        rows = db.execute('SELECT id,version,envelope FROM records ORDER BY id').fetchall()
    return hashlib.sha256(json.dumps(rows).encode()).hexdigest()


evidence = []
fixture = None
owned_backend = None
server = None
try:
    fixture = subprocess.Popen(['dotnet', str(FIXTURE), str(ROOT), PYTHON, '--serve'], cwd=ROOT,
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               encoding='utf-8', errors='replace')
    ready = None
    while True:
        line = fixture.stdout.readline()
        if not line:
            raise AssertionError('Fixture did not start: ' + fixture.stderr.read()[-1000:])
        try:
            message = json.loads(line)
        except ValueError:
            continue
        if message.get('ready'):
            ready = message
            break
    directory = Path(ready['directory']).resolve()
    assert directory.is_relative_to(ROOT/'.lab') and directory.name.startswith('hybrid-')
    site = ready['profile']['SiteId']
    port = urllib.parse.urlparse(ready['profile']['ServerUrl']).port
    before = record_digest(directory)
    reused = invoke(directory, site, port)
    check(reused['Reused'] and reused['Ready'] and reused['BuildRoot'] is None,
          'trusted running backend reused without recompiling its locked DLLs')
    check(record_digest(directory) == before, 'reuse does not change existing encrypted records')
    fixture.stdin.close()
    fixture.wait(timeout=15)
    restarted = invoke(directory, site, port)
    owned_backend = restarted['BackendPid']
    build = Path(restarted['BuildRoot']).resolve()
    check(not restarted['Reused'] and restarted['Ready'] and owned_backend,
          'stopped lab starts successfully from a fresh isolated build')
    check(build.is_relative_to(ROOT/'.lab/start-builds') and Path(restarted['ApiAssembly']).is_file(),
          'new API artifacts stay outside the shared Debug directory')
    check(record_digest(directory) == before, 'restart preserves initialized users and business records')
    second = invoke(directory, site, port)
    check(second['Reused'] and second['BackendPid'] == owned_backend and second['BuildRoot'] is None,
          'second launch reuses the same backend instead of spawning a duplicate')

    class SilentHandler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), SilentHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    foreign = invoke(directory, site, server.server_port, expected_success=False)
    check(foreign.returncode != 0 and 'LAB_RESULT_JSON:' not in foreign.stdout,
          'untrusted occupied port is rejected instead of bypassing TLS or starting another backend')
    check(record_digest(directory) == before, 'rejected foreign service cannot reinitialize stored data')
    (ROOT/'evidence/lab-launcher.json').write_text(json.dumps(evidence, indent=2)+'\n', encoding='utf-8')
    print(str(len(evidence))+' launcher checks passed.', flush=True)
finally:
    if server:
        server.shutdown()
        server.server_close()
    if fixture and fixture.poll() is None:
        fixture.stdin.close()
        try:
            fixture.wait(timeout=10)
        except subprocess.TimeoutExpired:
            fixture.terminate()
    if owned_backend:
        try:
            os.kill(int(owned_backend), signal.SIGTERM)
        except OSError:
            pass

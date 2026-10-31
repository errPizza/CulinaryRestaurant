#!/usr/bin/python3
"""Turns evidence/*.json into a report for one commit: what ran, on what, and how much of it covers each risk."""
import json, platform, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RISKS = {
    'Dinero': ['money', 'approvals', 'boundaries', 'live-integrations', 'fiscal-sandbox'],
    'Inventario': ['inventory', 'backoffice'],
    'Seguridad': ['licenses', 'checkpoints', 'approvals', 'privacy', 'tenants'],
    'Restauración': ['store-outbox', 'checkpoints', 'boundaries'],
    'Sincronización': ['store-outbox', 'boundaries'],
}


def git(*args):
    return subprocess.run(['git', *args], cwd=ROOT, capture_output=True, text=True, check=False).stdout.strip()


def tool(*args):
    try:
        return subprocess.run(args, capture_output=True, text=True, check=False, shell=platform.system() == 'Windows').stdout.strip().splitlines()[0]
    except (OSError, IndexError):
        return 'no disponible'


def checks(name):
    path = ROOT / 'evidence' / (name + '.json')
    if not path.is_file():
        return None
    rows = json.loads(path.read_text(encoding='utf-8-sig'))
    return (len(rows), sum(1 for row in rows if row.get('result') == 'passed'), path.stat().st_mtime) if isinstance(rows, list) else None


def report():
    head = git('rev-parse', 'HEAD')
    lines = ['# Informe de pruebas', '', f'- Commit: `{head[:12]}` ({git("branch", "--show-current")})', f'- Cambios sin confirmar en el código: {"sí" if git("status", "--short", "--", "src", "tests", "deploy") else "no"}', f'- Sistema: {platform.platform()}', f'- .NET: {tool("dotnet", "--version")}', f'- Node: {tool("node", "--version")}', f'- Python: {platform.python_version()}', '', '| Riesgo | Suite | Comprobaciones | Pasaron |', '|---|---|---:|---:|']
    missing = []
    for risk, suites in RISKS.items():
        for suite in suites:
            found = checks(suite)
            if found is None:
                missing.append(suite)
                lines.append(f'| {risk} | {suite} | sin evidencia | — |')
            else:
                lines.append(f'| {risk} | {suite} | {found[0]} | {found[1]} |')
    web = ROOT / 'evidence/hybrid-web-tests.json'
    if web.is_file():
        stats = json.loads(web.read_text(encoding='utf-8-sig')).get('stats', {})
        lines += ['', f'Web: {stats.get("expected", 0)} pasaron, {stats.get("unexpected", 0)} fallaron, {stats.get("skipped", 0)} omitidas, {stats.get("duration", 0) / 1000:.0f} s.']
    lines += ['', 'Un riesgo cubierto aquí significa probado con simuladores. Lo validado en real está en `docs/ESTADO-COMERCIAL.md`.']
    return '\n'.join(lines) + '\n', sorted(set(missing))


if __name__ == '__main__':
    text, missing = report()
    (ROOT / 'evidence/INFORME.md').write_text(text, encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    print(text)
    # A suite that left no evidence did not run: in CI that is a failure, not a blank row.
    if missing and '--strict' in sys.argv:
        raise SystemExit('Sin evidencia: ' + ', '.join(missing))

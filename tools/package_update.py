"""The .deb the panel delivers to CulinaryOS: application files over /opt/culinaryos-app, nothing that holds branch
data. The Linux client comes from the builder VM into artifacts/culinaryos-linux/client (build-client.sh)."""
import argparse
import hashlib
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).parent))
from package_deb import ROOT, ar, tarball
APP = ROOT / 'artifacts/culinaryos-linux'
DEPLOY = ROOT / 'deploy/culinaryos'

def text(path):
    return Path(path).read_bytes().replace(b'\r\n', b'\n')

def payload():
    app = 'opt/culinaryos-app/'
    entries = [
        (app + 'backend/MesaNexo.Api', (APP / 'backend/MesaNexo.Api').read_bytes(), 0o755),
        (app + 'backend/MesaNexo.Api.staticwebassets.endpoints.json', (APP / 'backend/MesaNexo.Api.staticwebassets.endpoints.json').read_bytes(), 0o644),
        (app + 'client/culinary-bridge', (APP / 'bridge/culinary-bridge').read_bytes(), 0o755),
        (app + 'client/culinary-restaurant', (APP / 'client/culinary-restaurant').read_bytes(), 0o755),
        (app + 'client/CulinaryRestaurant', text(DEPLOY / 'CulinaryRestaurant'), 0o755),
        (app + 'hardware-identity.py', text(DEPLOY / 'hardware-identity.py'), 0o644),
        (app + 'renew-tls.py', text(DEPLOY / 'renew-tls.py'), 0o644),
        (app + 'rotate-ca.py', text(DEPLOY / 'rotate-ca.py'), 0o644),
        ('usr/local/lib/culinary-update/apply.py', text(DEPLOY / 'update/apply.py'), 0o755),
        ('etc/systemd/system/culinary-update.path', text(DEPLOY / 'update/culinary-update.path'), 0o644),
        ('etc/systemd/system/culinary-update.service', text(DEPLOY / 'update/culinary-update.service'), 0o644),
        ('usr/lib/tmpfiles.d/culinary-update.conf', text(DEPLOY / 'update/culinary-update.tmpfiles'), 0o644),
    ]
    for file in sorted((DEPLOY / 'kiosk').iterdir()):
        if file.is_file():
            entries.append((app + 'kiosk/' + file.name, text(file), 0o755 if file.name == 'session.sh' else 0o644))
    return entries

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    version = parser.parse_args().version
    entries = payload()
    if any(data[:4] != b'\x7fELF' for path, data, _ in entries if path.endswith(('MesaNexo.Api', 'culinary-restaurant', 'culinary-bridge'))):
        raise ValueError('Expected Linux executables')
    control = f'Package: culinary-restaurant\nVersion: {version}\nArchitecture: amd64\nMaintainer: PizzaDev (Culinary)\nSection: misc\nPriority: optional\nDescription: Culinary Restaurant for CulinaryOS\n Replaces the application files. Does not touch branch data, keys or networking.\n'
    md5 = ''.join(hashlib.md5(data).hexdigest() + '  ' + name + '\n' for name, data, _ in sorted(entries))
    # dpkg creates no parents on its own: each directory needs its entry.
    folders = {parent.as_posix() + '/' for path, _, _ in entries for parent in Path(path).parents if parent != Path('.')}
    target = ROOT / 'artifacts' / f'CulinaryRestaurant-{version}-amd64.deb'
    target.write_bytes(ar([('debian-binary', b'2.0\n'), ('control.tar.gz', tarball([('control', control.encode(), 0o644), ('md5sums', md5.encode(), 0o644), ('postinst', text(DEPLOY / 'update/postinst'), 0o755)])), ('data.tar.gz', tarball(entries + [(folder, b'', 0o755) for folder in folders]))]))
    print(target, target.stat().st_size, 'bytes')

if __name__ == '__main__':
    main()

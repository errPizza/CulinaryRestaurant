"""Prepare a small kiosk maintenance CD, without backend, data or credentials."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--revision", type=int, required=True)
args = parser.parse_args()
if not 5 <= args.revision <= 99:
    raise SystemExit("Use a new revision, 5..99")
output = ROOT / f"artifacts/culinaryos-cd-r{args.revision}"
if output.exists():
    raise SystemExit("The staging directory already exists; do not overwrite it")
output.mkdir(parents=True)


def copy(source, target):
    source = ROOT / source
    target = output / target
    if source.is_symlink():
        raise ValueError("No symlinks in package")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)


def tree(source, target):
    for path in sorted((ROOT / source).rglob("*")):
        if path.is_symlink():
            raise ValueError("No symlinks in package")
        if path.is_file():
            copy(path.relative_to(ROOT), Path(target) / path.relative_to(ROOT / source))


for name in ("Cargo.toml", "Cargo.lock", "build.rs", "tauri.conf.json", "tauri.linux.conf.json"):
    copy("src/MesaNexo.Web/src-tauri/" + name, "client-source/src-tauri/" + name)
for name in ("src", "icons", "capabilities"):
    tree("src/MesaNexo.Web/src-tauri/" + name, "client-source/src-tauri/" + name)
tree("src/MesaNexo.Web/dist", "client-source/dist")
tree("deploy/culinaryos/kiosk", "deploy/culinaryos/kiosk")
copy("deploy/culinaryos/kiosk/activate.sh", "activar-kiosco.sh")
copy("docs/CULINARYOS-KIOSCO.md", "LEEME.md")
lines = []
for path in sorted(output.rglob("*")):
    if path.is_file():
        lines.append(hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.relative_to(output).as_posix())
(output / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="ascii", newline="\n")
print(json.dumps({"kind": "kiosk-maintenance", "revision": args.revision, "files": len(lines), "tree": str(output),
                  "containsBackend": False, "containsCredentials": False, "guestActivationExecuted": False}, indent=2))

#!/usr/bin/python3
"""Incremental, backed-up kiosk activation for an already working CulinaryOS app."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile

APP = Path("/opt/culinaryos-app")
LIGHTDM = Path("/etc/lightdm/lightdm.conf.d/95-culinary-restaurant-kiosk.conf")
DESKTOP = Path("/usr/share/xsessions/culinary-restaurant.desktop")
MARKER = APP / "kiosk/installed.json"


def run(*args, **kwargs):
    return subprocess.run([str(v) for v in args], check=True, **kwargs)


def elf(path):
    with Path(path).open("rb") as stream:
        data = stream.read(64)
    if len(data) < 64 or data[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", data, 18)[0] != 62:
        raise ValueError("Se requiere un binario ELF Linux x86-64: " + str(path))


def verify_bundle(bundle):
    expected = set()
    for row in (bundle / "SHA256SUMS").read_text(encoding="ascii").splitlines():
        digest, name = row.split("  ", 1)
        relative = Path(name)
        if not re.fullmatch(r"[0-9a-f]{64}", digest) or relative.is_absolute() or ".." in relative.parts or name in expected:
            raise ValueError("Manifiesto del CD no valido")
        path = bundle / relative
        if path.is_symlink() or not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError("El CD no coincide: " + name)
        expected.add(name)
    actual = {p.relative_to(bundle).as_posix() for p in bundle.rglob("*") if p.is_file() and p.name != "SHA256SUMS"}
    if actual != expected or any(p.is_symlink() for p in bundle.rglob("*")):
        raise ValueError("Contenido inesperado en el CD")


def lightdm_config():
    # LightDM --show-config intentionally prints configuration with g_printerr.
    # Merge both streams so a successful stderr-only result is not treated as absent.
    return run("lightdm", "--show-config", stdout=subprocess.PIPE,
               stderr=subprocess.STDOUT, text=True).stdout


def autologin_user(text):
    users = re.findall(r"^\s*(?:[A-Z]+\s+)?autologin-user\s*=\s*(\S*)\s*$", text, re.M)
    return users[-1] if users else None


def secure_parent(path):
    for part in (path, *path.parents):
        if part.is_symlink() or part.stat().st_uid != 0 or part.stat().st_mode & 0o022:
            raise PermissionError("Ruta debe ser de root y no modificable por usuarios: " + str(part))


def cache_for(uid, base=Path("/var/tmp")):
    candidates = []
    for work in base.glob("culinary-restaurant-build.*"):
        if work.is_symlink() or not work.is_dir() or work.stat().st_uid != uid:
            continue
        cargo = work / "toolchain/cargo/bin/cargo"
        target = work / "client-source/src-tauri/target"
        if cargo.is_file() and target.is_dir():
            if not cargo.resolve().is_relative_to(work.resolve()) or not target.resolve().is_relative_to(work.resolve()):
                continue
            if cargo.stat().st_uid == uid and target.stat().st_uid == uid:
                candidates.append(work)
    if not candidates:
        raise ValueError("No se encontro la compilacion anterior de esta cuenta en /var/tmp; no se cambia la app")
    return max(candidates, key=lambda p: p.stat().st_mtime)


def replace_atomic(path, data, mode):
    path = Path(path)
    if path.is_symlink():
        raise PermissionError("No reemplazar enlaces: " + str(path))
    fd, temporary = tempfile.mkstemp(prefix=".kiosk-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        os.chmod(temporary, mode); os.chown(temporary, 0, 0)
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def commit(files, backup, validate=None):
    """Back up every destination before mutation; restore all on any failure."""
    backup.mkdir(parents=True, mode=0o700)
    os.chmod(backup, 0o700)
    records = []
    for index, (path, data, mode) in enumerate(files):
        path = Path(path)
        if path.is_symlink() or (path.exists() and not path.is_file()):
            raise PermissionError("Destino inesperado: " + str(path))
        record = {"path": str(path), "saved": None, "mode": None}
        if path.exists():
            saved = str(index)
            (backup / saved).write_bytes(path.read_bytes())
            os.chmod(backup / saved, 0o600)
            record.update(saved=saved, mode=path.stat().st_mode & 0o777)
        records.append(record)
    (backup / "files.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
    applied = []
    try:
        for (path, data, mode), record in zip(files, records):
            path = Path(path)
            if not path.parent.exists():
                path.parent.mkdir(parents=True, mode=0o755)
                # umask 077 protects backups, but public executable paths require 0755.
                os.chmod(path.parent, 0o755)
            secure_parent(path.parent)
            replace_atomic(path, data, mode)
            applied.append(record)
        if validate is not None:
            validate()
    except BaseException:
        restore(backup, reversed(applied))
        raise


def restore(backup, records=None):
    if records is None:
        records = reversed(json.loads((backup / "files.json").read_text(encoding="utf-8")))
    for record in records:
        path = Path(record["path"])
        allowed = (path == APP / "client/culinary-restaurant" or path == LIGHTDM or path == DESKTOP
                   or path.parent == APP / "kiosk")
        if not allowed:
            raise ValueError("Destino de respaldo no permitido")
        secure_parent(path.parent)
        if record["saved"] is None:
            if path.is_symlink():
                raise PermissionError("Destino es enlace")
            path.unlink(missing_ok=True)
        else:
            saved = backup / record["saved"]
            if not str(record["saved"]).isdigit() or saved.is_symlink():
                raise ValueError("Archivo de respaldo inesperado")
            replace_atomic(path, saved.read_bytes(), record["mode"])


def install(bundle):
    import pwd
    if sys.platform != "linux" or os.geteuid() != 0:
        raise PermissionError("Ejecutar con sudo dentro de CulinaryOS")
    os.umask(0o077)
    uid = int(os.environ.get("SUDO_UID", "0"))
    admin = pwd.getpwuid(uid)
    if uid == 0 or admin.pw_name in ("operador", "culinaryos-app"):
        raise PermissionError("Usa sudo desde la cuenta administradora laboratorio")
    verify_bundle(bundle)
    if not Path("/etc/culinaryos/application.registered").is_file():
        raise ValueError("Primero debe estar registrada y funcionando Culinary Restaurant")
    secure_parent(APP / "client")
    for name in ("CulinaryRestaurant", "culinary-restaurant", "culinary-bridge"):
        path = APP / "client" / name
        if path.is_symlink() or path.stat().st_uid != 0:
            raise PermissionError("Ejecutable instalado inesperado")
    elf(APP / "client/culinary-restaurant"); elf(APP / "client/culinary-bridge")
    config = lightdm_config()
    if autologin_user(config) != "operador":
        raise ValueError("Se requiere el autologin de operador ya existente; este instalador no cambia la autenticacion de Linux")
    if LIGHTDM.exists() or MARKER.exists():
        raise ValueError("El kiosco ya esta instalado o hay una activacion anterior; revisar sin sobrescribir")
    cache = cache_for(uid)
    print("Comprobado: app existente, cuenta de operador y cache de compilacion. No se modifican usuarios, claves ni datos.", flush=True)
    packages = ("openbox", "python3-tk", "gnome-keyring", "dbus-x11", "xvfb", "xauth", "wmctrl", "x11-utils", "libxtst6")
    apt_env = dict(os.environ, NEEDRESTART_MODE="l")
    run("apt-get", "update", env=apt_env)
    run("apt-get", "install", "-y", "--no-install-recommends", *packages, env=apt_env)
    work = Path(tempfile.mkdtemp(prefix="culinary-restaurant-kiosk-build.", dir="/var/tmp"))
    shutil.copytree(bundle / "client-source", work / "client-source")
    shutil.copytree(bundle / "deploy/culinaryos/kiosk", work / "verification")
    binaries = work / "client-source/src-tauri/binaries"
    binaries.mkdir()
    shutil.copyfile(APP / "client/culinary-bridge", binaries / "culinary-bridge-x86_64-unknown-linux-gnu")
    smoke = work / "smoke"
    smoke.mkdir()
    for path in (work, *work.rglob("*")):
        if path.is_symlink():
            raise PermissionError("El trabajo nuevo no puede contener enlaces")
        os.chown(path, uid, admin.pw_gid)
        os.chmod(path, 0o755 if path.is_dir() or path.name.startswith("culinary-bridge") else 0o644)
    os.chmod(work, 0o700)
    source = work / "client-source/src-tauri"
    cargo_home = cache / "toolchain/cargo"
    env = dict(os.environ, HOME=admin.pw_dir, CARGO_HOME=str(cargo_home),
               RUSTUP_HOME=str(cache / "toolchain/rustup"), CARGO_BUILD_JOBS="2",
               CARGO_TARGET_DIR=str(cache / "client-source/src-tauri/target"),
               TAURI_CONFIG=(source / "tauri.linux.conf.json").read_text(encoding="utf-8"),
               PATH=str(cargo_home / "bin") + ":" + os.environ["PATH"])
    print("Compilando solo la interfaz con el toolchain y cache existentes…", flush=True)
    run("runuser", "-u", admin.pw_name, "--", cargo_home / "bin/cargo", "build", "--release", "--locked", "--offline",
        "--features", "tauri/custom-protocol", cwd=source, env=env)
    compiled = cache / "client-source/src-tauri/target/release/culinary-restaurant"
    elf(compiled)
    # Keep the original installed binary until a real private Linux UI test passes.
    bin_dir = work / "verified-bin"
    bin_dir.mkdir(mode=0o755)
    for src, name in ((compiled, "culinary-restaurant"), (APP / "client/culinary-bridge", "culinary-bridge")):
        shutil.copyfile(src, bin_dir / name); os.chmod(bin_dir / name, 0o755)
    os.chown(bin_dir, uid, admin.pw_gid)
    verify_env = {k: v for k, v in env.items() if k not in ("DBUS_SESSION_BUS_ADDRESS", "DISPLAY", "XAUTHORITY")}
    verify_env.update(HOME=str(smoke), XDG_DATA_HOME=str(smoke / "data"),
                      XDG_CONFIG_HOME=str(smoke / "config"), XDG_CACHE_HOME=str(smoke / "cache"))
    run("runuser", "-u", admin.pw_name, "--", "xvfb-run", "-a", "-s", "-screen 0 1360x900x24 -nolisten tcp -noreset",
        "dbus-run-session", "--", "/usr/bin/python3", work / "verification/verify-kiosk.py",
        bin_dir / "culinary-restaurant", smoke, env=verify_env, timeout=120)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    backup = Path("/var/backups/culinaryos") / ("restaurant-kiosk-" + stamp)
    files = [(APP / "client/culinary-restaurant", compiled.read_bytes(), 0o755)]
    for name in ("session.py", "session.sh", "openbox.xml", "install-kiosk.py"):
        files.append((APP / "kiosk" / name, (bundle / "deploy/culinaryos/kiosk" / name).read_bytes(), 0o755 if name.endswith(".sh") else 0o644))
    files.append((DESKTOP, (bundle / "deploy/culinaryos/kiosk/culinary-restaurant.desktop").read_bytes(), 0o644))
    files.append((MARKER, (json.dumps({"version": 1, "backup": str(backup), "linuxUiSmokeTest": "passed"}) + "\n").encode(), 0o644))
    # Activation is LAST. Keep the existing autologin-user, PAM and admin sessions.
    files.append((LIGHTDM, b"[Seat:*]\nautologin-session=culinary-restaurant\n", 0o644))
    def effective_session():
        current = lightdm_config()
        sessions = re.findall(r"^\s*(?:[A-Z]+\s+)?autologin-session\s*=\s*(\S*)\s*$", current, re.M)
        if autologin_user(current) != "operador" or not sessions or sessions[-1] != "culinary-restaurant":
            raise ValueError("Otra configuracion de LightDM prevalece; se restaura la sesion anterior")
    commit(files, backup, validate=effective_session)
    print("Kiosco preparado. Respaldo: " + str(backup))
    print("No se reinicio ni cerro la app actual. Al reiniciar: pantalla de carga -> Culinary Restaurant.")
    print("Recuperacion: Ctrl+Alt+F2, entrar como laboratorio, sudo python3 /opt/culinaryos-app/kiosk/install-kiosk.py --restore " + str(backup))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path)
    parser.add_argument("--restore", type=Path)
    args = parser.parse_args()
    if args.restore:
        if sys.platform != "linux" or os.geteuid() != 0:
            raise PermissionError("Recuperacion requiere sudo en CulinaryOS")
        backup = args.restore.resolve(strict=True)
        if backup.parent != Path("/var/backups/culinaryos") or not backup.name.startswith("restaurant-kiosk-"):
            raise ValueError("Respaldo fuera del directorio permitido")
        secure_parent(backup)
        restore(backup)
        print("Sesion anterior restaurada. Reinicia con sudo reboot cuando estes listo.")
    elif args.bundle:
        install(args.bundle.resolve(strict=True))
    else:
        parser.error("Usa --bundle o --restore")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("No se activo el kiosco: " + str(error), file=sys.stderr)
        raise SystemExit(1)

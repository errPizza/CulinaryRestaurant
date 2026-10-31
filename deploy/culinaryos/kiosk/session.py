#!/usr/bin/python3
"""Exclusive graphical session: no XFCE desktop, panels or application launcher.

Only operator-owned child process groups are supervised. Administrative TTYs,
LightDM authentication, the backend and the encrypted store are left alone.
"""
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import time

APP = "/opt/culinaryos-app/client/CulinaryRestaurant"
CONFIG = "/opt/culinaryos-app/kiosk/openbox.xml"
LOG_LIMIT = 256 * 1024
TAIL_BYTES = 4096


def valid_ready(raw, pid, nonce):
    try:
        if len(raw) > 512:
            return False
        value = json.loads(raw)
        return (isinstance(value, dict) and type(value.get("protocol")) is int
                and value["protocol"] == 1 and type(value.get("pid")) is int
                and value["pid"] == pid and isinstance(value.get("nonce"), str)
                and secrets.compare_digest(value["nonce"], nonce))
    except (ValueError, TypeError, UnicodeError):
        return False


class Watchdog:
    def __init__(self, now):
        self.started = now
        self.last_paint = None

    def painted(self, now):
        self.last_paint = now

    def expired(self, now):
        return (now - self.started > 60 if self.last_paint is None
                else now - self.last_paint > 45)


def exit_cause(returncode):
    if returncode is None:
        return "sin senales de la interfaz"
    if returncode < 0:
        try:
            return "senal " + signal.Signals(-returncode).name
        except ValueError:
            return f"senal {-returncode}"
    return f"codigo de salida {returncode}"


def append_log(path, text):
    """A crash must leave its cause behind; the log can never stop the recovery."""
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.is_file() and path.stat().st_size > LOG_LIMIT:
            path.replace(path.with_suffix(".log.1"))
        with path.open("a", encoding="utf-8") as log:
            log.write(text)
    except OSError:
        pass


def keyring_key(path):
    """Autologin has no password to unlock the keyring, so the operator account keeps its own key."""
    if path.is_file() and (key := path.read_text(encoding="ascii").strip()):
        return key
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    key = secrets.token_urlsafe(32)
    pending = path.with_suffix(".tmp")
    pending.unlink(missing_ok=True)
    with os.fdopen(os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as output:
        output.write(key)
    pending.replace(path)
    return key


def unlock_keyring(path):
    """A locked keyring would ask for a password behind the kiosk and lose the device identity.

    --replace takes over a daemon PAM may have started without unlocking anything.
    """
    subprocess.run(["/usr/bin/gnome-keyring-daemon", "--replace", "--daemonize", "--unlock", "--components=secrets"],
                   input=keyring_key(path).encode("ascii"), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                   timeout=15, check=True)


def tail(path):
    try:
        with path.open("rb") as output:
            output.seek(max(0, path.stat().st_size - TAIL_BYTES))
            return output.read().decode("utf-8", "replace")
    except OSError:
        return ""


def trim(path):
    """The output lives in the session's small tmpfs for as long as the app runs."""
    try:
        if path.stat().st_size > LOG_LIMIT:
            os.truncate(path, 0)
    except OSError:
        pass


def terminate(child):
    if child is None:
        return
    # Even if the GUI exited first, its bridge may still be in this process group.
    try:
        os.killpg(child.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        child.wait(timeout=3)
    except subprocess.TimeoutExpired:
        pass
    try:
        os.killpg(child.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    child.wait()


class Session:
    def __init__(self, root, runtime):
        import tkinter as tk
        self.root = root
        self.runtime = runtime
        state = os.environ.get("XDG_STATE_HOME") or str(Path.home() / ".local/state")
        self.log = Path(state) / "culinary-restaurant/kiosk.log"
        self.child = None
        self.wm = None
        self.guard = None
        self.nonce = ""
        self.retries = 0
        self.next_start = time.monotonic()
        self.visible = True
        self.stopping = False
        self.message = tk.StringVar(value="Preparando tu restaurante…")
        root.title("Culinary Restaurant · iniciando")
        root.configure(bg="#222d3d")
        root.overrideredirect(True)
        root.geometry(f"{root.winfo_screenwidth()}x{root.winfo_screenheight()}+0+0")
        root.protocol("WM_DELETE_WINDOW", lambda: None)
        root.bind("<Alt-F4>", lambda event: "break")
        content = tk.Frame(root, bg="#222d3d")
        content.place(relx=.5, rely=.5, anchor="center")
        icon = Path("/usr/share/icons/hicolor/256x256/apps/culinary-restaurant.png")
        if icon.is_file():
            self.icon = tk.PhotoImage(file=str(icon)).subsample(2)
            tk.Label(content, image=self.icon, bg="#222d3d").pack(pady=(0, 20))
        tk.Label(content, text="Culinary Restaurant", bg="#222d3d", fg="#f8fafc",
                 font=("DejaVu Sans", 30, "bold")).pack()
        tk.Label(content, textvariable=self.message, bg="#222d3d", fg="#f2b93b",
                 font=("DejaVu Sans", 14), wraplength=800).pack(pady=22)
        self.socket = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        path = runtime / "ready.sock"
        if path.is_symlink():
            raise PermissionError("Socket de sesion inesperado")
        path.unlink(missing_ok=True)
        self.socket.bind(str(path))
        os.chmod(path, 0o600)
        self.socket.setblocking(False)
        signal.signal(signal.SIGTERM, self.stop)
        signal.signal(signal.SIGINT, self.stop)
        root.after(250, self.tick)

    def unlock(self):
        try:
            unlock_keyring(self.log.with_name("keyring.key"))
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            append_log(self.log, f"{time.strftime('%Y-%m-%d %H:%M:%S')} Llavero sin desbloquear: {error}\n")

    def show_loading(self, message):
        self.message.set(message)
        if not self.visible:
            self.root.deiconify()
            self.root.lift()
            self.visible = True

    def start(self, now):
        self.nonce = secrets.token_hex(32)
        env = dict(os.environ, CULINARY_RESTAURANT_KIOSK="1",
                   CULINARY_RESTAURANT_KIOSK_NONCE=self.nonce,
                   # WebKitGTK's DMA-BUF path fails on some real GPUs and never runs on the
                   # software renderer of the test VMs, where shortcuts do work.
                   WEBKIT_DISABLE_DMABUF_RENDERER="1")
        output_path = self.runtime / "app-output.log"
        output_path.write_bytes(b"")
        with output_path.open("ab") as output:
            self.child = subprocess.Popen([APP], env=env, start_new_session=True,
                                          stdout=output, stderr=output)
        self.guard = Watchdog(now)

    def record_failure(self, now, reason):
        when = time.strftime("%Y-%m-%d %H:%M:%S")
        append_log(self.log, f"{when} Recuperando: {reason}, {now - self.guard.started:.0f} s "
                             f"despues de abrir\n{tail(self.runtime / 'app-output.log')}\n")

    def recover(self, now, reason=None):
        self.show_loading("Recuperando Culinary Restaurant…")
        if self.guard is not None:
            self.record_failure(now, reason or exit_cause(self.child and self.child.poll()))
        terminate(self.child)
        self.child = None
        self.retries += 1
        self.next_start = now + min(30, 2 ** min(self.retries, 5))
        if self.retries >= 5:
            self.message.set("La aplicación no pudo iniciar. Reintentando…\n"
                             "Mantenimiento: Ctrl+Alt+F2, cuenta administradora.")

    def tick(self):
        if self.stopping:
            return
        now = time.monotonic()
        try:
            if self.wm is None or self.wm.poll() is not None:
                self.wm = subprocess.Popen(["/usr/bin/openbox", "--config-file", CONFIG],
                                          start_new_session=True, stdout=subprocess.DEVNULL,
                                          stderr=subprocess.DEVNULL)
            if self.child is None and now >= self.next_start:
                self.start(now)
            while True:
                try:
                    raw = self.socket.recv(513)
                except BlockingIOError:
                    break
                if self.child and self.child.poll() is None and valid_ready(raw, self.child.pid, self.nonce):
                    self.guard.painted(now)
                    if self.visible:
                        self.root.withdraw()
                        self.visible = False
            trim(self.runtime / "app-output.log")
            if self.child and (self.child.poll() is not None or self.guard.expired(now)):
                self.recover(now)
            elif self.child and self.guard.last_paint is not None and now - self.guard.started > 120:
                self.retries = 0
        except OSError as error:
            self.recover(now, f"error de la sesion: {error}")
        finally:
            if not self.stopping:
                self.root.after(250, self.tick)

    def stop(self, *_):
        self.stopping = True
        self.root.quit()

    def close(self):
        terminate(self.child)
        terminate(self.wm)
        self.socket.close()
        (self.runtime / "ready.sock").unlink(missing_ok=True)


def main():
    import fcntl
    import pwd
    import tkinter as tk
    if os.getuid() != pwd.getpwnam("operador").pw_uid or os.getuid() == 0:
        raise PermissionError("Usar la sesion de operador, nunca root")
    runtime_base = Path(os.environ["XDG_RUNTIME_DIR"])
    if (not runtime_base.is_absolute() or runtime_base.is_symlink()
            or runtime_base.stat().st_uid != os.getuid()):
        raise PermissionError("Runtime de sesion no valido")
    runtime = runtime_base / "culinary-restaurant-kiosk"
    if runtime.is_symlink():
        raise PermissionError("Directorio de sesion no valido")
    runtime.mkdir(mode=0o700, exist_ok=True)
    os.chmod(runtime, 0o700)
    lock = (runtime / "session.lock").open("a")
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    root = tk.Tk()
    session = Session(root, runtime)
    # After drawing the loader and before the first tick starts the app.
    root.after(0, session.unlock)
    try:
        root.mainloop()
    finally:
        session.close()
        root.destroy()
        lock.close()


if __name__ == "__main__":
    main()

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

    def show_loading(self, message):
        self.message.set(message)
        if not self.visible:
            self.root.deiconify()
            self.root.lift()
            self.visible = True

    def start(self, now):
        self.nonce = secrets.token_hex(32)
        env = dict(os.environ, CULINARY_RESTAURANT_KIOSK="1",
                   CULINARY_RESTAURANT_KIOSK_NONCE=self.nonce)
        self.child = subprocess.Popen([APP], env=env, start_new_session=True,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.guard = Watchdog(now)

    def recover(self, now):
        self.show_loading("Recuperando Culinary Restaurant…")
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
            if self.child and (self.child.poll() is not None or self.guard.expired(now)):
                self.recover(now)
            elif self.child and self.guard.last_paint is not None and now - self.guard.started > 120:
                self.retries = 0
        except OSError:
            self.recover(now)
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
    # Start after drawing the loader. Secret Service keeps its existing encryption.
    root.after(0, lambda: subprocess.Popen(
        ["/usr/bin/gnome-keyring-daemon", "--start", "--components=secrets"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    try:
        root.mainloop()
    finally:
        session.close()
        root.destroy()
        lock.close()


if __name__ == "__main__":
    main()

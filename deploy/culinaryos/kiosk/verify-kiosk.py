#!/usr/bin/python3
"""Real Linux smoke test on a private Xvfb display and private D-Bus/keyring.
No user credentials, enrollment, payments or backend changes are performed.
"""
import ctypes
import ctypes.util
import json
import re
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import sys
import time


def active_window():
    value = subprocess.check_output(["xprop", "-root", "_NET_ACTIVE_WINDOW"], text=True)
    match = re.search(r"0x[0-9a-fA-F]+", value)
    return int(match.group(), 16) if match else 0


def pointer_click(x, y):
    x11 = ctypes.CDLL(ctypes.util.find_library("X11"))
    xtst = ctypes.CDLL(ctypes.util.find_library("Xtst"))
    x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    x11.XOpenDisplay.restype = ctypes.c_void_p
    x11.XFlush.argtypes = [ctypes.c_void_p]
    x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
    xtst.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
    xtst.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
    display = x11.XOpenDisplay(None)
    if not display:
        raise RuntimeError("No se pudo conectar a la pantalla de prueba")
    try:
        xtst.XTestFakeMotionEvent(display, -1, x, y, 0)
        xtst.XTestFakeButtonEvent(display, 1, 1, 0)
        xtst.XTestFakeButtonEvent(display, 1, 0, 0)
        x11.XFlush(display)
    finally:
        x11.XCloseDisplay(display)


def press_shortcuts():
    # Real key events, as a keyboard sends them. Ctrl+C is only a key here, never SIGINT.
    x11 = ctypes.CDLL(ctypes.util.find_library("X11"))
    xtst = ctypes.CDLL(ctypes.util.find_library("Xtst"))
    x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    x11.XOpenDisplay.restype = ctypes.c_void_p
    x11.XFlush.argtypes = [ctypes.c_void_p]
    x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
    x11.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    x11.XKeysymToKeycode.restype = ctypes.c_ubyte
    xtst.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
    display = x11.XOpenDisplay(None)
    if not display:
        raise RuntimeError("No se pudo conectar a la pantalla de prueba")
    try:
        control = x11.XKeysymToKeycode(display, 0xffe3)
        for letter in "acv":
            key = x11.XKeysymToKeycode(display, ord(letter))
            for code, pressed in ((control, 1), (key, 1), (key, 0), (control, 0)):
                xtst.XTestFakeKeyEvent(display, code, pressed, 0)
            x11.XFlush(display)
            time.sleep(.5)
    finally:
        x11.XCloseDisplay(display)


def verify_click_focus(window):
    # Reproduce losing focus to a second native window, then an actual mouse click.
    # Do not activate the app with wmctrl: that would conceal a broken mouse config.
    import tkinter as tk
    helper = tk.Tk()
    helper.title("Culinary focus probe")
    helper.geometry("180x80+0+0")
    try:
        helper.update()
        time.sleep(.3)
        helper.update()
        subprocess.run(["wmctrl", "-a", "Culinary focus probe"], check=True)
        deadline = time.monotonic() + 3
        while active_window() in (0, int(window, 16)) and time.monotonic() < deadline:
            helper.update()
            time.sleep(.05)
        if active_window() in (0, int(window, 16)):
            raise RuntimeError("No se pudo preparar la perdida de foco para la prueba")
        # Blank client area, outside the sign-in controls; no credential entry or submit.
        pointer_click(int(helper.winfo_screenwidth() * .95), int(helper.winfo_screenheight() * .95))
        deadline = time.monotonic() + 3
        while active_window() != int(window, 16) and time.monotonic() < deadline:
            helper.update()
            time.sleep(.05)
        if active_window() != int(window, 16):
            raise RuntimeError("El clic no devuelve el foco a Culinary Restaurant")
    finally:
        helper.destroy()


def main():
    binary = Path(sys.argv[1]).resolve(strict=True)
    scratch = Path(sys.argv[2]).resolve(strict=True)
    runtime = scratch / "runtime"
    runtime.mkdir(mode=0o700)
    ready_dir = runtime / "culinary-restaurant-kiosk"
    ready_dir.mkdir(mode=0o700)
    nonce = secrets.token_hex(32)
    listener = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
    listener.bind(str(ready_dir / "ready.sock"))
    listener.settimeout(60)
    # A fresh data/config HOME keeps this test away from the installed user's vault.
    env = dict(os.environ, HOME=str(scratch), XDG_RUNTIME_DIR=str(runtime),
               XDG_DATA_HOME=str(scratch / "data"), XDG_CONFIG_HOME=str(scratch / "config"),
               XDG_CACHE_HOME=str(scratch / "cache"), CULINARY_RESTAURANT_KIOSK="1",
               CULINARY_RESTAURANT_KIOSK_NONCE=nonce)
    wm = subprocess.Popen(["/usr/bin/openbox", "--config-file", str(Path(__file__).with_name("openbox.xml"))],
                          env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    child = None
    try:
        time.sleep(.6)
        child = subprocess.Popen([str(binary)], env=env, start_new_session=True,
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        reply = json.loads(listener.recv(1024))
        if reply != {"protocol": 1, "pid": child.pid, "nonce": nonce}:
            raise RuntimeError("Readiness no corresponde a la nueva interfaz")
        rows = subprocess.check_output(["wmctrl", "-l"], text=True).splitlines()
        matches = [row.split()[0] for row in rows if row.endswith(" Culinary Restaurant")]
        if len(matches) != 1:
            raise RuntimeError("No se encontro una unica ventana de la app")
        window = matches[0]
        state = subprocess.check_output(["xprop", "-id", window, "_NET_WM_STATE"], text=True)
        if "_NET_WM_STATE_FULLSCREEN" not in state:
            raise RuntimeError("La ventana no esta a pantalla completa")
        verify_click_focus(window)
        press_shortcuts()
        time.sleep(1)
        if child.poll() is not None:
            raise RuntimeError("Ctrl+A, Ctrl+C o Ctrl+V cerraron la interfaz")
        subprocess.run(["wmctrl", "-i", "-c", window], check=True)
        time.sleep(1)
        if child.poll() is not None:
            raise RuntimeError("La ventana permitio el cierre normal")
        # A second real renderer heartbeat proves the first datagram was not stale.
        reply = json.loads(listener.recv(1024))
        if reply != {"protocol": 1, "pid": child.pid, "nonce": nonce}:
            raise RuntimeError("La interfaz dejo de responder tras intentar cerrarla")
        print("Comprobacion grafica Linux: interfaz visible, fullscreen, foco por clic, atajos, cierre bloqueado y heartbeat correctos.")
    finally:
        if child is not None:
            try:
                os.killpg(child.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait()
        wm.terminate()
        try:
            wm.wait(timeout=3)
        except subprocess.TimeoutExpired:
            wm.kill()
            wm.wait()
        listener.close()


if __name__ == "__main__":
    main()

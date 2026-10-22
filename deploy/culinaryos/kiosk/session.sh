#!/bin/sh
set -eu
# LightDM keeps its existing PAM/autologin policy. Never run the GUI as root.
[ "$(id -un)" = operador ] || { echo 'Esta sesion es exclusiva de operador' >&2; exit 1; }
export XDG_CURRENT_DESKTOP=CulinaryRestaurant XDG_SESSION_DESKTOP=culinary-restaurant
export XDG_SESSION_TYPE=x11
if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ]; then
    exec /usr/bin/dbus-run-session -- /usr/bin/python3 /opt/culinaryos-app/kiosk/session.py
fi
exec /usr/bin/python3 /opt/culinaryos-app/kiosk/session.py

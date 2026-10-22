#!/bin/bash
set -Eeuo pipefail
[[ $(uname -s) == Linux && $EUID -eq 0 && -n ${SUDO_USER:-} && ${SUDO_UID:-0} -ne 0 ]] || {
    echo 'Ejecuta con sudo desde la cuenta administradora de CulinaryOS.' >&2; exit 1;
}
bundle=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
cd "$bundle"
sha256sum --strict --check SHA256SUMS
exec /usr/bin/python3 "$bundle/deploy/culinaryos/kiosk/install-kiosk.py" --bundle "$bundle"

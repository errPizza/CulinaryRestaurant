#!/bin/bash
# Run inside the installed Debian guest, from the attached data CD.
set -Eeuo pipefail
[[ $(uname -s) == Linux && $EUID -eq 0 ]] || { echo 'Ejecuta sudo bash instalar.sh dentro de CulinaryOS.'; exit 2; }
[[ -t 0 && -t 1 ]] || { echo 'Se necesita una terminal para tus credenciales iniciales.'; exit 2; }
# shellcheck source=/dev/null
. /etc/os-release
[[ ${ID:-} == debian && ${VERSION_CODENAME:-} == trixie && $(uname -m) == x86_64 ]] || { echo 'Requiere CulinaryOS Debian 13 amd64.'; exit 2; }
[[ -f /etc/culinaryos/profile && -f /etc/culinaryos/site.json ]] || { echo 'CulinaryOS no esta aprovisionado.'; exit 2; }
[[ -n ${SUDO_USER:-} && ${SUDO_UID:-0} -ne 0 ]] || { echo 'Entra con tu cuenta administradora y usa sudo.'; exit 2; }
media=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
# CulinaryServer, where this branch activates and renews its license; written when the CD was prepared.
server=$(head -n1 "$media/deploy/culinaryos/license-server.txt")
(cd "$media" && sha256sum --strict -c SHA256SUMS)
python3 "$media/deploy/culinaryos/install.py" preflight --bundle "$media"
if [[ ${1:-} == --resume ]]; then
  python3 "$media/deploy/culinaryos/install.py" resume --bundle "$media" --license-server "$server"
  echo 'Instalacion reanudada sin repetir apt ni la compilacion. Cierra y vuelve a abrir tu sesion para comprobar la app.'
  exit 0
fi
[[ $# -eq 0 ]] || { echo 'Uso: sudo bash instalar.sh [--resume]'; exit 2; }
# Debian's signed repositories provide system libraries; this does not add repositories.
apt-get update
apt-get install --no-install-recommends -y build-essential pkg-config curl ca-certificates \
  libwebkit2gtk-4.1-dev libssl-dev libayatana-appindicator3-dev librsvg2-dev libxdo-dev \
  gnome-keyring libsecret-tools libpam-gnome-keyring dbus-x11 libicu76 libssl3t64 zlib1g tpm2-tools
work=$(mktemp -d /var/tmp/culinary-restaurant-build.XXXXXX)
fs=$(findmnt -n -o FSTYPE -T "$work")
case "$fs" in ext4|ext3|btrfs|xfs) ;; *) echo 'Compilar en un disco Linux persistente.'; exit 2 ;; esac
cp -R -- "$media/client-source" "$media/toolchain" "$media/payload" "$work/"
chown -R -- "$SUDO_UID:$(id -g "$SUDO_USER")" "$work"
chmod -R u+rwX -- "$work"
install -o "$SUDO_UID" -g "$(id -g "$SUDO_USER")" -m 0755 "$media/deploy/culinaryos/build-client.sh" "$work/build-client.sh"
echo "Compilacion Linux: $work (la primera vez necesita internet y puede tardar)."
runuser -u "$SUDO_USER" -- bash "$work/build-client.sh" "$work"
# Do not install an unchecked result or any symlink from the build directory.
python3 "$media/deploy/culinaryos/install.py" install --bundle "$media" --client "$work/client-source/src-tauri/target/release/culinary-restaurant" --license-server "$server"
echo 'Instalacion terminada. Cierra la sesion de Linux y vuelve a entrar, o reinicia cuando lo desees.'

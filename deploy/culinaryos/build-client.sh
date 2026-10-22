#!/bin/bash
set -Eeuo pipefail
[[ $(uname -s) == Linux && $EUID -ne 0 && $# -eq 1 ]] || exit 2
work=$(realpath -- "$1")
export CARGO_HOME="$work/toolchain/cargo" RUSTUP_HOME="$work/toolchain/rustup"
export PATH="$CARGO_HOME/bin:$PATH" CARGO_BUILD_JOBS=2
chmod 0755 "$work/toolchain/rustup-init"
# Bundled rustup-init was checked by SHA256SUMS before copying from the read-only CD.
"$work/toolchain/rustup-init" -y --no-modify-path --profile minimal --default-toolchain 1.99.1
mkdir -p "$work/client-source/src-tauri/binaries"
cp -- "$work/payload/bridge/culinary-bridge" "$work/client-source/src-tauri/binaries/culinary-bridge-x86_64-unknown-linux-gnu"
chmod 0755 "$work/client-source/src-tauri/binaries/culinary-bridge-x86_64-unknown-linux-gnu"
cd "$work/client-source/src-tauri"
export TAURI_CONFIG
TAURI_CONFIG=$(cat tauri.linux.conf.json)
cargo build --release --locked --features tauri/custom-protocol

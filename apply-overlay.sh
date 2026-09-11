#!/usr/bin/env bash
set -euo pipefail
# CI overlay installer for FreeRDP 3.31.1
if [[ $# -ne 1 ]]; then
  echo "Usage: $0 /path/to/freerdp-3.31.1" >&2
  exit 2
fi
ROOT="$(cd "$1" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"

cp -a "$HERE/freerdp-overlay/channels/rdptun" "$ROOT/channels/"
cp -a "$HERE/freerdp-overlay/client/Android/Studio/rdpTunnel" "$ROOT/client/Android/Studio/"

SETTINGS="$ROOT/client/Android/Studio/settings.gradle"
if ! grep -q "include ':rdpTunnel'" "$SETTINGS"; then
  printf "\ninclude ':rdpTunnel'\n" >> "$SETTINGS"
fi

echo "Overlay applied to $ROOT"
echo "Android Studio project: $ROOT/client/Android/Studio"

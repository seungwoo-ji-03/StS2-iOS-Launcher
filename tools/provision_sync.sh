#!/bin/bash
# Copy your Steam login (refresh token from `make login`) and, optionally, a multiplayer config into the
# installed app's Documents folder. The app reads user://steam_token.json and user://mp_config.json.
#
# usage: tools/provision_sync.sh [bridge-ip [steamid64]]
# env:   DEV (device UDID/identifier), BUNDLE (bundle id)
set -euo pipefail
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
DEV="${DEV:?set DEV to your device identifier (xcrun devicectl list devices)}"
BUNDLE="${BUNDLE:?set BUNDLE}"
TOK="$HOME/Library/Application Support/stssync/token.json"
[ -f "$TOK" ] || { echo "no Steam login yet: run make login"; exit 1; }

# The token grants access to your Steam account: stage it in a private directory that is always removed.
STAGE="$(mktemp -d)"; chmod 700 "$STAGE"; trap 'rm -rf "$STAGE"' EXIT
chmod 600 "$TOK"
cp "$TOK" "$STAGE/steam_token.json"
xcrun devicectl device copy to --device "$DEV" --domain-type appDataContainer --domain-identifier "$BUNDLE" \
  --source "$STAGE/steam_token.json" --destination "Documents/steam_token.json" >/dev/null
echo "Steam login copied to the app"

if [ -n "${1:-}" ]; then
  printf '{ "ip": "%s", "port": 33771, "netId": "%s" }\n' "$1" "${2:-}" > "$STAGE/mp_config.json"
  xcrun devicectl device copy to --device "$DEV" --domain-type appDataContainer --domain-identifier "$BUNDLE" \
    --source "$STAGE/mp_config.json" --destination "Documents/mp_config.json" >/dev/null
  echo "multiplayer config: ip=$1 netId=${2:-}"
fi

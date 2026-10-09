#!/bin/bash
# Turn a Godot iOS export into the installable app:
#   exported .app  +  iOS pck (tools/pck/build_pck.py)  +  Info.plist edits  +  release_info.json  ->  sign
#
# usage: tools/assemble.sh <exported .app> <ios pck> <game Resources dir> <out .app>
# env:   SIGN_ID   code-signing identity (SHA-1 or name, see `security find-identity -v -p codesigning`)
#        TEAM      Apple team ID (the OU field of your signing certificate)
#        BUNDLE    bundle identifier
#        ICON      optional square PNG for the home-screen icon
set -euo pipefail
EXP="${1:?}"; PCK="${2:?}"; RES="${3:?}"; OUT="${4:?}"
: "${SIGN_ID:?set SIGN_ID}" "${TEAM:?set TEAM}" "${BUNDLE:?set BUNDLE}"
log(){ echo "[assemble] $*"; }
PB=/usr/libexec/PlistBuddy

[ -d "$EXP" ] || { echo "no export at $EXP"; exit 1; }
[ -f "$PCK" ] || { echo "no pck at $PCK (make pck)"; exit 1; }
NEW="$OUT.new"; rm -rf "$NEW"; cp -Rc "$EXP" "$NEW"

log "pck: $(du -h "$PCK" | cut -f1)"
cp -c "$PCK" "$NEW/sts2.pck"

# The game reads its version from here (multiplayer compatibility checks, save metadata).
cp "$RES/release_info.json" "$NEW/release_info.json"

P="$NEW/Info.plist"
# both landscape orientations (Godot writes only the one from the export preset)
for key in UISupportedInterfaceOrientations "UISupportedInterfaceOrientations~ipad"; do
  $PB -c "Delete :$key" "$P" 2>/dev/null || true
  $PB -c "Add :$key array" -c "Add :$key:0 string UIInterfaceOrientationLandscapeLeft" \
      -c "Add :$key:1 string UIInterfaceOrientationLandscapeRight" "$P"
done
# LAN multiplayer (ENet) needs the local-network permission prompt text
$PB -c "Delete :NSLocalNetworkUsageDescription" "$P" 2>/dev/null || true
$PB -c "Add :NSLocalNetworkUsageDescription string Connect to nearby players for multiplayer." "$P"
$PB -c "Set :CFBundleIdentifier $BUNDLE" "$P"
plutil -lint "$P" >/dev/null

if [ -n "${ICON:-}" ]; then
  sips -s format png -z 120 120 "$ICON" --out "$NEW/AppIcon60x60@2x.png" >/dev/null
  sips -s format png -z 152 152 "$ICON" --out "$NEW/AppIcon76x76@2x~ipad.png" >/dev/null
  log "icon: $ICON"
fi

ENT="$(mktemp -t sts2ent).plist"
cat >"$ENT" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>application-identifier</key><string>$TEAM.$BUNDLE</string>
  <key>com.apple.developer.team-identifier</key><string>$TEAM</string>
  <key>get-task-allow</key><true/>
</dict></plist>
PLIST

log "sign (inside-out)"
rm -rf "$NEW/_CodeSignature"; find "$NEW/Frameworks" -name _CodeSignature -exec rm -rf {} + 2>/dev/null || true
find "$NEW/Frameworks" -name '*.framework' | while read -r fw; do
  b="$fw/$(basename "$fw" .framework)"
  [ -f "$b" ] && codesign --force --timestamp=none --sign "$SIGN_ID" "$b" >/dev/null 2>&1
  codesign --force --timestamp=none --sign "$SIGN_ID" "$fw" >/dev/null 2>&1
done
find "$NEW/Frameworks" -name '*.dylib' -exec codesign --force --timestamp=none --sign "$SIGN_ID" {} \; 2>/dev/null || true
codesign --force --timestamp=none --sign "$SIGN_ID" "$NEW/sts2" >/dev/null 2>&1
codesign --force --timestamp=none --sign "$SIGN_ID" --entitlements "$ENT" "$NEW" >/dev/null 2>&1
codesign --verify --strict "$NEW"
rm -f "$ENT"

rm -rf "$OUT.prev"; [ -d "$OUT" ] && mv "$OUT" "$OUT.prev"; mv "$NEW" "$OUT"
log "ready: $OUT (previous kept as $(basename "$OUT").prev)"

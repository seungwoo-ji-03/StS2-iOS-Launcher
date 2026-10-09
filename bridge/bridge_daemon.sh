#!/bin/bash
# Keeps the multiplayer bridge running: relaunches the desktop game with --bridge whenever it is not running
# and Steam is. Used by the opt-in LaunchAgent (bridge/com.sts2.bridge.plist).
# env: GAME_APP (default: the standard Steam install location)
GAME_APP="${GAME_APP:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app}"
EXE="$GAME_APP/Contents/MacOS/Slay the Spire 2"
LOG="${TMPDIR:-/tmp}/sts2_bridge.log"
while true; do
  if ! pgrep -f "Slay the Spire 2.*--bridge" >/dev/null 2>&1; then
    if pgrep -x steam_osx >/dev/null 2>&1; then
      echo "[daemon $(date '+%H:%M:%S')] bridge not running -> launching"
      (cd "$(dirname "$EXE")" && SteamAppId=2868840 SteamGameId=2868840 nohup "$EXE" --bridge >>"$LOG" 2>&1 &)
    fi
  fi
  sleep 8
done

#!/usr/bin/env python3
"""Print the Steam display name (PersonaName) for a SteamID64 from Steam's loginusers.vdf, if present."""

import os
import re
import sys

steamid = sys.argv[1] if len(sys.argv) > 1 else ""
path = os.path.expanduser("~/Library/Application Support/Steam/config/loginusers.vdf")
try:
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()
except OSError:
    sys.exit(0)
m = re.search(rf'"{re.escape(steamid)}"\s*\{{(.*?)\}}', text, re.DOTALL)
if m:
    n = re.search(r'"PersonaName"\s+"([^"]*)"', m.group(1))
    if n:
        print(n.group(1))

"""Register bridgemgr.dll in the desktop game's <app>.deps.json.

This lets the game's isolated AssemblyLoadContext resolve it. bridgemgr is added as a "reference"
entry of the RID-specific target, the same way a local dll such as Steamworks.NET is listed.
Idempotent; the original is backed up once as .orig.
"""

import json
import os
import shutil
import sys


def main(deps_path):
    if not os.path.isfile(deps_path):
        sys.exit(f"deps.json not found: {deps_path}")
    if not os.path.isfile(deps_path + ".orig"):
        shutil.copyfile(deps_path, deps_path + ".orig")
    with open(deps_path) as f:
        d = json.load(f)
    # The RID-specific target (e.g. ".NETCoreApp,Version=v9.0/osx-arm64") holds the runtime assemblies.
    targets = d.get("targets", {})
    rid_key = next((k for k in targets if "/" in k), None)
    if not rid_key:
        sys.exit("no RID-specific target found in deps.json")
    targets[rid_key]["bridgemgr/0.0.0.0"] = {
        "runtime": {
            "bridgemgr.dll": {"assemblyVersion": "0.0.0.0", "fileVersion": "0.0.0.0"}
        }
    }
    d.setdefault("libraries", {})["bridgemgr/0.0.0.0"] = {
        "type": "reference",
        "serviceable": False,
        "sha512": "",
    }
    with open(deps_path, "w") as f:
        json.dump(d, f, indent=2)
    print(f"registered bridgemgr in {rid_key}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: register_bridgemgr_deps.py <path-to-sts2.deps.json>")
    main(sys.argv[1])

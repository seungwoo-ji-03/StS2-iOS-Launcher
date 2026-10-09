#!/usr/bin/env python3
"""Turn the desktop Slay the Spire 2 .pck (from your own Steam install) into the iOS pck, in one pass.

build_pck.py extract <game.pck> <workdir>
    Copy every .ctex texture to <workdir>/t/<i>.ctex and write the Godot stub project that
    transcode.gd runs in. Then run:  godot --headless --path <workdir> -s res://transcode.gd

build_pck.py build <game.pck> <workdir> <out.pck>
    Write the iOS pck:
      * native extension descriptors point at the iOS FMOD / spine builds
      * textures replaced by the ASTC versions from <workdir>/o/
      * project settings overridden (renderer, orientation, frame pacing; see OVERRIDES)
      * shader fixes applied (see SHADER_PATCHES)
    Everything else is copied byte for byte.

build_pck.py verify <game.pck> <reference.pck>
    Check that `build` reproduces a known-good iOS pck (non-texture entries; textures are taken
    from the reference).
"""

import json
import os
import shutil
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from pcklib import Pack, ecfg_decode, ecfg_set, variant_repr, write_pack

FMOD_GDEXT = b"""[configuration]
entry_symbol = "fmod_library_init"
compatibility_minimum = 4.5

[libraries]
ios.release = "res://addons/fmod/libGodotFmod.ios.template_release.xcframework"
ios.debug = "res://addons/fmod/libGodotFmod.ios.template_release.xcframework"
"""
SPINE_GDEXT = b"""[configuration]
entry_symbol = "spine_godot_library_init"
compatibility_minimum = "4.1"

[libraries]
ios.release = "res://addons/spine/libspine_godot.ios.template_release.framework"
ios.debug = "res://addons/spine/libspine_godot.ios.template_release.framework"
"""
EXT_LIST = b"res://addons/fmod/fmod.gdextension\nres://addons/spine/spine_godot_extension.gdextension\n"
FILE_REPLACEMENTS = {
    "addons/fmod/fmod.gdextension": FMOD_GDEXT,
    "addons/spine/spine_godot_extension.gdextension": SPINE_GDEXT,
    ".godot/extension_list.cfg": EXT_LIST,
}

# Project settings for iPhone.
OVERRIDES = {
    # The Forward+/Mobile renderers precompile an uber-shader pipeline set that alone needs ~1.9 GB on
    # iOS and gets the app killed on 4 GB devices. Compatibility (GLES3) has no such cost.
    "rendering/renderer/rendering_method": "gl_compatibility",
    "rendering/renderer/rendering_method.mobile": "gl_compatibility",
    # 4 = SCREEN_SENSOR_LANDSCAPE: both landscape orientations (the desktop build is fixed landscape).
    "display/window/handheld/orientation": 4,
    # Render at the base resolution and scale the result, instead of rendering canvas items at full
    # device resolution: large GPU saving on high-DPI screens.
    "display/window/stretch/mode": "viewport",
    # A card game: redraw only when something changes, cap at 30 fps, 30 Hz physics. Heat and battery.
    "application/run/low_processor_mode": True,
    "application/run/low_processor_mode_sleep_usec": 16666,
    "application/run/max_fps": 30,
    "physics/common/physics_ticks_per_second": 30,
}

# Exact text edits. The game's shaders are not redistributed; these apply to your own copy.
# scry_reveal: dynamic uniform-array indexing that can go out of bounds. Desktop GPUs tolerate it; Apple's
# OpenGL-on-Metal raises a GPU read fault and the app is killed.
SHADER_PATCHES = {
    "shaders/scry_reveal.gdshader": [
        (
            "int index = int(floor(gridpos.y * gridSizef + gridpos.x));",
            "int index = clamp(int(floor(gridpos.y * gridSizef + gridpos.x)), 0, gridSize * gridSize - 1);",
        ),
        (
            "int index_n = int(floor(gridpos_n.y) * gridSizef + gridpos_n.x);",
            "int index_n = clamp(int(floor(gridpos_n.y) * gridSizef + gridpos_n.x), 0, gridSize * gridSize - 1);",
        ),
        ("vec4 curData = circleData[i];", "vec4 curData = circleData[clamp(i, 0, 7)];"),
        (
            "col = mix(colors[i+1], col, mask);",
            "col = mix(colors[clamp(i + 1, 0, 7)], col, mask);",
        ),
    ],
}

STUB_PROJECT = """config_version=5

[application]
config/name="transcode"
"""


def die(msg):
    print("error: " + msg, file=sys.stderr)
    sys.exit(1)


def patch_shader(path, data):
    text = data.decode("utf-8")
    for old, new in SHADER_PATCHES[path]:
        if new in text:
            continue  # already applied
        if text.count(old) != 1:
            die(
                f'{path}: expected exactly one "{old}" (shader changed by a game update?)'
            )
        text = text.replace(old, new)
    return text.encode("utf-8")


def textures(pack):
    return [e for e in pack.entries if e["path"].endswith(".ctex")]


def cmd_extract(game, work):
    src = Pack(game)
    tdir = os.path.join(work, "t")
    odir = os.path.join(work, "o")
    os.makedirs(tdir, exist_ok=True)
    # transcode.gd resumes by skipping o/<i>.ctex files that exist. After a game update the index -> texture
    # mapping changes, so drop every previous result whose source texture is not the same one anymore.
    old = {}
    mpath = os.path.join(work, "manifest.json")
    if os.path.exists(mpath):
        with open(mpath) as f:
            old = {m["idx"]: (m["path"], m["md5"]) for m in json.load(f)}
    kept = dropped = 0
    for i, e in enumerate(textures(src)):
        o = os.path.join(odir, f"{i}.ctex")
        if os.path.exists(o):
            if old.get(i) == (e["path"], e["md5"].hex()):
                kept += 1
            else:
                os.remove(o)
                dropped += 1
    manifest = []
    for i, e in enumerate(textures(src)):
        with open(os.path.join(tdir, f"{i}.ctex"), "wb") as out:
            src.copy_to(e, out)
        manifest.append({"idx": i, "path": e["path"], "md5": e["md5"].hex()})
    with open(mpath, "w") as f:
        json.dump(manifest, f)
    with open(os.path.join(work, "count.txt"), "w") as f:
        f.write(str(len(manifest)))
    with open(os.path.join(work, "project.godot"), "w") as f:
        f.write(STUB_PROJECT)
    here = os.path.dirname(os.path.abspath(__file__))
    shutil.copy(os.path.join(here, "transcode.gd"), os.path.join(work, "transcode.gd"))
    print(
        f"extracted {len(manifest)} textures to {tdir} (reusing {kept} transcoded, {dropped} changed)"
    )


def build(game, out_path, texture_for):
    src = Pack(game)
    stats = {"textures": 0, "files": 0, "shaders": 0, "settings": 0}

    def replace(e):
        p = e["path"]
        if p.endswith(".ctex"):
            data = texture_for(e)
            if data is not None:
                stats["textures"] += 1
            return data
        if p in FILE_REPLACEMENTS:
            stats["files"] += 1
            return FILE_REPLACEMENTS[p]
        if p in SHADER_PATCHES:
            stats["shaders"] += 1
            return patch_shader(p, src.read(p))
        if p == "project.binary":
            stats["settings"] += 1
            return ecfg_set(src.read(p), OVERRIDES)
        return None

    required = [*FILE_REPLACEMENTS, *SHADER_PATCHES, "project.binary"]
    missing = [p for p in required if p not in src.by_path]
    if missing:
        die(f"game pack lacks {missing} (unexpected game layout)")
    write_pack(src, out_path, replace)
    print(
        f"wrote {out_path}: {stats['textures']} textures, {stats['files']} extension files, "
        f"{stats['shaders']} shader fixes, {len(OVERRIDES)} settings"
    )
    return stats


def cmd_build(game, work, out_path):
    manifest_path = os.path.join(work, "manifest.json")
    if not os.path.exists(manifest_path):
        die(f"{manifest_path} missing: run `extract` and the transcode first")
    with open(manifest_path) as f:
        manifest = json.load(f)
    src_md5 = {e["path"]: e["md5"].hex() for e in Pack(game).entries}
    by_path = {}
    for m in manifest:
        if src_md5.get(m["path"]) != m["md5"]:
            die(
                f"{m['path']} changed since extraction (game updated?): re-run extract + transcode"
            )
        o = os.path.join(work, "o", f"{m['idx']}.ctex")
        if not os.path.exists(o):
            die(f"{o} missing: transcode incomplete")
        by_path[m["path"]] = o

    def texture_for(e):
        with open(by_path[e["path"]], "rb") as f:
            return f.read()

    build(game, out_path, texture_for)


def cmd_verify(game, reference):
    ref = Pack(reference)
    tmp = reference + ".verify.pck"
    build(game, tmp, lambda e: ref.read(e["path"]))
    out = Pack(tmp)
    bad = []
    for e in out.entries:
        r = ref.by_path.get(e["path"])
        if r is None:
            bad.append(e["path"] + " (missing in reference)")
        elif e["path"] == "project.binary":
            a = {k: variant_repr(v) for k, v in ecfg_decode(out.read("project.binary"))}
            b = {k: variant_repr(v) for k, v in ecfg_decode(ref.read("project.binary"))}
            if a != b:
                diff = {
                    k: (a.get(k), b.get(k))
                    for k in set(a) | set(b)
                    if a.get(k) != b.get(k)
                }
                bad.append(f"project.binary: {diff}")
        elif e["md5"] != r["md5"]:
            bad.append(e["path"])
    os.remove(tmp)
    print(f"verify: {len(out.entries)} entries, {len(bad)} differ")
    for b in bad[:20]:
        print("  " + b)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) == 3 and a[0] == "extract":
        cmd_extract(a[1], a[2])
    elif len(a) == 4 and a[0] == "build":
        cmd_build(a[1], a[2], a[3])
    elif len(a) == 3 and a[0] == "verify":
        cmd_verify(a[1], a[2])
    else:
        print(__doc__)
        sys.exit(2)

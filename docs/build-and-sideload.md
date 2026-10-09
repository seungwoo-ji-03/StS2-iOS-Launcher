# Build pipeline in detail

`make build` runs these stages in order. Each reads only your Steam install and this repository, and writes to
`gameapp/libs` or `WORKDIR` (set in `config.mk`).

| Stage | Input | Output | Notes |
|---|---|---|---|
| `make libs` | `…/data_sts2_macos_arm64/*.dll` | `gameapp/libs/`, `sts2.dll.orig` | a bridge-patched desktop install is detected by its `sts2.dll.orig` |
| `make syncmgr` | `syncmgr/` | `gameapp/libs/syncmgr.dll`, `Google.Protobuf.dll` | in-app sync, iOS lifecycle, touch input |
| `make patch` | `sts2.dll.orig` | `gameapp/libs/sts2.dll` | `cecilpatch --game --sync --quit-exit --fps-default=30` (+ `--mp …` when `MP=1`) |
| `make natives` (once) | FMOD iOS SDK, pinned upstream sources | `gameapp/addons/{fmod,spine}` | fmod-gdextension `b74dd11` + `patches/fmod-gdextension`, spine-runtimes branch `4.2` |
| `make textures` | game `.pck` | `WORKDIR/textures/o/*.ctex` | 3,485 textures to ASTC, ~10 min; resumable, redoes only changed textures |
| `make pck` | game `.pck`, transcoded textures | `WORKDIR/sts2_ios.pck` | one pass; see below |
| `make export` | `gameapp/` | `gameapp/build/sts2.xcarchive` | Godot `--export-release` (NativeAOT, ~10 min); team/bundle injected into the preset only for the export |
| `make app` | export + pck | `WORKDIR/sts2.app` | pck, `release_info.json`, Info.plist keys, icon, inside-out signing |

## The iOS game pack

`tools/pck/build_pck.py build` copies the game pack and changes exactly these entries:

- 3,485 `.ctex` textures: desktop S3TC/BPTC to ASTC (4x4 under 1024 px, 8x8 above), full resolution
- `addons/fmod/fmod.gdextension`, `addons/spine/spine_godot_extension.gdextension`,
  `.godot/extension_list.cfg`: point at the iOS builds, drop the desktop-only Sentry extension
- `shaders/scry_reveal.gdshader`: four array indexes clamped (GPU fault on Apple's OpenGL-on-Metal)
- `project.binary`: the eight settings in `OVERRIDES`, each commented in the script

`build_pck.py verify <game.pck> <known-good.pck>` checks the recipe against a known-good iOS pack. On v0.107.1
all 12,328 entries match, and a fresh transcode reproduces all 3,485 textures byte for byte.

## Why the export works

- The stub Godot project (`gameapp/`) has no game code. `sts2.csproj` replaces its empty `sts2.dll` with the
  game's patched one just before the ILC compiler reads its inputs, so the NativeAOT image is the game.
- `sts2_rd.xml` and `IlcTrimMetadata=false` keep the reflection metadata Godot needs to instantiate the game's
  600+ script classes.
- Only `--export-release` builds run on device; a debug export shows a black screen.

## Signing

The Godot export signs with automatic provisioning for your team, so the exported app already embeds a profile.
`tools/assemble.sh` re-signs inside-out after changing the bundle. It signs framework binaries, then frameworks,
then dylibs, then the main binary, then the bundle with minimal entitlements
(`application-identifier`, `team-identifier`, `get-task-allow`). `make resign` fetches a fresh 7-day profile
through `xcodebuild -allowProvisioningUpdates` and re-signs without a new export.

Install over the existing app (`make install`); uninstalling deletes the app's saves and Steam login.

## Multiplayer build

With `MP=1`, `make patch` adds `--mp --mp-config --mp-join=… --mp-netid=… --mp-name=…`. The join address can be
changed later without rebuilding by `make provision` (writes `mp_config.json` into the app). The PC side is
`make bridge-setup` once and `make bridge-run` per session; for an always-on bridge, see
`bridge/com.sts2.bridge.plist` and `bridge/bridge_daemon.sh`. All players need the same game build.

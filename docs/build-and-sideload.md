# Build pipeline in detail

## Overview

iOS forbids JIT compilation, so the Android approach (download at runtime, patch with Harmony) cannot work.
Everything is done ahead of time on a Mac:

```
your Steam install ─┬─ game assemblies ──► cecilpatch (build-time IL edits) ──► Godot iOS export (NativeAOT) ─┐
                    └─ game pack (.pck) ──► ASTC textures + iOS settings + shader fix ─────────────────────────┴─► signed app ─► iPhone
```

- **Assembly patches** (`tools/cecilpatch`): iOS has no Steam client, so the game's Steam platform init is
  replaced with a local one. The patches also add the cloud-sync hook and make "Quit" end the app. Nothing is
  patched at run time.
- **Game pack** (`tools/pck/build_pck.py`): desktop textures (S3TC/BPTC) are re-encoded to ASTC so the
  iPhone GPU can read them. The renderer switches to Compatibility (GLES3), because Forward+'s pipeline cache
  alone exceeds the memory limit. Frame pacing drops to 30 fps, and one shader with out-of-bounds array reads is
  fixed; Apple's GPU faults on those reads.
- **Native extensions** (`tools/build_natives.sh`): the game's FMOD and Spine GDExtensions are rebuilt for
  iOS from their official sources.

## Stages

`make build` runs these stages in order. Each reads only your Steam install and this repository, and writes to
`gameapp/libs` or `WORKDIR` (set in `config.mk`).

| Stage | Input | Output | Notes |
|---|---|---|---|
| `make libs` | `…/data_sts2_macos_arm64/*.dll` | `gameapp/libs/`, `sts2.dll.orig` | a bridge-patched desktop install is detected by its `sts2.dll.orig` |
| `make syncmgr` | `syncmgr/` | `gameapp/libs/syncmgr.dll`, `Google.Protobuf.dll` | in-app sync and iOS lifecycle |
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

## Staying signed

With a free Apple ID the signature expires after 7 days and the app will not open. Run `make resign`, which
keeps your saves, or install through [AltStore](https://altstore.io) with `make ipa`. AltStore re-signs in the
background whenever the phone and your Mac share a network.

## Game updates

The phone runs the game version you built. After Steam updates the game, run `make build install` again.
Only textures that changed are re-encoded. **Use the same game branch (public or beta) on every device you
sync.** An older version drops content it does not know when it loads a save. The sync refuses to upload such a
save over a newer one, but play is smoother when the versions match.

## Troubleshooting

| Symptom | Fix |
|---|---|
| App does not open, "untrusted developer" | Settings > General > VPN & Device Management > trust |
| App does not open after a week | `make resign` (free Apple ID) |
| Saves not syncing | `make logs` copies the app's `sync_log.txt` to your Mac; `make provision` again if it says the login was rejected |
| Black screen after export | Only `--export-release` builds work on device; use `make export`, not the editor |
| `make natives` fails finding the SDK | Install Xcode; the Command Line Tools alone have no iOS SDK |

## Repository layout

| Path | What |
|---|---|
| `Makefile`, `config.mk.example` | the whole workflow |
| `tools/cecilpatch/` | build-time assembly patcher (Mono.Cecil) |
| `tools/pck/` | iOS game pack builder and ASTC transcoder |
| `tools/build_natives.sh`, `patches/` | FMOD and Spine iOS builds |
| `tools/assemble.sh` | app bundle assembly and signing |
| `syncmgr/` | in-app sync and iOS lifecycle; `syncmgr/tests/` offline tests (`make test`) |
| `tools/stssync/` | desktop Steam login and save sync |
| `bridge/` | multiplayer PC bridge |
| `gameapp/` | Godot project used for the export (no game code; assemblies are copied in by `make libs`) |
| `docs/` | design notes, measurements, [ROADMAP](docs/ROADMAP.md) |

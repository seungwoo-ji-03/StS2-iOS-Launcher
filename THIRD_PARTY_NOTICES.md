# Third-party notices

This repository's own code is MIT-licensed (see [LICENSE](LICENSE)). It builds on the components below.
Nothing from Slay the Spire 2 is included: the game's code, assets, audio and packs come from **your own
Steam copy** at build time and stay on your Mac and your phone.

## Shipped inside the app you build

| Component | License | Where |
|---|---|---|
| [Godot Engine](https://godotengine.org) 4.5.1 (.NET export templates) | MIT | engine runtime of the app |
| [MoltenVK](https://github.com/KhronosGroup/MoltenVK) | Apache-2.0 | part of Godot's iOS export template |
| [Google.Protobuf](https://github.com/protocolbuffers/protobuf) 3.28.3 | BSD-3-Clause | `syncmgr` (in-app save sync) |
| [fmod-gdextension](https://github.com/utopia-rise/fmod-gdextension) (utopia-rise) | MIT | FMOD bridge, built by `make natives` with `patches/fmod-gdextension/` |
| [godot-cpp](https://github.com/godotengine/godot-cpp) | MIT | used by both native extensions |
| [spine-runtimes / spine-godot](https://github.com/EsotericSoftware/spine-runtimes) 4.2 | [Spine Runtimes License](https://esotericsoftware.com/spine-runtimes-license) | built by `make natives` |
| [FMOD Engine](https://www.fmod.com) iOS | proprietary (Firelight Technologies) | **not redistributed**: you download it from fmod.com and accept FMOD's license |

The Spine runtime license permits use with software that uses Spine content licensed to its developer
(Mega Crit, for this game). FMOD is free for non-commercial use under FMOD's own terms. Both are built
locally from their official sources for use with a copy of the game you own.

## Steam protocol definitions

`syncmgr/proto/*.proto` and the code generated from them in `syncmgr/gen/` are small excerpts of the Steam
client's network protocol as published by [SteamDatabase/Protobufs](https://github.com/SteamDatabase/Protobufs).
They describe Valve's protocol, are used only for interoperability (reading and writing your own Steam Cloud
saves) and are **not** covered by this repository's MIT license.

## Desktop tools only (not in the app)

| Component | License | Used by |
|---|---|---|
| [Mono.Cecil](https://github.com/jbevain/cecil) 0.11.5 | MIT | `tools/cecilpatch` (build-time assembly patcher) |
| [SteamKit2](https://github.com/SteamRE/SteamKit) 3.4.0 | LGPL-2.1 | `tools/stssync` (desktop Steam login and save sync) |
| [QRCoder](https://github.com/codebude/QRCoder) 1.6.0 | MIT | `tools/stssync` (login QR code) |
| System.Reflection.Metadata | MIT | analysis tools under `tools/` |

These are restored from NuGet when you build; no binaries are committed here.

## Trademarks

Slay the Spire and Mega Crit are trademarks of Mega Crit Games. Steam is a trademark of Valve Corporation.
iPhone and iOS are trademarks of Apple Inc. This project is not affiliated with, endorsed by or sponsored by
any of them.

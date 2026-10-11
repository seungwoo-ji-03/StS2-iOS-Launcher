# StS2-iOS-Launcher

**Build Slay the Spire 2 as a native iPhone app from your own Steam copy.** The game runs on the phone,
offline, and shares Steam Cloud saves with your PC.

[한국어 README](README.ko.md)

<p align="center">
  <a href="docs/media/gameplay.mp4"><img src="docs/media/combat.webp" width="720" alt="Slay the Spire 2 running on an iPhone 12: the first fight of a run"></a>
</p>
<p align="center"><sub>Running on an iPhone 12, captured through iPhone Mirroring.
<a href="docs/media/gameplay.mp4">Full clip (50 s)</a>: launch, character select, map and the first fight.</sub></p>

| | |
|:---:|:---:|
| <img src="docs/media/menu.jpg" alt="Main menu"> | <img src="docs/media/map.jpg" alt="Act 1 map"> |
| Main menu | Map |
| <img src="docs/media/combat.jpg" alt="Combat"> | <img src="docs/media/reward.jpg" alt="Card reward"> |
| Combat | Card reward |

> [!IMPORTANT]
> This repository contains **no game files** (only gameplay screenshots), just build tools. You build from the copy **you bought on Steam**.
> Unofficial; not affiliated with Mega Crit or Valve. **Never share the app you build.** Use at your own risk.
> Rights holders: open an issue.

## Features

- The full game on iPhone (tested on iPhone 12, iOS 18)
- Automatic Steam Cloud saves: continue a run from your PC on the phone and back
- Optional multiplayer with Steam friends, using your PC as a relay

## Requirements

- Mac with Apple silicon, Xcode, `brew install scons dotnet protobuf`
- Godot 4.5.1 .NET with export templates
- Slay the Spire 2 installed from Steam
- [FMOD Engine for iOS](https://www.fmod.com/download) (free, registration required)
- An Apple ID (free works, but the app must be re-signed every 7 days)

## Install

```bash
make config      # fill in config.mk
make natives     # once, ~10 min
make build       # ~20 min the first time
make install
make login       # Steam Cloud: scan the QR code with the Steam app
make provision
```

On first launch, trust the developer in Settings > General > VPN & Device Management.
With a free Apple ID, run `make resign` every 7 days.

## Limits

- Touch works as tap = click, drag = drag. There is no right click, and the game shows 16:9 with side bars.
- Long fights warm the phone. 30 fps is the default.
- No achievements or Steam overlay on the phone.

## Docs

[Build and troubleshooting](docs/build-and-sideload.md) · [Save sync](docs/in-app-sync-design.md) ·
[Multiplayer](docs/architecture.md) · [Roadmap](docs/ROADMAP.md)

## License

MIT ([LICENSE](LICENSE)); third-party licenses in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Slay the Spire 2 belongs to Mega Crit. Thanks to [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher)
and [StS2-Launcher-Overhaul](https://github.com/SocialHummingbird/StS2-Launcher-Overhaul).

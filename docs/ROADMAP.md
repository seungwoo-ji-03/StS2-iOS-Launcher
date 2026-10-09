# Release roadmap (public release target: week of 2026-09-25)

Status legend: [ ] todo · [~] in progress · [x] done. "Verified" means checked by tests or byte comparison unless it says "on device".

## P0 — save safety (sync)
- [x] D1 boot pull landed after the game loaded saves → blocking pull of live files inside `Boot()` (runs before `GameStartup` reads them), history async; live files never written after the window closes
- [x] Three-way reconcile with a SHA1 manifest as common base (local-only change → push, cloud-only → pull, both → progress compare with loser backed up); replaces timestamp manifest (clock skew, wrong-ts deletion)
- [x] D2 corrupt-quarantined `current_run` must restore from cloud, not delete the cloud copy
- [x] D3 back up the cloud copy before a push overwrites it; backups for progress/current_run/profile/prefs
- [x] Reverse resurrection: run ended on PC (cloud deleted) → delete the stale local run instead of re-uploading it
- [x] progress.save superset guard: never upload a progress file that lacks content present in the cloud (older game build dropping unknown IDs)
- [x] Push on background: iOS background task instead of blocking the main thread 30s (watchdog kill)
- [x] Token: re-read per attempt, atomic write + `.prev`, read errors contained
- [x] CM client: fail pending calls when the socket dies, random CM among least-loaded, retry TryAnotherCM, pending leak, response dispose
- [x] Serialize sync runs; push cancels the background history pull
- [x] Quit: `_exit(0)` after push (avoid static-destructor/FMOD teardown crash)
- [x] All cloud writes inside one always-completed app upload batch; stop at the first TooManyPending (root cause of the 108 storms: unfinished unbatched uploads + retrying ~210 history files per pause)
- [x] Mirror the game's run-history cloud window (newest 100 files / 5 MB per profile, older ones forgotten, never re-uploaded)
- [x] Offline scenario tests: `make test` (54 scenarios; mutation-checked)
- [x] Verify on device: boot pull 2.2–2.8 s before the game reads saves, background push completes, no TooManyPending (on device, 9/25)
- [x] Sync status visible to the user: on-screen notice when the Steam login is rejected; `sync_status.txt` + `make logs`

## P0 — reproducible build from a Steam copy
- [x] `make libs` (copy game assemblies), `make pck` (gdextension swap → ASTC → ECFG typed overrides → shader clamp), `make natives` (FMOD gdextension patch + spine 4.2 iOS), `make assemble` (Info.plist, icons, release_info, FMOD dylib, entitlements), wired into `make deploy`
- [x] Typed ECFG writer (int/bool/string, replace existing keys)
- [x] Shader clamp patch script (the game shader cannot be redistributed)
- [x] pck pipeline reproduces the shipped pck exactly: 12,328 entries, 3,485 textures byte-identical (transcode ~10 min)
- [x] `patches/fmod-gdextension/`, pinned upstream revisions
- [x] `make natives` clean build verified (FMOD + spine 4.2, arm64 iOS, from pinned sources)
- [x] Export preset team/bundle from `config.mk` (restored after export); `GAMEAPP` in repo; `.gitignore` for build outputs
- [x] Workspace-only scripts ported (assemble, provision, bridge daemon; run_bridge.sh replaced by `make bridge-run` since it hardcoded a personal IP), personal values scrubbed
- [x] Full build with the repo Makefile on this Mac (libs → app), result identical to the working app; works with macOS GNU Make 3.81
- [ ] Fresh-clone run on a second Mac (or a new user account)

## P1 — playability
- [ ] Touch: long press / two-finger tap as right click, lifted drag cursor, map panning; must feel better than the default emulation in device tests before it ships
- [ ] Fill the safe area instead of the 16:9 letterbox, verified on device
- [ ] Audio session interruptions (calls, Siri, alarms): FMOD mixer suspend/resume, verified with a real call
- [~] Game version: build from the Steam branch the player uses (public or beta) (documented); every build stage stamps the game version and `make app` refuses mixed versions (done)

## P1 — publish
- [x] THIRD_PARTY_NOTICES.md (Mono.Cecil, Google.Protobuf, Valve protobuf definitions, Godot, FMOD, Spine, SteamKit2 for desktop tools)
- [x] English disclaimer at Ekyso's level; "never share the built IPA"; no DRM/bypass wording
- [x] Token hygiene: 0600, no world-readable staging copy, revoke instructions (README)
- [x] Document the unofficial CM client and its account risk (README, in-app-sync-design)
- [x] Stale docs (SteamKit2/Mono AOT mentions); Korean README; build pipeline doc; `make logs`

## P2
- [ ] On-device Steam QR login (no Mac provisioning)
- [ ] Multiplayer: join IP editable in game, LAN discovery
- [ ] Optional Forward Mobile build for 6 GB+ devices
- [ ] On-device crash reports (MetricKit)

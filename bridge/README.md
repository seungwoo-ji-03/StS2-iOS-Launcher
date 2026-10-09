# bridge/ — Steam ↔ iPhone multiplayer bridge (PC-side)

Lets an iPhone (Steam-less, ENet/fastmp) play with a **friend on vanilla Steam**, by running
a small bridge on **your own PC** (which has the game + Steam). The friend changes nothing.

```
friend (vanilla Steam, SDR)  ⇄  [ SteamClient | ENetHost ]  your PC  ⇄  iPhone (ENet/fastmp)
```

The bridge reuses the game's own **public** transport classes (`ENetHost`, `SteamClient` —
a byte-pipe keyed by `ulong netId`) and forwards opaque game packets both ways. No packet
parsing. See [`../docs/multiplayer-bridge-design.md`](../docs/multiplayer-bridge-design.md).

## How it runs — *inside* the desktop game (not a standalone process)

The desktop build is **CoreCLR (JIT)**, so its managed assemblies load at runtime. That lets us
inject the bridge into the real game, where Steam is already initialized and every game singleton
is live — far more robust than re-hosting the netcode standalone.

- `Bridge.cs` builds as **`bridgemgr.dll`** (see `bridge.csproj`). It **must** build with
  `Godot.NET.Sdk` — the Godot source generators wire the `_Ready`/`_Process` engine callbacks;
  a plain-SDK build yields a `Node` whose `_Ready` never fires.
- `cecilpatch --inject-bridge sts2.dll` prepends `Bridge.Boot()` to `NGame.InitializePlatform`
  (and touches nothing else — Steam stays intact). `Boot()` is gated by the `--bridge` launch arg
  and installs the node on the first `ProcessFrame` (the tree isn't ready that early).

## Set-up (once, on your PC)

macOS assemblies live in `SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64/`:

```bash
DIR=".../data_sts2_macos_arm64"
GameLibs=$DIR dotnet build bridge -c Release -o out          # -> out/bridgemgr.dll (Godot.NET.Sdk)
cp out/bridgemgr.dll "$DIR/"
cp "$DIR/sts2.dll" "$DIR/sts2.dll.orig"                       # backup
cecilpatch --inject-bridge "$DIR/sts2.dll.orig" "$DIR/sts2.dll"
```

Register `bridgemgr` in `sts2.deps.json` so the game's isolated load-context resolves it — add to
the **RID** target (`.NETCoreApp,Version=vX/<rid>`), mirroring a local-dll entry like `Steamworks.NET`:

```jsonc
// targets[".NETCoreApp,Version=v9.0/osx-arm64"]
"bridgemgr/0.0.0.0": { "runtime": { "bridgemgr.dll": { "assemblyVersion": "0.0.0.0", "fileVersion": "0.0.0.0" } } }
// libraries
"bridgemgr/0.0.0.0": { "type": "reference", "serviceable": false, "sha512": "" }
```

Verify: `new AssemblyDependencyResolver(sts2.dll).ResolveAssemblyToPath(new AssemblyName("bridgemgr"))`
returns the path.

## Run

```bash
# Steam must be running; steam_appid.txt (=appid) next to the executable so SteamAPI_Init attaches.
STS2_FRIEND_STEAMID=<friend 64-bit SteamID> \
  SteamAppId=<appid> "<game executable>" --bridge
```

The bridge opens an ENet host on UDP 33771 and joins the friend's Steam lobby. Confirm with
`lsof -nP -iUDP:33771` and the `[bridge] ENet host :33771 started` log line.

The iPhone (fastmp build) then connects to this PC's IP:33771 — patch it with
`cecilpatch --mp --mp-join=<PC IP> --mp-netid=<your SteamID>` so it proposes **your SteamID** as
its netId, which is exactly what the friend-host assigns to the bridge's SteamClient. Player
references then line up end to end.

## Status
- ✅ Boots inside the live game; ENet host on 33771 comes up with Steam initialized (verified).
- ⏸ Full friend↔bridge↔iPhone relay pending a 2-peer test (friend / 2nd Steam account online).

## Reachability for remote play
Same LAN works out of the box. For true remote, put your iPhone and PC on the same Tailscale
tailnet (both are your own devices) and point `--mp-join` at the PC's Tailscale IP. Built-in
ICE/TURN is a larger effort — see the design doc.

## Requirement
All three peers (friend, PC, iPhone) must run the **same game build version** (see the
`release_info.json` version-gate note in the main docs).

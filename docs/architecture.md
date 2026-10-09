# Architecture — how iPhone ↔ vanilla-Steam multiplayer works

This project lets an **iPhone** (running a sideloaded native build, no Steam) play Slay the Spire 2
multiplayer with a **friend on unmodified Steam**, by running a small **bridge** on a PC you own.
No changes for the friend, and **no servers hosted by this project**.

## The three parties

| Party | State | Transport it speaks |
|---|---|---|
| **Friend** | Vanilla Steam, unmodified | Steam networking (SDR) |
| **Your iPhone** | Native port, Steam stubbed out | ENet (the game's `fastmp` path, raw UDP) |
| **Your PC (bridge)** | The game + your Steam + an injected module | both — it translates |

The game ships with **two** multiplayer transports behind one interface
(`MegaCrit.Sts2.Core.Multiplayer.Transport`): `Steam.*` (needs Steam) and `ENet.*` (raw UDP, no Steam).
iOS can't use Steam, so the phone uses ENet; the friend uses Steam. Those two can't meet directly, so
the bridge — which can speak both — sits in the middle and relays.

## Data path

```
 friend PC ──Steam SDR (Valve relay, crosses any NAT)──┐
                                                        ▼
                                            your PC = bridge
                                                        ▲
 your iPhone ──ENet/UDP (cellular or wifi)──▶ your router (UPnP) ─┘
```

- **friend ↔ bridge**: Steam's own relay handles remote NAT traversal — the friend can be anywhere.
- **iPhone ↔ bridge**: a direct UDP connection to your PC. Reachability is the only thing you provide
  (see [reachability.md](reachability.md)); it needs no server from us.

## The bridge

The bridge is the game itself launched with `--bridge`, plus a Cecil-injected module (`bridgemgr`,
built from [`bridge/Bridge.cs`](../bridge/Bridge.cs)). Because the desktop build is CoreCLR (JIT), the
module loads at runtime inside the live game — so Steam is already initialized and every game singleton
is available. On boot the module:

1. Opens an **ENet host on UDP 33771** — the iPhone connects here.
2. **Auto-discovers** which of your Steam friends is hosting an StS2 lobby
   (`SteamFriends.GetFriendGamePlayed` → their `m_steamIDLobby`) and joins it as a Steam client.
   No SteamID typing; `STS2_FRIEND_NAME` disambiguates if several friends host.
3. **Relays opaque game packets** both ways. The transport is a byte-pipe keyed by `ulong netId`, so the
   bridge never parses game messages — it just forwards bytes between the two transports.

### Ordering, so nothing is lost

The phone is connected **first**, then the bridge joins the friend (triggered on the phone's connect).
Packets the phone sends during the ~1–2 s join window are **buffered**, then flushed once the Steam
side connects. Without this, the phone's join request is delivered-and-acked by ENet but dropped before
the friend exists — and never resent. (This was the first live-test failure.)

### Identity (netId) — why the asymmetry is safe

Player identity travels **in the payload** as a `ulong netId`, separate from the transport connection:

- The iPhone announces itself with **your SteamID** (patched via `--mp-netid`), so to the friend-host it
  looks exactly like you — a normal Steam friend — joined.
- Incoming host packets are labeled by the client with its transport `HostNetId` (the bridge's ENet host
  id), which is *not* the friend's SteamID. That's fine: the game keys **players** off the payload netIds
  (which are consistent end-to-end), while `HostNetId` is just "the connection I'm on." Verified in
  `ENetClient.HandleMessageReceived`.

### Reliability-mode mapping

ENet and Steam use different transfer-mode values. `SteamUtil.FlagsFromMode` accepts only
`Unreliable`/`Reliable` and throws on `None`, which ENet can surface. The bridge coerces `None →
Unreliable` and wraps every send in try/catch so one odd packet can never fault the whole connection.

### Lifecycle sync

If either leg drops, the bridge tears **both** down and returns to a clean waiting state
(`TearDownRelay`). Otherwise the friend keeps streaming to a vanished phone and floods "Peer not
connected". A keep-alive daemon ([`bridge_daemon.sh`](../bridge/README.md)) relaunches the bridge if it
ever exits; an opt-in LaunchAgent ([`com.sts2.bridge.plist`](../bridge/com.sts2.bridge.plist)) starts it
at boot.

## Hard requirements

- All three (friend, bridge, iPhone) run the **same game build version**. The iPhone reports its real
  version from `release_info.json` (placed next to the executable), so it matches stock Steam.
- You and the friend are **Steam friends**; the friend **hosts**.
- The bridge machine has the game on Steam (your own copy) and this project's build applied.

## What this project does NOT do

- No hosted servers, no TURN relay, no account system. Reachability uses your own public IP (UPnP) or,
  behind CGNAT, your own free Tailscale — never our infrastructure.
- No game assets or copyrighted files are distributed; you build from your own Steam copy.

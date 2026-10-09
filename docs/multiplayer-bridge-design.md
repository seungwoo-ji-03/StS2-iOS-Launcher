# Multiplayer on iOS — design & feasibility

Goal: let the iPhone port play StS2 multiplayer with a **friend on unmodified Steam**, ideally over the internet.

## The hard constraint (why naive MP can't work)
iOS has no Steam client, so this port **stubs Steam entirely**. StS2 online MP normally uses:
- **Steam lobby** (ISteamMatchmaking) for matchmaking, and
- **Steam SDR** (Steam Datagram Relay) for the P2P transport, addressed by SteamID.

The iPhone has neither → it cannot appear in / join a Steam lobby, nor speak SDR. A device with no Steam can't enter the "Steam world." So a **vanilla-Steam friend is unreachable directly** — something must bridge the two worlds.

## The game's transport is bridge-friendly (Stage 0, source-confirmed)
The game abstracts networking to a **transport-agnostic byte pipe** (all classes PUBLIC):
- `INetHandler.OnPacketReceived(ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel)`
- `NetClient.SendMessageToHost(byte[], len, mode, channel)`, `NetHost.SendMessageToClient(ulong peerId, byte[], len, mode, channel)`
- Two interchangeable transports implement it: `Transport.Steam.{SteamClient,SteamHost}` and `Transport.ENet.{ENetClient,ENetHost}` (ENet = Godot's built-in UDP, no Steam).
- Identity = `ulong netId`. Steam: netId = SteamID (`ClientConnection.netId : SteamNetworkingIdentity`). ENet: the client **proposes** its netId and the host ACKs it (`ENetClient.SendAndWaitForNetIdAck(ulong netId, ...)`).

Because packets are **opaque byte[] keyed by netId**, a bridge can forward them without parsing.

## Architecture: PC bridge (your PC, not the friend's)
```
friend (vanilla Steam, SDR) ⇄ [ SteamClient | ENetHost ] YOUR PC bridge ⇄ iPhone (ENet/fastmp)
```
- Bridge (Godot headless on your PC) = `SteamClient` joining the friend's lobby (as YOUR Steam) + `ENetHost` the iPhone connects to. Forwards byte packets both ways. Code: [`../bridge/Bridge.cs`](../bridge/Bridge.cs) — **compiles against the game API (verified)**.
- Friend: unchanged. Your PC: runs the bridge (you own the game/Steam). iPhone: the fastmp build.

### netId consistency (the one non-trivial detail — solvable)
Game messages reference players by netId, so both ends must agree. Make the **iPhone propose `netId = your SteamID`** (your SteamID64, from `config.mk` or the Steam login); the friend-host assigns the SAME id to the bridge's SteamClient (which authenticates as you). → references match end-to-end. Requires patching the iPhone's fastmp client id (currently hardcoded `1000`) to your SteamID.

## Reachability (iPhone → your PC) — a separate layer
| option | user setup | notes |
|---|---|---|
| **LAN** | none | same wifi only |
| **Tailscale (own iPhone+PC)** | install app + login once, on YOUR two devices | auto NAT traversal; remote play; friend not involved |
| **Port-forward + DDNS** | router config | free, fiddly |
| **Built-in ICE/STUN/TURN (SIPSorcery)** | none (zero-config) | you host signaling + TURN; cellular=CGNAT⇒TURN almost always; ~4–8wk + infra. See below. |

Steam's value is exactly SDR (relay+NAT). Removing Steam means *some* reachability layer is unavoidable. Tailscale-on-your-own-two-devices is the low-friction realistic choice.

## Built-in zero-config (ICE) — feasibility summary
Technically possible with **SIPSorcery** (mature C# WebRTC/ICE/STUN/TURN, runs both ends incl. iOS Mono interpreter) + the localhost trick (iPhone game hardcodes `127.0.0.1:33771`; a local shim listens there and tunnels over ICE). But: cellular = symmetric/CGNAT ⇒ **hole-punch fails ⇒ TURN required**, so YOU end up hosting signaling + TURN (a mini-SDR). "Zero-config" for users = real infra + ~4–8 weeks for you. Defer until the bridge itself is proven.

## Immovable constraints (all approaches)
1. The friend/host must be reachable via ENet (bridge speaks Steam for them — friend stays vanilla).
2. **All peers (friend, your PC, iPhone) run the same game build version** (MP wire protocol). Open-source ⇒ needs a build pipeline tracking the current Steam version.

## Staged plan
- **Stage 0 — feasibility ✅ DONE**: transport is a bridgeable byte-pipe; netId mappable; all classes public; `Bridge.cs` compiles against the game API.
- **Stage 1 — LAN prototype (needs 2 peers)**: Godot-headless bridge harness; friend/2nd-account ⇄ bridge ⇄ iPhone on LAN; validate netId/channels. First real proof.
- **Stage 2 — remote via Tailscale** (own devices, ~no code): iPhone anywhere → your PC.
- **Stage 3 — built-in ICE/TURN** (zero-config, biggest lift): only after Stages 0–2.

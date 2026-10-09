# Reachability — connecting the iPhone to the bridge, with zero hosted infrastructure

The friend↔bridge link rides Steam's own relay (SDR), which crosses any NAT for free. The only link
**you** must make reachable is **iPhone ↔ your PC (bridge)** — and both are devices *you* own. This
project never runs a server for it. Two tiers cover essentially everyone:

## Tier 1 — Home with a public IP (zero setup)

If your home internet has a real public IP (most Korean/US fiber and many cable lines do), the bridge
opens its own port automatically:

- On start the bridge calls **UPnP-IGD** (`Godot.Upnp`) to map **UDP 33771** on your router and reads
  back the external IP.
- The iPhone connects out to `your-public-ip:33771`. Since the phone is the *client* dialing a
  now-public *server*, this works over cellular or any network — no hole-punching, no relay.

**Check:** the bridge log prints `UPnP: external IP = X … mapping result = Success`, and `X` equals
what `curl https://api.ipify.org` reports. If they match, you have a public IP and Tier 1 works.

Caveat: a dynamic public IP can change (rare, but possible). The bridge logs the current IP on every
start so you can update the iPhone's target if needed.

## Tier 2 — Behind CGNAT (your own free Tailscale)

Some connections (a lot of cellular, some apartment/campus networks) put you behind **CGNAT** — no
public IP, and symmetric NAT that defeats hole-punching. The textbook fix is a TURN relay, which costs
money to host. We don't do that. Instead, because both endpoints are **your own devices**, use
**Tailscale** (free tier):

- Install Tailscale on the iPhone and the bridge PC, sign into the same account.
- Tailscale gives the PC a **stable `100.x.y.z` address** and carries the traffic over its own relays
  when direct connection isn't possible — at no cost to you or this project.
- Point the iPhone's join target at the PC's Tailscale IP.

This also neatly handles the dynamic-IP problem (the Tailscale address never changes) and networks that
block peer-to-peer traffic.

## Why we can skip TURN entirely

A hosted TURN relay is only unavoidable when **two uncooperative peers** are both behind symmetric NAT.
Here the two peers are *yours*, so Tailscale's free relay fills that role when needed, and the friend is
reached over **Steam's** relay. There is no third-party pair that forces us to run infrastructure.

| Your situation | What to use | Who pays |
|---|---|---|
| Home, public IP | UPnP (automatic) | nobody |
| CGNAT / blocked network | your own free Tailscale | nobody (Tailscale free tier) |
| Friend, wherever they are | Steam SDR (built in) | nobody |

## Setting the iPhone's target

The join target is applied when the iPhone build is patched (`--mp-join=<ip-or-tailscale-ip>`). Changing
it currently means re-patching + re-signing the app (see [build-and-sideload.md](build-and-sideload.md)).
Prefer a stable address (Tailscale IP, or a static/rarely-changing home IP) so you set it once.

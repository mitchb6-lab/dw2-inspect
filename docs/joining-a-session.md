# Joining a Distant Worlds 2 multiplayer session

How a second player connects to a session hosted on **this PC**.

> ### Status: 2026-09-08 — works on a LAN, with caveats
>
> | | |
> |---|---|
> | Two instances on one PC | **Working** (verified) |
> | Two PCs on a LAN | **Should work — first test 2026-09-10.** Setup for the other machine: [second-machine.md](second-machine.md) |
> | Over the internet | **Works via a virtual LAN** (Tailscale/ZeroTier); see [Playing over the internet](#playing-over-the-internet) |
> | Joiner's empire choice reaching the host | ~~Not yet~~ **Working since 2026-09-08** |
>
> ~~The networking is proven: state syncs host→client and commands relay client→host between
> two live games. What is *not* finished is the lobby handing the joiner's empire to the
> host, so right now both players end up in the host's galaxy as configured by the host.~~
> *(Struck 2026-09-10: the empire handoff was finished on 2026-09-08 and this header still
> denied it.)* Both players pick an empire in the lobby and the host generates the galaxy to
> match. **If the other machine is building from GitHub rather than receiving files, read
> [second-machine.md](second-machine.md) instead of the next section.**

---

## What the other person needs

| Item | Where from | Notes |
|---|---|---|
| **Distant Worlds 2** | Steam | Must be the **same build**. This PC runs **1.3.6.3**. |
| **`Dw2MpLobby.exe`** | You send it | The launcher — it finds their game and starts it |
| **`Dw2Mp.dll`** | You send it | The mod. Put it **beside** the lobby exe |
| **A save file** | You send it | Temporary — see [Limitations](#limitations) |

They do **not** need .NET installed: DW2 ships its own .NET 8 runtime and the mod loads
into it.

### Where to get the files on this PC

```
Lobby : C:\Users\mitch\OneDrive\Documents\dw2-inspect\src\Dw2MpLobby\bin\Debug\net9.0-windows\
Mod   : C:\Users\mitch\OneDrive\Documents\dw2-inspect\src\Dw2Mp\bin\Debug\Dw2Mp.dll
Saves : C:\Program Files (x86)\Steam\steamapps\common\Distant Worlds 2\data\SavedGames\
```

Send them the whole `net9.0-windows` folder with `Dw2Mp.dll` copied into it. The lobby
looks for the mod beside itself first.

---

## Your network details

| | |
|---|---|
| **This PC's LAN address** | `192.168.1.166` |
| **Port** | `47800` (TCP) |

**You do not need to look this up.** The lobby lists every address a joiner could use
under **Your address**, labelled by what it actually is, with a **Copy** button. It also
detects Tailscale, ZeroTier, Hamachi and Radmin adapters and marks those as working over
the internet.

It warns about full-tunnel VPNs (NordVPN and similar) because those break peer
connections, and it ignores adapters that are down or failed DHCP — so the list only
contains addresses that could actually work.

If you want to check by hand anyway:

```powershell
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' }
```

~~**Firewall is already handled.** Windows has inbound Allow rules for
`DistantWorlds2.exe` (TCP, any local port, all profiles). The listener runs *inside* that
process, so port 47800 is already permitted — nothing to configure.~~

**WRONG since the launcher took over the transport — found 2026-09-10, before the first
two-PC test.** The listener on 47800 now runs in **`Dw2MpLobby.exe`**, which has no firewall
rule, and this PC's active network profile is Public. Loopback never showed it because
loopback is not filtered. The rule to add (elevated PowerShell, by a person) is in
[second-machine.md → The host](second-machine.md#the-host-this-pc).

---

## Step by step — you (host)

1. **Pick the save.** Both players load the same one for now. `MPTestSmall.DWGame` (21 MB)
   is the light one; a late-game save works but costs ~15 GB of RAM per instance instead
   of ~3.5 GB.
2. **Send the joiner** the lobby folder, `Dw2Mp.dll`, and that save file. They drop the
   save into their own `data\SavedGames\`.
3. **Run `Dw2MpLobby.exe`.**
4. Set **Role → Host a session**.
5. Configure your empire — name, race, government, colour.
6. Choose **Mode**: *Co-op — shared empire* (both drive one empire) or *Competitive*.
7. Leave **Port** at `47800`.
8. **Pick your address** from **Your address** and click **Copy**. Send it to the joiner.
   Choose the LAN entry if you are on the same network, or the Tailscale/ZeroTier/Hamachi
   entry for internet play.
9. Click **Host and launch**. DW2 starts; wait until you are actually in the galaxy.
10. **Tell the joiner to connect only once you are in-game.** The host does not listen until
    it has loaded.

## Step by step — them (joining)

1. Copy `Dw2Mp.dll` next to `Dw2MpLobby.exe`, and the save into their
   `...\Distant Worlds 2\data\SavedGames\`.
2. **Run `Dw2MpLobby.exe`.** It should report finding their game and loading 23 races.
3. Set **Role → Join a session**.
4. Paste the **Host address** you sent them, and leave **Port** at `47800`.
5. Click **Test connection**. It answers in a couple of seconds and tells them whether the
   host is listening — far better than discovering a typo after a four-minute game load.
   *(Expect "no answer" until the host is actually in-game.)*
6. Configure their empire (see [Limitations](#limitations) — this does not reach you yet).
7. Click **Join and launch**.
8. Their game loads, connects, and begins receiving the galaxy.

## Confirming it worked

Both machines write a log to `%LOCALAPPDATA%\Dw2Mp\`. Open the newest `determinism-*.log`.

**Host should show**

```
# net: role=Host launcher=127.0.0.1:47810 — commands carry the steady state, ...
# net[Host]: connected to launcher on 127.0.0.1:47810
# net: handshake OK — protocol and game version match
# net[host]: client requested a resync
# net[host]: full state #1 tick=442 (client asked) raw=23,881,224B packed=4,330,091B ...
# net[host]: delta #1 tick=30 28 record(s) changed (44 ships total) (600B; ...)
```

**Joiner should show**

```
# net[client]: connected to launcher on 127.0.0.1:47811
# net: handshake OK — protocol and game version match
# net[client]: requested full state #1 — no galaxy yet
# net[client]: APPLIED sync #1  23,910,011B  read=280ms  apply=42ms  ships=45 empires=9
# net[client]: local simulation STOPPED — the host's deltas move this world
# net[client]: delta #51 applied — 2 ship(s), 4 project(s), 62B
```

Three lines matter, in this order:

- **`handshake OK`** — the two builds agree on protocol and game version. A mismatch here is
  the failure you want, because it stops before corrupting anything.
- **`APPLIED sync #1`** — a galaxy simulated on the host is now running on the joiner. This is
  the join.
- **`delta … applied`** — the steady state. If you see the first two and never this, the
  joiner is connected and frozen.

The joiner's `[totals: …]` on every 50th delta is the cumulative count by kind. Prefer it to
the per-delta line for "is this working", because a sampled log can miss a whole category:
research runs every 5th delta and the log samples every 50th, so **the sampler never lands on
a research delta** and that section looks dead while working perfectly. Totals have no phase.

---

## Playing over the internet

The steps above assume both players are on the same LAN. Over the internet there are two
options, and the easy one needs no changes to anything.

### Recommended today: a virtual LAN

Tools like **Tailscale**, **ZeroTier** or **Hamachi** put both machines on one virtual
subnet. The joiner then types the host's *virtual* address into the lobby instead of a
LAN address, and everything else is identical — the transport is plain TCP and neither
end knows the difference.

This removes **port forwarding** entirely, and works even behind carrier-grade NAT where
forwarding is not possible at all.

**Prefer Tailscale or ZeroTier over Hamachi.** Hamachi works, but its free tier is capped
at a handful of members and it is the oldest and least reliable of the three. Tailscale is
WireGuard-based, free for personal use, and setup is roughly "install, sign in" on both
ends.

Setup:

1. Both players install the same tool and join the same network.
2. **The host opens the lobby and picks the virtual adapter from "Your address."** It is
   detected and labelled automatically — no need to work out which address is which —
   and sorted above the LAN entry, because a virtual address works in strictly more
   situations.
3. **Copy** it and send it over.
4. The joiner pastes it, clicks **Test connection** to confirm, then launches.

Detection is by adapter name first and IP range second. Ranges alone are ambiguous —
Tailscale uses `100.64/10`, which is also the carrier-grade NAT range, and ZeroTier's
range is configurable. Naming also keeps Tailscale from being mistaken for a
"turn this off" VPN, since it is WireGuard-based like the ones that genuinely do break
peer connections.

> **Do not run NordVPN at the same time.** It is installed on this PC and, like any
> full-tunnel VPN, it will fight a virtual-LAN adapter and break the connection. Turn it
> off for a session.

### ~~The real constraint is bandwidth, not NAT~~ — OVERTAKEN 2026-09-09

> **The section below was true when written and is now wrong.** It is kept, struck, because
> it explains why delta sync was built — and because it recommends `DW2MP_SYNC_EVERY_TICKS`,
> an environment variable that **no longer exists**. Setting it does nothing.
>
> ~~A virtual LAN solves reachability. What it does not solve is that we send a full
> compressed galaxy on every sync — 4.2 MB for a small save, 9.2 MB late-game, every 1,200
> ticks, bounded by the host's upload speed. Use a small save; sync less often with
> `DW2MP_SYNC_EVERY_TICKS`; expect the joiner to lag by roughly one transfer time. Delta sync
> is the priority for internet play.~~

**Deltas exist now, and bandwidth is no longer the binding constraint.** What crosses the
wire in normal play:

| Message | Size | When |
|---|---:|---|
| Ship/colony/research/fleet delta | **~400–900 B** | every 30 ticks (~3 s) |
| Structural summary | **25 B** | every 300 ticks |
| Player command | ~39 B | as you act |
| Full galaxy state | 4.3 MB | on join, and on a resync the client asks for |

Measured over a 480-second two-instance run in a 50-star galaxy: **five full states and
~2,650 deltas, the deltas totalling under 1 MB between them.** A full state is now a join
and repair mechanism rather than the transport, so the host's upload speed stops being the
thing that decides whether internet play is usable.

What replaces the old advice:

- **Save size no longer matters** for ongoing play — only for the one state sent on join.
- **There is no sync interval to tune.** Full states are sent when the client asks, and it
  asks when its structure has genuinely diverged or on a 60-second safety floor.
- The joiner is a few hundred milliseconds behind on *movement* and a second or two behind
  on newly-built ships, rather than one 4 MB transfer behind on everything.

**Still unmeasured over a real network.** These numbers are loopback. Latency and jitter are
exactly what loopback cannot show, and a delta stream is more sensitive to both than a
periodic bulk transfer was.
### The better long-term answer

`SteamNetworkingSockets` gives NAT traversal and lobby discovery with **no third-party
install** — the joiner needs nothing beyond the game they already own. `Facepunch.Steamworks`
is already loaded inside DW2's process, so it is reachable. A virtual LAN is the right
answer *today*; Steam sockets is the right destination.

---

## Troubleshooting

**"Could not find Distant Worlds 2"** — the lobby checked `DW2_PATH` and every Steam
library. Either pass the install folder as the first argument, or set `DW2_PATH`.

**`*** GAME VERSION MISMATCH ***` in the log** — different DW2 builds. State transfer is
the game's own serialised galaxy, so mismatched builds exchange bytes that deserialise
into nonsense. Both must be on the same version; this PC is on 1.3.6.3.

**Joiner connects then nothing happens** — the host had not finished loading. It does not
listen until it is in-game. Reconnect after the host is in the galaxy.

**Connection refused** — check the host is actually in-game, the address is current, and
both machines are on the same network. If a **VPN is connected on either end it will
break LAN discovery** — NordVPN is installed on this PC ~~(currently disconnected)~~ *(it
was connected on 2026-09-10 — check, do not assume)*; turn it off for a session.

**Connection refused from another PC, but Test works on the host itself** — the firewall.
See the struck paragraph under [Your network details](#your-network-details).

**Client never finishes loading (both games on one PC)** — they starve each other on disk.
Start the host, wait until it is in the galaxy, *then* start the client. This was a real
failure: 450 seconds with zero simulation ticks.

**Out of memory with both games on one PC** — a late-game 40 MB save costs ~15 GB per
instance. Use a small save (~3.5 GB each). This PC has 31.4 GB.

**Host stutters every sync** — expected. Serialising and compressing the galaxy runs on the
simulation thread: ~150–200 ms on a small save, up to ~700 ms on a late-game one.

---

## Limitations

Things that will surprise you if you do not know them:

- ~~**The joiner's empire choice does not reach the host.**~~ ~~**Everyone needs the same
  save.**~~ **Both fixed 2026-09-08** — struck rather than deleted because a reader who
  remembers them should be able to see when they stopped being true. Both players pick their
  empire in the lobby and the host builds the galaxy to match; the host sends its state on
  join, so **no save file is shared and nothing has to match beforehand** beyond the game
  version, which the handshake checks.
- **Competitive mode leaks information.** Every client receives the whole galaxy on join and
  on each resync. The UI filters it correctly per empire, so it *plays* right, but the hidden
  data is present in memory and a determined player could read it. Fine among people who will
  not; not cheat-proof.
- **The host has authority.** If the host quits, the session ends. No reconnection.
- **The client does not simulate.** Its world moves only when the host says so, through
  deltas. That is what a host-authoritative client should be, and it means a client whose
  connection stalls sees a frozen galaxy rather than a diverging one — which is the better
  failure, but it is a visible one.
- **The client is briefly behind on what EXISTS.** Deltas carry ships, colonies, fleets and
  characters as they are created and destroyed, so this is a second or two, not a session.
  Research progress and colony internals refresh on a slower schedule.
- **Never tested across two machines.** Everything measured so far is two game processes on
  one PC. The transport is the same either way, but latency, MTU and a real network stack
  are not exercised by loopback.

---

## Changelog

- **2026-09-08** — First version. Loopback verified; LAN untested. Lobby, handshake, state
  sync and command relay all in place.
- **2026-09-08** — Added "Playing over the internet": virtual LAN (Tailscale/ZeroTier/
  Hamachi) as the no-code path, with measured per-sync bandwidth showing that payload size,
  not NAT, is the binding constraint over the internet.
- **2026-09-08** — Lobby now discovers and labels every connectable address (LAN,
  Tailscale, ZeroTier, Hamachi, Radmin), warns about full-tunnel VPNs, offers Copy for the
  host and Test connection for the joiner. No more hunting for the right IP.

### 2026-09-08 — empire selection works end to end

- Both players now choose their empire (name, race, government, colour) in the client, and
  the host generates a galaxy containing both. Previously the chosen empires were ignored
  and the joining player was silently given DW2's neutral `Independent` pseudo-empire.
- If you ran an earlier build, **check `data\Logs` in your game folder**. The old bug made
  DW2 write a crash dump per tick; a few thousand files there will make the game hang or
  fail to start. Deleting `DW2_CrashDump*` and `SENT_DW2_CrashDump*` is safe.
- Also delete `data\SessionActive` if the game was force-killed — a stale one can block
  startup.

### 2026-09-09 — the protocol changed underneath this guide

Three sections above were describing a design that no longer exists. Corrected, and struck
rather than deleted so a reader who remembers the old behaviour can see when it changed:

- **Bandwidth is no longer the binding constraint.** Full galaxy state used to go out every
  1,200 ticks at 4.2–9.2 MB; it is now sent on join and on request only, with ~400–900 B
  deltas carrying the world in between. The old section recommended `DW2MP_SYNC_EVERY_TICKS`,
  which **no longer exists** — setting it does nothing.
- **The log lines to look for have changed.** `# net[host]: listening on 47800` and
  `sync #N tick=…` are gone; the mod now talks to the launcher on loopback and the host logs
  `full state #N (client asked)` plus `delta #N`. A guide that lists lines the software no
  longer prints reads as "it is broken".
- **Two limitations were fixed and the page still listed them** — the joiner's empire choice
  reaching the host, and both players needing the same save. Both have been true since
  2026-09-08, and the Changelog directly below already said so while Limitations directly
  above still denied it.

Also added: the client no longer simulates locally, so a stalled connection now shows as a
frozen galaxy rather than a diverging one — worth knowing before you diagnose it as a hang.

### 2026-09-10 — preparing the first two-PC test

- New page: **[second-machine.md](second-machine.md)** — clone, build, run and connect from a
  machine that gets the code from GitHub rather than being handed files, written so a Claude
  session on that machine can follow it. The section above ("What the other person needs")
  still describes the hand-them-the-folder path, which also works.
- **The firewall claim above was wrong** and would have failed the test at the first
  connection: the listener moved from the game process into `Dw2MpLobby.exe` when the
  launcher took over the transport, and only the game has a rule. Struck and corrected in
  place; the rule to add is on the new page.
- Two more things the new page records that this one assumed: the host's internet is
  Starlink (CGNAT — no port forwarding, so internet play needs Tailscale on both ends), and
  NordVPN was connected on the day of writing.

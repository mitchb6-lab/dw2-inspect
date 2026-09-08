# The lobby — design

**Goal:** players configure their own empire before a session starts, rather than everyone
loading one pre-made save.

## Why a separate application

DW2's interface is its own framework (`DWControl`, `ScaledRenderer`, `DWRendererBase`) inside
an obfuscated binary. Building a lobby screen *inside* the game would mean reverse-engineering
that framework — expensive, fragile, and it fights the protector.

An external app avoids all of it, and solves distribution at the same time: **the launcher is
the thing you hand someone.** They run it, it finds their DW2, and it starts the game correctly
configured. No command lines, no editing files.

```
[Lobby.exe]  <--- lobby protocol (JSON/TCP) --->  [Lobby.exe]
     |                                                 |
   writes session.json                          writes session.json
   launches DW2 --low-level-inject              launches DW2 --low-level-inject
     |                                                 |
[DW2 + Dw2Mp] <--- game protocol (MessagePacket) --> [DW2 + Dw2Mp]
```

The split is deliberate: **the lobby owns pre-game negotiation, the mod owns in-game
networking.** Neither needs to understand the other's job, and the lobby can be redesigned
without touching anything that was hard to prove.

## What the lobby can offer, without running the game

All of it comes from readable XML in `data/`:

| Source | Gives |
|---|---|
| `Races.xml` | **23 races** — id, name, default `MainColor`/`SecondaryColor`, flag files |
| `Governments.xml` | **11 governments** — id and name |
| — | galaxy size, star count, AI empire count, seed |

So the empire editor is: name, race, government, colours, flag — mapping one-to-one onto
`GameStartSettingsEmpire`, whose fields we already set programmatically in `MakeSave`.

## session.json — the contract

Written by the lobby, read by the mod at startup. **`mySlot` differs per machine**; everything
else is identical on both.

```json
{
  "protocolVersion": 1,
  "mode": "coop-shared",
  "hostAddress": "127.0.0.1",
  "port": 47800,
  "galaxy": { "stars": 30, "aiEmpires": 4, "seed": 12345 },
  "players": [
    { "slot": 0, "name": "Mitch",  "empire": { "name": "Terran Union", "raceId": 0, "governmentId": 0, "r": 40, "g": 90, "b": 200 } },
    { "slot": 1, "name": "Friend", "empire": { "name": "Ackdarian Compact", "raceId": 1, "governmentId": 2, "r": 200, "g": 60, "b": 40 } }
  ],
  "mySlot": 0
}
```

**Mode decides how slots map to empires:**

- `coop-shared` — every player drives the **same** empire. Only slot 0's empire config is used;
  no assignment needed, no information concern. The quickest playable thing.
- `competitive` — one empire per slot. The mod promotes *this machine's* empire via the
  `IsPlayer` hook proven in `MakeSave.EnsurePlayerEmpire`.

## How the mod uses it

The host already has everything it needs — `MakeSave` proved a `GameStartSettings` can be
built in-process and handed to `StartGameNew`, with one `GameStartSettingsEmpire` per player.
So:

1. **Host** builds `GameStartSettings` from `galaxy` + `players`, generates the galaxy, starts
   syncing.
2. **Client** connects, receives the full galaxy, adopts it (M3/M4 path), and promotes its own
   empire if the mode is competitive.

The client therefore **needs no save file** — the galaxy arrives over the wire. That removes
file exchange from "give this to a friend" entirely.

## Known unknowns

- **Can a client adopt a galaxy from the main menu**, without loading a save first?
  `StartGameExisting` may need an existing game context. Untested, and it decides whether the
  client needs a throwaway local game first.
- Colour and flag round-tripping into `GameStartSettingsEmpire` is unverified.
- Steam sockets replace TCP for internet play later; the lobby's address field becomes a
  Steam lobby id.

---

## Status — built 2026-09-08

`src/Dw2MpLobby` — WinForms, `net9.0-windows`, **no NuGet packages**. One file you hand
someone; it finds their DW2 and launches it configured.

**Verified working:**

- Finds DW2 via `DW2_PATH` or by reading every Steam library from `libraryfolders.vdf`
- Loads **23 races with their real in-game colours** (Human navy, Ackdarian cyan, Mortalen
  dark red) and **11 governments**, straight from `Races.xml` / `Governments.xml`
- Empire editor: player name, empire name, race, government, colour picker
- Selecting a race adopts that race's own colour, so a player who changes nothing still
  gets a sensible empire
- Host/join roles, mode selector, galaxy settings, address and port
- Writes `session.json` and launches DW2 with `--low-level-inject` and the right environment

Only top-level `<Race>` elements are counted — `RaceId` also appears inside per-race
relation tables, and a naive scan finds 418 of them instead of 23.

**Not yet built:**

- **Slot exchange.** The joining player's empire is currently known only to their own
  machine; the lobby does not yet send it to the host. Until it does, the host generates
  using its own configuration. This is the next piece and it is what makes competitive
  mode meaningful.
- The mod does not yet read `session.json` — it still takes its settings from environment
  variables. Wiring `DW2MP_SESSION` through to `MakeSave`-style generation is the other
  half.
- Flag selection (races carry `AlternateFlagFilenames`, currently unused).

---

## Phase 1 — transport moved into the launcher (2026-09-08)

Modelled on FAF. Supreme Commander is launched with `/gpgnet 127.0.0.1:59800` and never
touches the internet itself; the FAF client and its ICE adapter own the network, and the
game just reports state upward over localhost.

Ours now works the same way:

```
[DW2 + Dw2Mp] --localhost--> [Dw2MpLobby] --TCP/Steam--> [Dw2MpLobby] <--localhost-- [DW2 + Dw2Mp]
```

**The mod no longer opens a listening socket or dials a remote address.** It connects to
`127.0.0.1:47810` and nothing else. `NetSession.LauncherLoop` replaced the separate
`HostLoop`/`ClientLoop` — both roles connect identically now, and the role only decides
what they *send*.

Why it is worth the churn:

- NAT traversal, Steam sockets, reconnection and relays can all change **without touching
  game code**, and without a four-minute game reload to test each attempt.
- The mod's networking is one localhost connection that cannot fail for interesting reasons.
- The launcher sees every byte, so it can show connection state and throughput.

`Relay` forwards **whole frames**, reading only the 5-byte header to learn the payload
length. A byte-for-byte copy would happily split or merge frames and corrupt a galaxy in a
way that would be very hard to trace back to the relay. It is otherwise ignorant of
content — a protocol change in the mod needs no change here.

The mod retries its connection for two minutes: the game takes minutes to load and the
launcher may restart while it does. A single failed connect used to mean no networking for
the whole session, with nothing in the log to say why.

## Phase 2 — Steam networking: seam built, implementation deferred

`IRemoteTransport` is the seam; `TcpTransport` implements it. `SteamTransport` exists,
is selectable, and says clearly that it is not available.

**Why it is not implemented rather than half-implemented:** it needs two Steam accounts on
two machines to verify, and there is an unresolved question about which process should own
the Steam connection — DW2's process already has Steam initialised with the correct app id,
whereas the launcher would have to initialise it separately with someone else's app id.

Shipping networking that has never carried a byte is how a feature looks finished and fails
in someone else's hands. The design is sound: both players own DW2 on Steam, so
`SteamNetworkingSockets` gives NAT traversal, Valve-hosted relay fallback and peer discovery
by Steam ID — replacing the single largest piece of infrastructure FAF had to build and host
(their ICE adapter plus TURN servers). FAF cannot do this because SupCom players are not
guaranteed to share a platform. We are.

## Client remodel

Four tabs, after the FAF client's shape:

| Tab | State |
|---|---|
| **Private** | Working — empire config, host/join, transport, galaxy settings |
| **Lobby** | Phase 3 placeholder — a disabled game list and an explanation |
| **Mods** | Lists installed mods and which are enabled |
| **Log** | Connection status and relay throughput |

A status bar shows game/peer connection state, frame count and MB each way.

### Why a Mods tab now, when we only support vanilla

**Mod mismatch breaks state sync exactly like a version mismatch.** State transfer ships
DW2's own serialised galaxy, and mods change the data it is built from — components, races,
governments. Two players with different mods enabled would exchange state that deserialises
into something subtly or catastrophically wrong.

`ModDetection` reads `mods\<Name>\mod.json` and treats `mods\mods.json`'s `order` array as
the enabled set — which it is, per `DwModSupport`: `EnableModInternal` appends to it,
`DisableModInternal` splices out of it. An empty `order` means everything installed is
**off**, which is easy to misread from the in-game UI. `EnabledFingerprint` sorts the set so
two players who enabled the same mods in a different order still match.

Vanilla is the supported configuration. This exists so a mismatch is *detected* rather than
discovered as strange behaviour an hour into a session.

## NOT YET VERIFIED

The Phase 1 refactor **has not been run end to end**. Both projects build and the client
launches, but no session has yet gone game → launcher → launcher → game. That test needs two
game instances and about twelve minutes. Until it passes, treat Phase 1 as written but
unproven — the previously verified path (mod-owned TCP) no longer exists to fall back on.

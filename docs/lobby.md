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

## Phase 1 VERIFIED end to end — 2026-09-08

Two live games, each talking only to its own launcher-side relay on localhost, with the
relays linked over TCP.

```
relay : host(game=True peer=True frames=25 up=46,287,270B)
        client(game=True peer=True frames=25 down=46,287,270B)

client: connected to launcher on 127.0.0.1:47811
        peer protocol=1 game=1.3.6.3 mode=coop-shared
        handshake OK
        APPLIED sync #1  23,138,191B  read=201ms  apply=45ms  ships=90 empires=6
        ... 10 states applied
```

**Byte counts are identical on both sides of the relay** — 46,287,270 in and out, so not a
byte was lost or duplicated across frame boundaries. Ten full galaxies were applied, and
the handshake travelled the relay in BOTH directions: each end read the other Hello and
confirmed protocol and game version.

Host cost is unchanged by the refactor (serialise ~57-102 ms, pack ~101-124 ms,
send 1-2 ms) and the client still applies in ~10-45 ms after a ~94-201 ms parse. The relay
adds no measurable overhead on loopback.

One limitation the test exposed and FIXED: the launcher hardcoded local port 47810, so two
launchers on one machine would collide. It now asks the OS for a free loopback port per
instance and passes it to the game as `DW2MP_LOCAL_PORT`.

---

## Slot exchange — VERIFIED 2026-09-08

The gap that made empire customisation decoration is closed. Both players now agree a
session **before either game starts**, which is the only order that can work: a galaxy
generated before the joiner's choices arrive cannot contain their empire.

```
[HOST  ] Lobby open on port 47900. Waiting for a player to join...
[CLIENT] Connected to 127.0.0.1:47900. Sending your empire...
[HOST  ] Friend joined as "Ackdarian Compact" (slot 1).
[CLIENT] Session received: 2 player(s), mode coop-shared, you are slot 1.

HOST's view                                   CLIENT's view
  slot 0: Mitch  — "Terran Union"      race=0   slot 0: Mitch  — "Terran Union"      race=0
  slot 1: Friend — "Ackdarian Compact" race=1   slot 1: Friend — "Ackdarian Compact" race=1
  mySlot=0                                      mySlot=1

RESULT: PASS — empires exchanged, slots assigned, start delivered
```

Both sides hold an identical descriptor differing **only in `mySlot`**, which is what lets
each machine know which empire is its own without either side guessing.

### Design notes

- **The host owns slot numbering.** A client asking for a slot must not be able to
  overwrite the host's own empire, so the host assigns `Players.Count` and echoes the
  agreed session back.
- **The lobby connection is reused as the relay's transport** (`ExistingStreamTransport`).
  Reconnecting after the handshake would mean a second listen/dial cycle with a race
  between the two launchers, for no benefit — the peers are connected and already agree.
- **The lobby stops reading when it hands over.** Two readers on one socket would each
  consume half the other's frames.
- Framing matches the game protocol exactly, so the codebase has one framing convention
  rather than two subtly different ones.

### Flow in the UI

1. Both players configure an empire.
2. Host clicks **Open lobby**; joiner enters the address and clicks **Connect to host**.
3. Both see the player list fill in with names, empires, races and governments.
4. Host clicks **Start session** — enabled only once someone has actually joined.
5. Both launch, and the already-open connection becomes the relay.

### Still to do

The mod does not yet read `session.json`, so the agreed empires do not yet shape the
generated galaxy. `MakeSave` already proves that path works — it builds `GameStartSettings`
with per-empire configuration and generates a galaxy — so this is wiring, not discovery.

---

## Session-driven generation — 2026-09-08

The mod now reads `session.json` and the host builds the galaxy from the agreed players,
so both players' empire choices shape the game. `--continue` is gone from the launch
command: **nobody needs to share a save file any more.**

```
# session: mode=competitive players=2 mySlot=0 stars=15 ai=2 seed=4242
#   slot 0: Mitch — Terran Union (race 0, gov 0)
#   slot 1: Friend — Ackdarian Compact (race 1, gov 2)
# makesave: empire for slot 0 — Terran Union (race 0, gov 0)
# makesave: empire for slot 1 — Ackdarian Compact (race 1, gov 2)
# makesave: competitive — 2 human empire(s)
# makesave: generating — stars=15, 2 AI empire(s), seed=4242
# makesave: playing empire[0] '' (slot 0, mode competitive)
```

The game's own log for that galaxy, with **no exceptions in the entire run**:

```
Dimensions: 6 x 6 sectors, Star Systems: 17, Total bodies: 821,
Standard Empires: 3, Pirate Empires: 3, Colonies: 3, Ships and Bases: 50
```

### How the two modes differ — one line, not two code paths

`SessionConfig.PlayableEmpireIndex` returns `MySlot` in competitive and `0` in co-op.
Co-op therefore adds only slot 0's empire to the settings (adding both would create a
second empire nobody plays) and every machine promotes empire 0, so both players drive the
same one. `MakeSave.EnsurePlayerEmpire` promotes that index, and since
`StartGameExisting` binds the whole UI to whichever empire carries `IsPlayer`, that single
choice decides what this player commands.

### The client no longer needs a save

Both roles generate. The client's galaxy is a **throwaway** whose only job is to give
`StartGameExisting` a game context to adopt the host's state into — so it does not matter
whether generation is deterministic between the two machines, which is fortunate, because
nothing suggests it is.

### NOT verified — empire identity may not survive generation

Two signals say the chosen names and races may not reach the generated empires:

1. The promoted empire logged an **empty name** — `playing empire[0] ''` — rather than
   "Terran Union".
2. The galaxy contains **3 standard empires**, where 2 human + 2 AI implies 4.

So what is proven is that the session is read, both empires are submitted to
`GameStartSettings`, and a galaxy is generated cleanly from the session's star count, AI
count and seed. What is **not** proven is that the generated empires carry the chosen
names, races, governments or colours.

This is the same class of gap found earlier: `IsPlayer` set on a `GameStartSettingsEmpire`
does not survive `Galaxy.Generate`, so other fields may not either. The next step is to
read the generated `Galaxy.Empires` back and compare against the session — a small check
that settles it either way.

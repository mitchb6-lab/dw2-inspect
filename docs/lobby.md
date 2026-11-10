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
  "protocolVersion": 2,
  "mode": "coop-shared",
  "hostAddress": "127.0.0.1",
  "port": 47800,
  "galaxy": { "stars": 30, "aiEmpires": 4, "seed": 12345 },
  "players": [
    {
      "slot": 0, "name": "Mitch",
      "empire": { "name": "Terran Union", "raceId": 0, "governmentId": 0, "r": 40, "g": 90, "b": 200 },
      "lobbyProtocol": 2,
      "gameVersion": "1.3.6.3",
      "contentPacks": [ "Factions - Ikkuro and Dhayut", "Return of the Shakturi" ],
      "contentPackDetails": [
        { "name": "Factions - Ikkuro and Dhayut", "installed": true,  "steamAppId": 2140010, "version": "2085203991787705401", "note": "bundles on disk" },
        { "name": "Return of the Shakturi",       "installed": true,  "steamAppId": 2926560, "version": "1480154476699251668", "note": "bundles on disk" },
        { "name": "Shadows Rising",               "installed": false, "steamAppId": 3989670, "version": null,                  "note": "bundles not on disk" }
      ]
    },
    { "slot": 1, "name": "Friend", "empire": { "name": "Ackdarian Compact", "raceId": 1, "governmentId": 2, "r": 200, "g": 60, "b": 40 }, "lobbyProtocol": 2, "gameVersion": "1.3.6.3", "contentPacks": [ "..." ], "contentPackDetails": [ "..." ] }
  ],
  "mySlot": 0
}
```

`protocolVersion` is the **lobby protocol** (`LobbyProtocol.Version` in the lobby,
`SessionConfig.ExpectedProtocolVersion` in the mod -- bump both together). It covers this
file and the lobby's own messages, because the Join and Session payloads *are* these types.
It is separate from the mod's in-game `NetSession.ProtocolVersion`. Protocol 2 (2026-11-09)
added `lobbyProtocol`, `gameVersion` and `contentPackDetails` per player; see
*Content packs are checked before anyone launches* below.

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

- ~~**Slot exchange.** The joining player's empire is currently known only to their own
  machine; the lobby does not yet send it to the host. Until it does, the host generates
  using its own configuration. This is the next piece and it is what makes competitive
  mode meaningful.~~
- ~~The mod does not yet read `session.json` — it still takes its settings from environment
  variables. Wiring `DW2MP_SESSION` through to `MakeSave`-style generation is the other
  half.~~
  *(Struck 2026-11-08: built 2026-09-08, see "Session-driven generation" below; the lobby sets `DW2MP_SESSION` and the mod loads it in `Determinism.cs`.)*
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

~~The mod does not yet read `session.json`, so the agreed empires do not yet shape the
generated galaxy. `MakeSave` already proves that path works — it builds `GameStartSettings`
with per-empire configuration and generates a galaxy — so this is wiring, not discovery.~~
*(Struck 2026-11-08: built 2026-09-08, see "Session-driven generation" below; the lobby sets `DW2MP_SESSION` and the mod loads it in `Determinism.cs`.)*

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

### Empire identity check — RESULT 2026-09-08: configured empires are NOT used

Read the generated `Galaxy.Empires` back and compared against the session:

```
[0] name='Independent'                          race=255 gov=-1
[1] name='Najarca Empire'                       race=1   gov=6
[2] name='Atuuk Commonwealth'                   race=9   gov=4
[3] name='Ikkuro Serenity'                      race=5   gov=11
[4] name='Ancient Mortalen Planet Destroyer AI' race=248 gov=32765
[5] name='The Hive'                             race=249 gov=32766
[6..8] Hidden Council / Red Dagger Clan / Harmonious Void  gov=32767

session asked for:  'Terran Union' race=0 gov=0  |  'Ackdarian Compact' race=1 gov=2
```

**Two defects, both real:**

1. **`GameStartSettings.Empires` is ignored.** No generated empire carries `gov=0` or
   `gov=2`. The three standard empires were auto-generated from
   `OtherEmpiresAutoGenerateAmount`; our configured entries were not used. Consistent with
   `IsPlayer` not surviving `Galaxy.Generate` — the settings' empire list is not the input
   we assumed. `GameStartSettings.EmpireAutoGenerationState` is the obvious suspect and has
   never been set.

2. **Promotion picks the wrong empire.** Index 0 is `Independent` — DW2's neutral
   pseudo-empire (`race=255`, `gov=-1`) — and indices 4–8 are pirates and monsters
   (`gov` 32765–32767). "Promote index N" must skip non-playable empires or the player ends
   up commanding the independents.

#### A measurement bug produced a confident wrong answer first

The initial run reported every empire with an empty name and blank race, and concluded
names did not survive. **That was the probe, not the game.** `Empire.Name` is a PROPERTY
(`get_Name`/`set_Name`), and the race field is `DominantRaceId`, not `RaceId` —
`AccessTools.Field` returned null for both and `?.` printed empty. Only `GovernmentId`
read correctly, because it is genuinely a field.

The conclusion happened to survive re-measurement, but it was reached on invalid evidence,
and the corrected read is what revealed the `Independent` promotion bug — which the broken
probe had hidden behind an empty string. The probe now reports `<missing>` for an absent
member rather than printing nothing.

### Both defects FIXED — RESULT 2026-09-08: the galaxy is built to match the lobby

Both players now pick their empire in the client and the host generates a galaxy
containing exactly those empires:

```
# session: mode=competitive players=2 mySlot=0 stars=15 ai=2 seed=4242
#   slot 0: Mitch — Terran Union (race 0, gov 0)
#   slot 1: Friend — Ackdarian Compact (race 1, gov 2)
# makesave: empire for slot 0 — Terran Union (race 0, gov 0)
# makesave: empire for slot 1 — Ackdarian Compact (race 1, gov 2)
# makesave: competitive — 2 human empire(s)
# makesave: EmpireAutoGenerationState = SpecifiedAndAutoGenerated (3)
# makesave: generating — stars=15, 2 AI empire(s), seed=4242
# verify: galaxy generated 9 empire(s)
# verify:   [0] name='Independent'                          race=255 gov=-1  isPlayer=False
# verify:   [1] name='Najarca Technocracy'                  race=1   gov=6   isPlayer=False
# verify:   [2] name='Terran Union'                         race=0   gov=0   isPlayer=True
# verify:   [3] name='Ackdarian Compact'                    race=1   gov=2   isPlayer=True
# verify:   [4] name='Atuuk Domain'                         race=9   gov=4   isPlayer=False
# verify:   [5] name='Aquareos Perfection'                  race=5   gov=11  isPlayer=False
# verify:   [6] name='Ancient Mortalen Planet Destroyer AI' race=248 gov=32765
# verify:   [7] name='The Hive'                             race=249 gov=32766
# verify:   [8] name='Harmonious Void'                      race=5   gov=32767
# verify: RESULT — configured empire names DID survive generation
# makesave: player empire already set by generation — 'Terran Union' gov=0 race=0
```

Both configured empires are present with the exact requested race and government, both
flagged `IsPlayer`, alongside three auto-generated AI empires — which is precisely what
`SpecifiedAndAutoGenerated` promises.

#### Fix 1 — `EmpireAutoGenerationState` was the whole problem

It is a **flags** enum read through `DataHelpers.GetFlag`:

| Value | Meaning |
|---:|---|
| 0 | `SpecifiedEmpiresOnly` |
| 1 | `AutoGenerated` |
| 2 | `SpecifiedEmpires` |
| 3 | `SpecifiedAndAutoGenerated` |

It defaulted to `AutoGenerated (1)`, so bit 2 — "use the caller's `Empires` list" — was
never set and everything written into `Empires` was discarded in silence. One
`SetField(settings, "EmpireAutoGenerationState", (byte)3)` fixes it.

**This also retracts an earlier conclusion.** "`IsPlayer` does not survive
`Galaxy.Generate`" was wrong: `IsPlayer` survives fine once the list is actually read. The
whole `EnsurePlayerEmpire` promotion hack existed to work around an unset flag. It is kept
as a fallback for the no-session solo path, where it is still needed, and it now logs when
it stands down rather than returning in silence — because "nothing printed" was
indistinguishable from "the hook never ran", which cost a debugging round.

#### Fix 2 — promotion skipped `Independent`, and the crash pile proves it mattered

`IsPlayable()` now rejects `DominantRaceId == 255` and governments outside `0..32699`, and
promotion prefers a **name** match against the session before falling back to the first
playable empire.

This was not cosmetic. Promoting `Independent` produced **17,686 crash dumps** across the
evening's runs, in two families (`docs/crashdumps/`):

- 17,619 × `NullReferenceException`, top frames `Empire.DoTasksIndependentEmpire`,
  `IdentifyRefuellingPointBasesWeCanDockAt`, `GetScientistsAtResearchStations`,
  `CalculateResearchPointsMaximum` — full empire task processing run against a
  pseudo-empire that has none of the backing data.
- 67 × `IndexOutOfRangeException` in `Colony.CalculateCorruption` →
  `FindNearestCapitalByCorruptionReductionRatio` → `SystemPathTimeSet.GetPathTimesForSystem`
  — **NOT this bug.** These are the galaxy swap, they are still open, and they were
  briefly and wrongly filed under `Independent` here. A single instance playing a correct
  empire with no state apply produced zero dumps in 58,000 cycles; a two-instance run
  doing a swap produced 518. See `multiplayer.md`.

**DW2 swallows these**: it writes a dump and carries on, so runs continued for 40,000
cycles while quietly emitting thousands of dumps a minute. Nothing in the mod's own log
said anything was wrong.

#### The dumps then broke the game, which is worth knowing

At ~17.7k files DW2 stopped starting: one launch hung for 400 s without ever opening its
own `SessionLog`, the next died at startup with `0xE0434352` before our module initializer
ran. Clearing `data/Logs/*CrashDump*` and the stale `data/SessionActive` (left by
force-killing the game) restored it.

Two process lessons from that hour:

1. **Read the right log.** The `SessionLog.txt` I first read belonged to the *previous*
   run's PID. The live process had written no log at all — "the file exists and looks
   plausible" is not "this file is about the thing I am measuring".
2. **Prove the game started before blaming the feature.** The harness now has a startup
   guard that waits for `SessionLog` to be touched and reports `STARTUP STALL` distinctly
   from a generation failure. A separate self-inflicted bug — a launcher script missing
   `-WorkingDirectory` — presented identically, and cost another round.

## Content packs are checked before anyone launches — 2026-11-09

**Why.** mitch re-enabled the four DLC in Steam; Taylor's PC probably owns none. The mod is
host-authoritative, so two PCs with different packs means a galaxy one of them cannot draw
(the 2026-09-10 crash) or a load that stops at a dialog (2026-11-08). The 2026-09-10 check
compared pack names from bundle files and disabled Start, but it had no protocol number of
its own, so an older launcher looked like "a player with fewer packs", the joiner only learnt
of a mismatch through the shared session, and the host had to close the lobby for a retry.

**What each side reports** (`PlayerSlot`, lobby protocol 2):

| field | meaning |
|---|---|
| `lobbyProtocol` | `LobbyProtocol.Version`. A protocol-1 slot deserialises with 0 here. |
| `gameVersion` | `DistantWorlds2.exe`'s product version, e.g. `1.3.8.4+820e12…`; `unknown` if unreadable. |
| `contentPacks` | installed pack names — the protocol-1 field, kept so a protocol-1 host still parses the slot. |
| `contentPackDetails` | every pack DW2 knows, each with `installed`, `steamAppId`, `version`, `note`. |

*Installed* means the pack's bundle files are on disk **and** Steam does not list it under
`DisabledDLC` in `steamapps\appmanifest_1531540.acf` (read, never written). *Version* is the
DLC depot's manifest id from the same file -- the game has no per-pack version of its own,
and that id is the closest thing Steam reports. It is null for a GOG or Matrix install.

**What the host does on Join** (`LobbySession.ReasonToRefuse`), in the order a player
should fix things: lobby protocol, then game build (when both are known), then content
packs -- the installed sets must match, and versions must match where both sides know
them; a version known on one side only is a log line, not a refusal. On a refusal the host
logs `REFUSED <name>: <reason>`, sends `Refused` (message 4, a UTF-8 sentence), waits
250 ms for it to leave, closes that connection and **keeps listening**. The joiner shows the
sentence in a dialog and gets *Open lobby* back, so the fix-and-retry needs nothing from
the host.

**Older peers.** A protocol-1 joiner arrives with `lobbyProtocol` 0 and is refused with
"speaks lobby protocol 1 or older"; its own log can only say `Ignoring unexpected lobby
message Refused` and then `The other player disconnected` -- the most a build with no
version check can do, and the host's log carries the reason. A protocol-2 joiner on a
protocol-1 host is admitted (unknown JSON fields are ignored) and receives a Session with
`protocolVersion` 1; it refuses that session itself, with the same dialog, and the old host
sees a disconnect.

**One comparison.** `src\Shared\ContentPackCompare.cs` is compiled into the mod, the lobby
and `src\Dw2MpTests` (a console runner with no framework; `dotnet run --project
src\Dw2MpTests` prints ~~44 checks~~ one line per check and exits 1 on a failure;
(corrected 2026-11-10: 44 was the count when this was written; the runner has grown with
the join hardening and the desync work, and on main at 9998017 its source has 133 checks.
That count is read from the source, not from a run -- the PC that made this audit has no
.NET SDK). `src\Shared\SteamAppManifest.cs`
parses the `.acf`. The mod's `Content` message format is unchanged, so the in-game
`NetSession.ProtocolVersion` stays 2; what changed in the mod is that a protocol or
game-version mismatch in `Hello` now blocks state exchange the way a content mismatch did.

**Example** -- what mitch's PC reported on 2026-11-09 (lobby Log tab):

```
Game build: 1.3.8.4+820e122338180652f8f6e544f665261b4b2acc47
Content packs installed: Factions - Ikkuro and Dhayut, Factions - Quameno and Gizureans, Return of the Shakturi, Factions - Atuuk and Wekkarus
  Factions - Ikkuro and Dhayut: installed (bundles on disk, depot manifest 2085203991787705401; Steam app 2140010)
  Factions - Quameno and Gizureans: installed (bundles on disk, depot manifest 4707671932583055779; Steam app 2409480)
  Return of the Shakturi: installed (bundles on disk, depot manifest 1480154476699251668; Steam app 2926560)
  Factions - Atuuk and Wekkarus: installed (bundles on disk, depot manifest 650156249882694280; Steam app 3350550)
  Shadows Rising: NOT installed (bundles not on disk; Steam app 3989670)
```

If Taylor joins with the base game, the host logs and Taylor's dialog says:

> Content packs differ. Mitch (host) has Factions - Atuuk and Wekkarus, Factions - Ikkuro and
> Dhayut, Factions - Quameno and Gizureans, Return of the Shakturi; Taylor does not. Both
> players need the same DLC installed and ticked in Steam (Distant Worlds 2 > Properties > DLC).

**Playing with the common set -- investigated, not built.** `DWGame.Initialize` decides each
pack by calling the delegate `DWGame.CheckContentPackInstalled(name)`, which the store layer
assigns before Initialize. True sets `ContentPack.Installed`, logs `Content Pack installed:`,
and for two packs flips a `Galaxy` flag (`SetShakturiInstalled`, `SetPirateExpansionInstalled`);
then `Galaxy.StripDisallowedFeatures()` runs, those flags gate `LoadBundle` for the pack
bundles, and a race whose bundle is not mounted gets `IsPlayable = false`. So a Harmony
prefix on that delegate's `Invoke` returning false for the packs the *other* player lacks
would make the host's game behave exactly as if it did not own them -- in memory, with no
write to the game folder or to Steam, and the lobby already knows both inventories before
either game launches. It is not built because it cannot be verified without a launch, and
the failure mode (a pack half-disabled: definitions present, assets not) is the one that
crashed the 2026-09-10 join. The zero-code version exists today: the host unticks the extra
DLC in Steam (*Properties > DLC*), Steam removes those depots, and both lobbies report the
base game.

**Not verified here** (no game launch, one PC): the refusal between two lobbies on two PCs,
and the in-game `Hello` refusal. When it is tried: the host's Log tab should show
`REFUSED Taylor: Content packs differ. …` and go on listening; Taylor's lobby should show the
dialog and have *Open lobby* enabled again; after Taylor fixes the DLC and joins again, the
host should admit with `Taylor joined as … Game 1.3.8.4+…, content packs: …`.

**What this check is, and is not.** It is **cooperative**: each launcher reports its own
packs, build and protocol, and the other side believes it. It saves two honest players an
afternoon lost to a mismatch; it is no defence against a modified launcher that lies, and the
game-security review of 2026-11-09 asked for that to be said plainly. What that review did
harden, same day: every string from the other machine is capped and stripped of control
characters before it reaches the log pane, the player list or `session.json`
(`JoinPolicy.SanitiseSlot`, `PeerText`); a Join with no empire, an empty name, a null pack
entry or more than 32 packs is "not a player slot" and is dropped; a protocol-2 slot with no
`contentPackDetails` is refused rather than compared by names (which would have skipped the
version check); a lobby frame is at most 64 KB; a connection that throws is logged and the
host keeps listening; a repeat Join on an admitted connection is ignored instead of
appending a second slot; the joiner validates the host's Session (empire present, at most 8
players, `mySlot` names a player) and shows the host's refusal sentence capped at 600
characters under "The host's launcher said:"; the lobby logs any fault on its UI thread to
`%LOCALAPPDATA%\Dw2Mp\lobby-errors.log` instead of dying; the `.acf` parser refuses nesting
past 64 levels; and in the mod, the receive side now honours the handshake block too (a
peer that failed it cannot have its state or commands applied just because it sent them).

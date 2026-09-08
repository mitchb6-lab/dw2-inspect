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

# Crash dump samples

Three representative dumps kept from the 17,686 DW2 wrote on 2026-09-07/08. They are
**two unrelated bugs**, and it is worth being precise about which is which — an earlier
version of this file attributed all of them to the first, which was wrong.

| File | Exception | Count | Cause |
|---|---|---:|---|
| `nullref-independent-empire.txt` | `NullReferenceException` in `IdentifyRefuellingPointBasesWeCanDockAt` via `DoTasksIndependentEmpire` | part of 17,619 | `Independent` promoted to player — **fixed** |
| `nullref-research-scientists.txt` | `NullReferenceException` in `GetScientistsAtResearchStations` via `CalculateResearchPointsMaximum` | part of 17,619 | same — **fixed** |
| `indexoutofrange-corruption-pathing.txt` | `IndexOutOfRangeException` in `SystemPathTimeSet.GetPathTimesForSystem` via `Colony.CalculateCorruption` | 67 that night, 518 on 2026-09-08 | **the galaxy swap — still open** |

## Bug 1 — `Independent` promoted to the player empire (fixed)

`EnsurePlayerEmpire` promoted `Galaxy.Empires[0]`, which is always `Independent`, DW2's
neutral pseudo-empire (`DominantRaceId=255`, `GovernmentId=-1`). The game then ran full
empire task processing against an empire with none of the backing data those subsystems
assume. Fixed by `IsPlayable()` — see `../lobby.md`.

## Bug 2 — derived path caches survive a galaxy swap (open)

`StartGameExisting` replaces the galaxy, but `SystemPathTimeSet` keeps path tables keyed
to the *previous* galaxy's system layout, and the next `Colony.CalculateCorruption` indexes
past the end of one.

**Measured separation, 2026-09-08.** A single instance playing a correctly chosen empire
with no state apply ran 58,000 cycles and produced **zero** dumps. A two-instance run where
both sides perform a swap produced **518**. The swap is the variable, not `Independent` and
not the `FixedStep` clock.

This one matters more than bug 1 did, because the swap *is* the host-authoritative
mechanism. See `../multiplayer.md`.

## Two things to remember about DW2's error handling

1. **It swallows these.** A dump is written and the game continues, so a broken run looks
   healthy in our own log while emitting thousands of dumps a minute.
2. **The pile itself becomes a fault.** At ~17.7k files the game stopped launching — one
   run hung 400 s without opening its `SessionLog`, the next died with `0xE0434352` before
   our module initializer ran. Clear `data/Logs/*CrashDump*` (and a stale
   `data/SessionActive`) if the game will not start.

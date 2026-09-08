# Crash dump samples

Three representative dumps kept from the 17,686 DW2 wrote on 2026-09-07/08. All have the
same root cause: `EnsurePlayerEmpire` promoted `Galaxy.Empires[0]`, which is always
`Independent` — DW2's neutral pseudo-empire (`DominantRaceId=255`, `GovernmentId=-1`) —
so the game ran full empire task processing against an empire with none of the backing
data those subsystems assume.

| File | Exception | Count in the pile |
|---|---|---:|
| `nullref-independent-empire.txt` | `NullReferenceException` in `IdentifyRefuellingPointBasesWeCanDockAt` via `DoTasksIndependentEmpire` | part of 17,619 |
| `nullref-research-scientists.txt` | `NullReferenceException` in `GetScientistsAtResearchStations` via `CalculateResearchPointsMaximum` | part of 17,619 |
| `indexoutofrange-corruption-pathing.txt` | `IndexOutOfRangeException` in `SystemPathTimeSet.GetPathTimesForSystem` via `Colony.CalculateCorruption` | 67 |

Two things worth remembering:

1. **DW2 swallows these.** It writes a dump and keeps running, so a broken run looks
   healthy in our own log while emitting thousands of dumps a minute.
2. **The pile itself becomes a fault.** At ~17.7k files the game stopped launching — one
   run hung 400 s without opening its `SessionLog`, the next died with `0xE0434352` before
   our module initializer ran. Clear `data/Logs/*CrashDump*` (and a stale
   `data/SessionActive`) if the game will not start.

Fixed by `IsPlayable()` — see `../lobby.md`.

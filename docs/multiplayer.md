# Multiplayer for Distant Worlds 2 — architecture study and plan

**Goal:** a mod that lets two or more people play DW2 together in the same universe.

DW2 ships no multiplayer. This document records what the game's actual architecture
allows, what it forbids, and the sequence of work that answers the remaining question.

---

## M0 — What exists (completed)

### The good news, and there is a lot of it

Far more of a multiplayer design is already present than the game's marketing implies.

| Piece | Status |
|---|---|
| **Command vocabulary** | `GameTaskType` — ~205 values covering every player action |
| **Command object** | `GameTask` — `Type`, `Empire`, `Entities`, `Values`, `Offset`, plus explicit id fields |
| **Command serializer** | `GameTaskList.ReadFromStream` / `WriteToStream` — **implemented** |
| **Wire packet** | `MessagePacket` — SerialNumber, SenderEmpireId, Time, timings, tasks |
| **Packet serializer** | `MessagePacket.ReadFromStream` / `WriteToStream` — **implemented** (205 / 214 IL bytes) |
| **Reference resolution** | `Item<T>` holds an **id** and resolves via `Resolve(Galaxy)` — ids travel, objects don't |
| **Send/receive paths** | `GameClient.SendMessageToServer`, `GameServer.SendMessageToAllClients` |
| **Per-player identity** | `GameClient.PlayerEmpireId`, `GameStartSettingsEmpire.PlayMode` |
| **Clock sync** | `GetServerTimeDirect`, `LastServerTime`, `LocalTimeAtLastServerTime`, `FramesSinceLastServerTime` |
| **Code injection** | `--low-level-inject` — a supported, first-party loader (see `findings.md` §2) |
| **Patching library** | `0Harmony.dll`, already in the game directory |

`Item<T>` matters more than it looks. A `GameTask` created on machine A carries entity
**ids**, not object references, and `Resolve(Galaxy)` turns them back into objects against
whatever galaxy it is handed. That is precisely what command forwarding needs.

### The bad news, and it is structural

**`GameClient` is not a network client. It is a worker thread.**

It holds no game state at all — its fields are `Server`, two queues, timing data, a view
position and `PlayerEmpireId`. `UpdateGameAsClient` takes a `Galaxy` and then reads and
**mutates `galaxy.Ships`, `galaxy.Locations`, `galaxy.Creatures` directly**, in parallel
(`Task.Run`, `MaxDegreeOfParallelism`, `Galaxy.AddTask`), in blocks sized by
`_CurrentShipBlockSize` and friends.

So the client/server split is **intra-process work partitioning over shared memory**,
inherited from DW1. `SendMessageToAllClients` writing only to `GameClients[0]` and never
looping is consistent with that: there has only ever been one worker.

Two consequences:

1. **There is no state-replication path, and never was one.** Client and server are the
   same objects in the same heap. Nothing in the codebase distinguishes "state the server
   has" from "state the client has", so a host-authoritative design would mean building a
   replication layer across the entire data model from nothing.
2. **Therefore lockstep is the architecture that fits.** Each machine runs its own full
   `Galaxy` + `GameServer` + `GameClient`, and machines exchange `GameTask`s. The
   existing serializers are exactly the payload. This is also, evidently, what the
   original design was reaching for.

### The risk that decides the project

Lockstep requires that both machines, given the same starting state and the same command
stream, compute **identical** results. DW2 is hostile to that in three compounding ways:

1. **Floating point everywhere.** The simulation is `float`/`double` throughout
   (`EconomyFactorSet.IncomeFactors` is a `Single[]`). .NET does not guarantee
   bit-identical float results across CPU models or JIT versions.
2. **The simulation is multithreaded.** `UpdateGameAsClient` dispatches block processing
   through `Task.Run`. If any entity update depends on another entity updated in the same
   pass, completion order changes the result — and completion order is not reproducible.
3. **Work partitioning is derived from wall-clock time.** `AdjustClientBlockSizes` reads
   `TimeToProcessLastShipBlockMilliseconds` — *measured milliseconds* — and resizes blocks
   against `_TargetShipCycleTimeMaximum`/`Minimum`. **How the simulation is divided up
   depends on how fast your CPU is.**

Point 3 is the nastiest, because it means block boundaries differ between a fast and a
slow machine *by design*.

### What is NOT yet known, and must be measured before anything is built

**Whether any of that actually changes outcomes.**

Block partitioning only breaks determinism if entity updates are order-dependent *across*
block boundaries. If each ship's update reads a consistent snapshot and writes only its
own state, then block size is irrelevant to the result and parallelism is safe. Plenty of
simulations are built exactly that way.

This is an empirical question with a cheap answer, and it decides the entire project. It
must not be assumed in either direction.

> Methodology note, learned the hard way earlier in this repo's history: an inference from
> structure was already wrong once here (method-body sizes on disk "proved" the networking
> was unimplemented; it wasn't). **Measure. Do not conclude.**

---

## Milestones

### M1 — Get our code running inside the game ✅ DONE

`src/Dw2Mp` — a `[ModuleInitializer]` loaded with:

```
DistantWorlds2.exe --tool-mode --skip-splash --low-level-inject "<path>\Dw2Mp.dll"
```

Verified output (`%LOCALAPPDATA%\Dw2Mp\boot.log`):

```
process     : ...\Distant Worlds 2\DistantWorlds2.exe
pid         : 42380
runtime     : 8.0.0 / .NET 8.0.0
assembly    : DistantWorlds.Types 1.3.6.3
assembly    : DistantWorlds.Core 1.3.6.3
assembly    : DistantWorlds2 1.3.6.3
assembly    : Stride.Engine 4.2.0.28
harmony     : 2.3.3.0
patching    : OK ("unpatched" -> "patched")
```

**The delivery path is proven end to end**: our assembly loads into the game process, the
game's own types are reachable, Harmony 2.3.3 is live, and a patch applies and takes
effect. Nothing was installed into the game directory to achieve it.

Three things that had to be right, and would each have failed silently:

- **Target `net8.0`.** The game hosts `Microsoft.NETCore.App 8.0.0`. A `net9.0` assembly
  will not load, and `LowLevelInjection` catches the failure and returns quietly.
- **`Private=false` on every game reference.** Copying game DLLs next to ours risks
  loading a *second* instance of `DistantWorlds.Types`, so patches would apply to types
  nobody is using.
- **Log outside the game directory.** The install is under Program Files; a write there
  is not reliably permitted, and the log is the only evidence the injection worked.

### M2 — The determinism experiment ← *the decision point*  ⚠️ REDESIGN REQUIRED

**First run produced a finding that invalidates the test as originally designed, and it
is more important than the test would have been.**

Two things were learned by running it:

1. **A loaded save arrives paused.** The server cycles happily — 194,000 cycles in 900
   seconds — while `Galaxy.Time` never moves. Fixed by calling `GameServer.ResumeGame()`
   and `ChangeGameSpeed()` from the harness on the first cycle.

2. **DW2's simulation clock is a wall-clock stopwatch, not a tick counter.**
   `GameServer.ResumeGame()` decompiles to exactly `_Stopwatch.Start()`, and
   `Galaxy.Time` is a real-world `DateTime` that advances with real elapsed time scaled
   by game speed. Observed: `21:47:51` → `21:47:53` across 16,000 server cycles.

**Why that breaks the experiment.** Comparing state at equal `Galaxy.Time` compares two
runs that have executed *different numbers of update steps*, because steps are paced by
wall-clock and the machine is not equally loaded from moment to moment. A perfectly
deterministic DW2 would still fail such a test. It cannot distinguish "the arithmetic
diverges" from "the runs took different numbers of steps", so a divergence result would
mean nothing.

**Why it matters far beyond the test.** Lockstep requires a **fixed-timestep** model:
step N on machine A must correspond to step N on machine B. DW2 has no such notion. Its
simulation is variable-timestep, wall-clock-paced, multithreaded, and partitions its work
by measured milliseconds. There is no existing concept of "the same simulation step" for
two machines to agree on.

So the three risks listed above are really one: **DW2 does not have a simulation tick.**

#### M2b RESULT — measured 2026-09-07: **not deterministic**

Two runs, same 37.5 MB save, fixed 100 ms timestep, `AdjustClientBlockSizes` disabled,
compared at equal tick counts with raw bytes dumped for diffing.

| tick | size Δ | differing bytes | first diff offset |
|---:|---:|---:|---:|
| 1 | 0 | 475 | 2,547,330 |
| 1001 | +5,510 | 13,554,512 | 7,634 |
| 2001 | +7,151 | 12,664,066 | 7,634 |
| 3001 | +12,520 | 12,729,764 | 7,634 |

**Tick 1 is effectively identical.** The few hundred differing bytes are lazily-computed
caches — verified by diffing the bytes: one run holds computed values while the other
holds `0x7F7FFFFF` (`float.MaxValue`), preceded by a `00`/`01` dirty flag. A flag plus
six floats is a bounding box that had not been computed yet. Not simulation state.

**By tick 1001 roughly a third of the galaxy differs**, the serialised sizes no longer
match, and the size gap widens every tick. That is comprehensive structural divergence:
different numbers of entities, not drifting values.

**A fixed timestep is necessary but nowhere near sufficient.** The speed and scale of the
divergence point at thread completion order rather than float rounding. Pure floating
point drift starts tiny and amplifies gradually; this is immediate and total, which is
the signature of parallel workers processing entities in different orders — and of any
shared RNG being consumed in a different order as a result.

**What remains untested:** forcing the parallel block processing (`Task.Run` /
`MaxDegreeOfParallelism` in `UpdateGameAsClient`) to run sequentially. That is the last
cheap lever. If sequential execution reproduces, lockstep is viable but requires running
the simulation single-threaded — a serious performance cost on a large galaxy, and a
design decision rather than a patch. If it still diverges, the cause is float arithmetic
and the intervention needed is a rewrite, not a mod.

#### The redesigned experiment (M2b)

Make one, and test determinism inside it:

1. Harmony-patch the clock so `Galaxy.Time` advances by a **fixed increment per server
   cycle**, removing wall-clock from the simulation entirely.
2. Run twice, comparing state at equal **cycle counts**.

This is a valid test — identical step sequences, so any divergence is genuinely the
arithmetic — and it is not throwaway work: forcing a fixed timestep is the first piece
of the actual fix, not just instrumentation for measuring it. If state then matches
across runs, the remaining obstacles (thread ordering, block sizing) are attackable one
at a time. If it still diverges with an identical step sequence, the cause is float
arithmetic itself and lockstep needs a much bigger intervention.

#### Built so far

`src/Dw2Mp/Determinism.cs` + `Scripts/determinism-run.ps1`. Armed only when
`DW2MP_DETERMINISM=1`, since it exits the process when a run completes.

`src/Dw2Mp/Determinism.cs` + `Scripts/determinism-run.ps1`. Armed only when
`DW2MP_DETERMINISM=1`, since it exits the process when a run completes.

**Blocked on one manual step: the game needs a saved game to exist.**
`--new-game` cannot be used to drive this, because with unconfigured
`GameStartSettings` (234 bytes, no empires) the game throws during setup:

```
System.NullReferenceException
   at DWGame.InitializeSinglePlayerGame(Galaxy, Empire playerEmpire, DateTime)
   at DWGame.StartGameNewGenerate(Galaxy, GameStartSettings)
```

`playerEmpire` is null — those settings are normally written by the New Game screen.
Verified unrelated to our patch: the harness reported `0 server cycle(s) seen`, so
`UpdateGameAsServer` had not run when the game failed.

**Loading a save is the better experiment anyway.** Both runs then start from
byte-identical state, which removes galaxy generation as a variable, so any divergence
is unambiguously the simulation — exactly the property lockstep needs. `-Mode continue`
is therefore the default.

Two diagnostics that earned their place immediately:

- **Snapshot taken twice back to back.** The simulation runs on background threads, so
  a single hash could differ between runs from a torn read rather than a real
  divergence. Agreement means the instant is stable; disagreement means the row is not
  evidence and the script says so.
- **Heartbeat + watchdog.** An empty result is otherwise ambiguous between "patch never
  fired", "fired but game time is frozen", and "time advancing slower than the
  interval". `0 server cycle(s) seen` immediately pointed at the game, not the hook.
Harmony-patch a hash of galaxy state (positions, stocks, populations, ids) computed at
fixed **game-time** intervals, dumped to a file. Then:

- same seed, same machine, two runs → do the hashes match?
- if yes: same seed, two different machines → do they match?

Outcomes:
- **Deterministic** → lockstep is viable and this project is very achievable, because
  every other piece already exists. Proceed to M3.
- **Non-deterministic** → identify *which* of the three causes it is. Wall-clock block
  sizing may be patchable to a fixed size; thread parallelism may be forceable to
  sequential. Both cost performance but are far cheaper than a replication layer.
  Re-plan against what we learn.

### M3 — Transport and command relay
Two game processes on one machine, loopback socket, forwarding `MessagePacket`s. Patch
the `PlayMode == Multiplayer` early-returns in `SendMessageToServer` and
`SendMessageToAllClients`, and remove the `GameClients[0]` single-client assumption.

### M4 — Two empires, two machines
Session setup, join handshake, initial state transfer (the save format is the obvious
vehicle for the join), and command relay across a real network.

### M5 — Playable
Desync detection, reconnection, pause/speed negotiation, and the UI to start and join.

---

## Standing constraints

- **The mod manager cannot deliver this.** Code mods need `--low-level-inject`, so
  distribution is a launcher or a documented flag, not Steam Workshop.
- **Every claim in this document is checkable** with `dw2inspect`. Where it says
  "implemented", that means real IL was read, not inferred from a signature.

#### M2c RESULT — single-threaded, measured 2026-09-07: **still not deterministic**

Same save, fixed timestep, pinned block sizes, plus every `TaskHelper` parallelism knob
forced to 1 (`BackgroundTasks`, `ForegroundTasks`, `PriorityLocations`,
`ExclusiveLoading`, and the three integer degree properties).

| tick | size Δ | differing bytes | first diff |
|---:|---:|---:|---:|
| 1 | 0 | **0** | none |
| 501 | −16,496 | 9,129,550 | 7,634 |
| 1001 | −16,547 | 13,646,145 | 7,635 |

**Tick 1 is byte-identical across 39.3 MB.** That is worth stating: it confirms the
earlier tick-1 differences were lazy caches, and it validates the harness — when two
states genuinely match, this measurement reports exactly zero.

**And the simulation still diverges within 500 ticks.** Three interventions — fixed
timestep, frozen work partitioning, single-threaded execution — are not sufficient.

Two candidates remain, and neither is cheap:

1. **Residual concurrency.** `MaxDegreeOfParallelism = 1` governs `Parallel.For` and
   `ParallelWhile`, but `UpdateGameAsClient` also calls `Task.Run` directly and hands
   the result to `Galaxy.AddTask`. Those still run concurrently. Fully serialising would
   mean intercepting every task spawn in the simulation.
2. **Hash-order-dependent iteration.** .NET `Dictionary`/`HashSet` enumeration order
   depends on hash codes, and any type using the default reference hash code varies run
   to run with memory layout. That produces exactly this signature: identical at load,
   massively divergent the moment iteration happens. It is also effectively unfixable
   from outside — it would mean auditing every collection iteration across 650 types
   whose method bodies are encrypted at rest.

**Assessment.** Three cheap interventions have each been necessary and none sufficient.
What remains is not modding: it is reimplementing DW2's simulation scheduler and
collection iteration order against an obfuscated binary. Lockstep should be treated as
closed unless new evidence appears.

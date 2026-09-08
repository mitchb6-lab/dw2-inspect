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

---

## Branch `mp/host-authoritative`

Lockstep is closed (M2b/M2c). This branch pursues the one architecture that does not
need determinism: **one machine simulates; clients send `GameTask`s and receive galaxy
state.** The `mp/handoff` branch will explore sequential save-passing from the same base
commit for comparison.

### Why this is possible at all

Two entry points make it more than theory, both verified by decompilation:

| Piece | Signature | Role |
|---|---|---|
| Serialise | `Galaxy.WriteToStream(BinaryWriter, GameGalaxyData)` | Host produces state |
| Deserialise | `Galaxy.ReadFromStream(BinaryReader, GameGalaxyData&, List&)` → `Galaxy` | Client parses state |
| **Apply** | `DWGame.StartGameExisting(Galaxy, DateTime, GameGalaxyData)` | Client adopts state |

`StartGameExisting` taking a ready-made `Galaxy` is the crucial one: there is an existing,
in-memory path for "here is a galaxy, run with it". No file I/O and no process restart.

### M3a RESULT — transfer cost, measured 2026-09-07

Late-game save, 37.5 MB on disk, measured in-process on the dev machine:

| Metric | Value |
|---|---|
| Raw serialised galaxy | 39,333,035 B |
| Compressed (Deflate, Fastest) | 9,214,272 B — **23.4 %** |
| Serialise (host cost) | **373 ms** |
| Compress | 338 ms |
| **Deserialise (client cost)** | **220 ms** |

**Deserialising a full late-game galaxy costs 220 ms.** The multi-minute load times seen
all evening are engine and scene setup, which an already-running client would not repeat —
which is precisely why this had to be measured in-process rather than by timing a launch.

Budget for a full sync: host ~700 ms (serialise + compress), 9.2 MB on the wire, client
220 ms. At one full sync every 10 s that is ~920 KB/s (~7.4 Mbps) — comfortable on a LAN,
heavy but feasible on good broadband. Deltas over short intervals should cut it sharply,
and Deflate/Fastest was chosen precisely because a sync happens while someone waits.

### The next gate, and it is NOT yet measured

**Deserialising is not the same as applying.** 220 ms buys a `Galaxy` object in memory.
What it does not cover is `StartGameExisting` — rebinding the scene, re-creating visual
entities, rebuilding whatever caches the renderer holds. That could be the real cost, and
it is plausibly where the minutes actually went.

**M3b: measure `StartGameExisting` on an already-running client.** If applying state is
also sub-second, host-authoritative is viable and the rest is ordinary networking. If it
rebuilds the world every time, the design needs a cheaper application path — patching
state into the live galaxy rather than replacing it — which is a much larger job.

Do not build transport before knowing this number.

### M3b RESULT — applying state to a running client, measured 2026-09-07

```
raw=39,994,915B   deserialise=819ms   StartGameExisting=8ms   (total client cost 828ms)
```

**`StartGameExisting` costs 8 ms.** Adopting a foreign galaxy is nearly free on a running
client; the cost is *parsing* it. That fits the stack trace seen while debugging this —
the call loads facility images, and on a live client that content is already in memory.

**Correction to M3a.** M3a reported deserialisation at 220 ms. That measurement used
`RuntimeHelpers.GetUninitializedObject` as the read target, skipping `Galaxy`'s
constructor, so collections it allocates stayed null and the read bailed out early. The
same bug made `StartGameExisting` die inside `LoadImagesForFacilities` with a bare
`NullReferenceException` that looked like the game rejecting foreign state. **Use 819 ms**
— the only run that produced a galaxy the game actually accepted.

Two steps a receiving client must perform, both learned the hard way:

1. Construct the target galaxy properly (`Activator.CreateInstance`), not
   `GetUninitializedObject`.
2. Call `Galaxy.CopyStaticBaseDataToGalaxyInstance` on the incoming galaxy. A
   deserialised galaxy carries per-game state but not the static definition tables
   (facilities, races, components), and asset loading walks those.

#### Corrected sync budget

| Stage | Cost |
|---|---|
| Host serialise | 373 ms |
| Host compress (Deflate/Fastest) | 338 ms |
| Wire | 9.2 MB (23.4 % of 39.3 MB) |
| Client deserialise | 819 ms |
| Client apply | 8 ms |
| **Client total** | **~830 ms** |

A sub-second client hitch per full sync, on a *late-game* 40 MB galaxy — the worst case.
At one sync every 10 s that is ~920 KB/s. Viable on a LAN; heavy but possible on good
broadband. Deltas over shorter intervals should improve both figures substantially.

**Both gates are now passed. Host-authoritative is viable.**

#### Not yet verified

The 8 ms says the call *returned*. It does not prove the client then ran correctly on the
incoming galaxy — no visual or behavioural check was made, and the process was killed on
a timer rather than observed. **M3c should apply a state that is visibly different (a
galaxy from a different point in time) and confirm the client actually shows it.** Do
that before building transport.

### M3c — adoption verification, measured 2026-09-07: **object swap PROVEN, contents unverified**

Captured a galaxy at tick 400, ran on to tick 1600 (exactly 120.0 s of game time later),
then applied the *older* captured state back to the running client.

```
CAPTURED 39,876,802B  hash=CAAEA4B28D512B36  time=21:48:31.159
live time before apply = 21:50:31.159  (delta 120.0s)
deserialise=648ms  StartGameExisting=14ms  (client total 662ms)
verify[1601]: timeReverted=False  isIncomingObject=True  -> hash mismatch
verify[1602..1603]: isIncomingObject=True
```

**`isIncomingObject=True` is the finding.** The galaxy that `UpdateGameAsServer` is handed
after the apply is reference-identical to the object we deserialised. `StartGameExisting`
swapped the running game onto received state, and the simulation continued cleanly. A
no-op cannot produce that.

**The other two checks were invalidated by our own instrumentation.** The test was designed
around "the clock must jump backwards", while running under `FixedStep`, which computes
time as `_StopwatchStartTime + cycleCount × StepMs`. The cycle counter is monotonic, so
time cannot go backwards regardless of what state is loaded — `21:50:31.259` is exactly one
100 ms tick past the pre-apply value. The hash check failed for the same reason: a
differing `Time` field alone changes the hash of a 40 MB blob.

Silver lining: it confirms `FixedStep` is exact — 1200 ticks × 100 ms = **120.0 s**, to the
millisecond.

**Still unverified: that the adopted galaxy's CONTENTS are the captured ones.** The next
check must use a signal the clock does not touch — entity counts, a named fleet's position,
an empire's cash — captured alongside the bytes and compared after the apply. Run that with
`DW2MP_STEP_MS=0` so the harness clock is out of the picture entirely.

Confirmed costs remain good: **~660 ms client cost** for a full 40 MB late-game galaxy,
of which the apply itself is 14 ms.

### M3c FINAL — content adoption PROVEN, measured 2026-09-07

Captured at tick 400, ran on to tick 1600 (120.0 s of game time), applied the older
captured state back to the running client.

| Point | Ships | Fingerprint |
|---|---:|---|
| Captured (tick 400) | 732 | `199DCEBD90703BB7` |
| Live before apply (tick 1600) | **1111** | `99F7D9D4B6453719` |
| Incoming (deserialised) | 731 | `25FB02C7E08F1C67` |
| **Live after apply (tick 1601)** | **731** | `837CC352C8EFD831` |

**The live ship population dropped from 1111 back to 731.** The simulation had built 379
ships over 120 game-seconds; applying the received state wound that back to the received
galaxy's population exactly. Together with `isIncomingObject=True`, the client is
provably running the received entity state, not merely holding the object.

Three notes on method, because each cost a run to learn:

- **Entity count is the right signal; the hash is not.** The fingerprint is sampled one
  tick after the apply, and ship positions and countdowns advance every tick, so it can
  never match. The population cannot change by hundreds in a single tick, so a count that
  reverts is decisive. The verdict logic now uses counts.
- **The harness clock must stay ON for this test.** With `DW2MP_STEP_MS=0` an earlier
  attempt produced captured == live-before (delta 0.0 s, identical fingerprints): the
  simulation never advanced, so the test had no discriminating power at all. **DW2's
  simulation only advances when `GameServer.Now` advances, and `ResumeGame()` alone does
  not achieve that** — the game stays effectively paused.
- **Comparing post-apply against the ORIGINAL capture is wrong by construction.** The
  fingerprint reads every primitive ship field and serialisation does not persist
  transient runtime ones, so a round-trip legitimately differs from its source. The
  comparison must be against the incoming object.

**Fidelity caveat: 732 captured → 731 after round-trip.** One ship does not survive
serialise/deserialise. Small, but it is a real fidelity gap that would compound over
repeated syncs, and it should be understood before this is trusted in a live session.

#### M3 verdict

Host-authoritative is **viable and verified end to end**: a running DW2 client accepts a
foreign galaxy, adopts its contents, and continues simulating.

| Stage | Cost |
|---|---|
| Host serialise | 373 ms |
| Host compress | 338 ms |
| Wire | 9.2 MB (23.4 %) |
| Client deserialise | 650–1,740 ms (varies with galaxy size) |
| Client apply | 8–23 ms |

The apply is nearly free; parsing dominates. Next milestone is transport (M4): loopback
first, two processes on one machine, relaying `MessagePacket`s and full-state syncs.

---

## M4a — transport, measured 2026-09-07: **WORKING**

`src/Dw2Mp/NetSession.cs` (host/client TCP) plus `src/Dw2MpProbe` (a stub peer).

Host is a real DW2 instance; the probe connects as a client and validates the wire.

```
host : sync #1 tick=400  raw=39,993,898B packed=9,287,295B serialise=249ms pack=262ms send=4ms
host : sync #2 tick=800  raw=41,225,825B packed=9,510,650B serialise=316ms pack=284ms send=2ms
host : sync #3 tick=1200 raw=41,454,100B packed=9,542,733B serialise=289ms pack=246ms send=9ms

probe: FullState #1 packed=9,287,295B raw=39,993,898B ratio=23.2% sha=535BD6AC9087C309
probe: FullState #2 packed=9,510,650B raw=41,225,825B ratio=23.1% sha=17528919DD762B80
probe: FullState #3 packed=9,542,733B raw=41,454,100B ratio=23.0% sha=FF0221D92C0D282F
probe: OK -- received 3 full state(s); framing, compression and host sync all work
```

Byte counts agree exactly end to end. Raw sizes **grow** across syncs (39.99 → 41.23 →
41.45 MB), which confirms the host is simulating between them — each state is genuinely
new, not the same buffer resent.

Send is 2–9 ms on loopback; the real cost is serialise (~250–320 ms) plus compress
(~250–290 ms), both currently inline on the simulation thread, so **the host visibly
hitches on every sync**. That is the first thing M4b should fix.

### Why a stub peer instead of two game instances

A DW2 instance reaches a **~15 GB working set** on the late-game save; the machine has
31.4 GB total and 12.8 GB free. Two instances do not fit. The stub also isolates the
variable: M3 already proved a client can adopt a galaxy, so what was unproven was
framing, compression and the host's sync loop — and a stub tests exactly that, so a
failure has one possible cause.

The two-process test needs a small early-game save (see below).

### Protocol

`[int32 payload length][byte type][payload]`, types `Hello=1 FullState=2 Command=3`.
Length-prefixed on purpose: a stream protocol that infers message boundaries is a day
lost to intermittent corruption. `ReadExactly` loops, because one `Read` can return fewer
bytes than asked for.

Constraints carried from earlier milestones, none optional:

- **Apply runs on the main thread.** The socket reader only enqueues; `DWGame.Update`
  drains. M3b died in `LoadImagesForFacilities` when this was attempted off-thread.
- **The pump drops stale states.** With full-state syncs an older snapshot is worthless
  once a newer one has arrived; applying them in order would stutter through dead worlds.
- **Both ends need `FixedStep`.** Without it `GameServer.Now` never advances and the host
  sits inert, resending identical state forever.

### Making a small save (`MakeSave.cs`)

Two DW2 instances will not fit in 32 GB on the late-game save (~15 GB working set each),
so the two-process test needs a small one. `DW2MP_MAKE_SAVE=1` generates and saves one
without anyone touching the New Game screen.

Result: **`data/SavedGames/MPTestSmall.DWGame`, 21 MB** (vs 37.5 MB). The game's own log
for the generated galaxy:

```
Dimensions: 6 x 6 sectors, Star Systems: 16, Total Stars/Planets/Moons/Asteroids: 1026,
Standard Empires: 3, Total Colonies: 2, Total Ships and Bases: 99, Total Creatures: 41
```

Its header matches a known-good save byte for byte (same version stamp and magic).

Four obstacles, each of which had to be read out of the game rather than guessed:

1. **`--new-game` cannot be used.** It reads the stub `GameStartSettings` the game writes
   on first run (234 bytes, no empires) and dies in `InitializeSinglePlayerGame` because
   `playerEmpire` is null. So the settings are built in-process and passed to
   `StartGameNew` directly.
2. **`IsPlayer` on a `GameStartSettingsEmpire` does not survive `Galaxy.Generate`.**
   `StartGameExisting` resolves the player through `Galaxy.Empires.GetPlayer()`, which
   returns the first `Empire` with `IsPlayer` set and null otherwise. Fixing the *input*
   does not work; a prefix that promotes the first empire when `GetPlayer()` is null does.
   It is a no-op for every save-loading path, so it cannot disturb M3/M4.
3. **`EndGenerateGame` then dies in `Empire.GenerateSituationDescription`.** Generation
   itself succeeds; this is the flavour blurb, and it dereferences a null *field* inside a
   non-null settings entry (our hand-built empire leaves several strings null). Skipped
   unconditionally — it only runs when MakeSave is active and a test save does not need it.
4. **`DWGame.SaveGame(String)` takes a PATH, not a save name.** A bare name writes
   `data/<name>` with no extension, where the load screen will never see it. Found by
   searching the filesystem for the file after `SaveGame` returned "successfully" and
   `SavedGames` was still empty — a silent success is the worst kind.

---

## M4 — TWO LIVE GAMES SHARING A UNIVERSE, measured 2026-09-07

Two real DW2 instances on one machine, host and client, over loopback, on
`MPTestSmall.DWGame`. **Seven consecutive full-state syncs, every one applied.**

```
host  : sync #1 tick=600  raw=23,140,735B packed=4,208,658B serialise=71ms pack=110ms send=2ms
client: state received packed=4,208,658B raw=23,140,735B (queued)
client: APPLIED sync #1  23,140,735B  read=171ms  apply=40ms  ships=90 empires=6
...
host  : sync #7 tick=4200 raw=23,137,898B packed=4,207,839B serialise=86ms pack=119ms send=1ms
client: APPLIED sync #7  23,137,898B  read=93ms  apply=28ms  ships=90 empires=6
```

**A galaxy simulated in one process is now being adopted by a second live game, repeatedly
and reliably.** That is the technical foundation of the original goal.

### Costs on the small save

| Stage | Small save (23 MB) | Late-game (40 MB) |
|---|---|---|
| Host serialise | 52–87 ms | 250–370 ms |
| Host compress | 87–126 ms | 250–340 ms |
| Wire | **4.2 MB (18.2 %)** | 9.2–9.5 MB (23.4 %) |
| Client deserialise | 79–171 ms | 650–1,740 ms |
| Client apply | 24–42 ms | 8–23 ms |
| **Client total** | **~110–210 ms** | ~660–1,760 ms |

Memory: **3.5 GB per instance** on the small save against ~15 GB on the late-game one, a
4× reduction — which is what makes two instances possible on a 32 GB machine at all.

### Honest limits

- **State flows one way.** The client sees the host's universe; it cannot yet act on it.
  Command relay (client → host `GameTask`s) is M4b and is the other half of multiplayer.
  The channel is already open and the `Command` message type is already defined.
- **Both instances load the same save and control the same empire.** Assigning the client
  a different empire is part of session setup, not yet built.
- **The host hitches** ~150–200 ms per sync on the small save (worse on a large one),
  because serialise and compress run inline on the simulation thread.
- **Raw sizes barely move between syncs** (23,137,898 twice in a row). Expected for a
  small early-game galaxy over ~7 game-minutes, but it means this run did not exercise
  heavy state change. A busier galaxy would be a better stress test.
- The 732 → 731 round-trip fidelity gap from M3c has not been investigated.

---

## Target: BOTH co-op and competitive (decided 2026-09-08)

Two modes are in scope, and they have very different requirements.

### Co-op — reachable from where we are

Players share a universe and may share or divide control. Full-state sync is *adequate*:
everyone seeing everything is not a problem when everyone is on the same side.

Needs: session setup, command relay (M4b), and a join flow. All within reach.

### Competitive, separate empires — needs one thing we do not have

Each player drives their own empire against the others. Two pieces:

**1. Per-player empire assignment — SOLVED IN PRINCIPLE.** `StartGameExisting` resolves
the player through `Galaxy.Empires.GetPlayer()` (first `Empire` with `IsPlayer`) and then
binds the entire UI to it via `DWControl.BindGalaxy(Galaxy, Empire)` and
`CheckBindUserInterfaceControls(Galaxy, Empire)`. A prefix that clears `IsPlayer`
everywhere and sets it on *this client's assigned empire id* makes that client play that
empire — UI, selection, orders, all of it. `GameClient.PlayerEmpireId` is set to match.

The mechanism is already proven: `MakeSave.EnsurePlayerEmpire` flips `IsPlayer` before the
bind and the game accepts it. Only the empire *choice* changes.

**2. Per-empire state filtering — NOT SOLVED, and it is the real work.**

Full-state sync ships the entire galaxy: every empire's fleets, research, colonies and
intentions. A client adopting it holds all of that in memory. No UI hiding fixes it —
the data is simply there. **For competitive play that is total information disclosure.**

Fixing it means producing a per-recipient view of the galaxy before serialising, and
`Galaxy.WriteToStream` writes everything. That is a per-empire projection of the whole
data model — comparable in scale to the problem that closed the lockstep route.

Options, none cheap:
- **Filter before send.** Build a redacted `Galaxy` per recipient (strip other empires'
  hidden state), serialise that. Needs to know exactly what DW2 considers visible, and
  must not break the client's simulation by removing something it needs.
- **Reuse the game's own fog model.** DW2 already computes what each empire knows; if that
  knowledge is represented as data rather than derived at render time, filtering could
  lean on it. Unknown — needs investigation.
- **Accept it for now.** Ship co-op first, treat competitive as a later milestone gated on
  this. Honest, and it keeps the working thing working.

**Recommendation: build co-op first on the current foundation, and investigate DW2's fog
representation in parallel** — that investigation decides whether competitive is a
milestone or a rewrite, and it costs nothing but reading code.

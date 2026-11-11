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

---

## M4b — command relay: BUILT, injection path NOT YET VERIFIED

`NetSession` now carries client → host commands using the game's own wire format:

- **Client** patches `GameClient.SendMessageToServer` and forwards any packet carrying
  `GameTask`s, serialised with `MessagePacket.WriteToStream`.
- **Host** deserialises with `MessagePacket.ReadFromStream` and injects into
  `GameServer.InputQueue` — the same `ConcurrentDictionary` the host's own client writes
  to in single-player.
- Injection deliberately does **not** use the main thread. The queue is concurrent and the
  server drains it, unlike state apply, which must be on the main thread.
- The client patch is a **postfix**, so the local call still runs and the client executes
  its own command immediately, with the host's next sync correcting it. That is
  client-side prediction, free here because client state is overwritten anyway.

### What is proven, and what is not

**Proven:** the relay installs (`command relay armed on GameClient.SendMessageToServer`),
the host side resolves `MessagePacket` and `InputQueue`, and the channel is open.

**Not proven: no command has yet crossed the wire.** Two reasons, found by measuring:

1. **`SendMessageToServer` is command-driven, not per-tick** — measured at **1 call in
   nine minutes** with nobody at the client's keyboard. Organic traffic cannot exercise
   the relay in an unattended run, which is why the counters were added: "no relay
   activity" was otherwise ambiguous between "never called" and "called with nothing".
2. A synthetic-packet path was added to close that gap (client sends an empty
   `MessagePacket` every 400 ticks under `DW2MP_RELAY_HEARTBEAT=1`). **The run that
   should have exercised it had the client fail to finish loading — 450 s, 0 cycles** — so
   the heartbeat, which is driven off client server-cycles, never fired.

**M4b needs one more run to verify.** The code path is complete; the evidence is not.

### Separate finding: pathfinding exceptions

Nine `IndexOutOfRangeException`s in a two-second window during the M4b run:

```
System.IndexOutOfRangeException
  at SystemPathTimeSet.GetPathTimesForSystem(Galaxy, Empire, int systemId, ...)
  at SystemPathTimeSet.GetPathTimeDelayed(...)
  at StellarObjectList.FindNearestCapitalByCorruptionReductionRatio(...)
```

**Absent from the successful M4 run**, so M4's result stands. Cause unknown; candidates
are a galaxy swap leaving derived path caches keyed to the previous galaxy's shape, or our
synthesised `FixedStep` clock confusing a `starDate`-based cache. Worth chasing before
either mode ships, because it points at derived state that a state-swap does not rebuild.

> **~~CAUSE FOUND, 2026-09-08 — neither candidate above was right.~~ That claim was
> WRONG and is struck the same day. Corrected below.**
>
> The wrong version said these pathing exceptions were the `Independent` pseudo-empire
> being promoted to the player empire, and that galaxy swapping had nothing to do with it.
> Two exception families were being conflated, and only one of them is `Independent`'s:
>
> | Family | Real cause |
> |---|---|
> | 17,619 × `NullReferenceException` in `DoTasksIndependentEmpire`, `GetScientistsAtResearchStations`, `CalculateResearchPointsMaximum` | `Independent` promoted to player. Fixed — see `lobby.md`. |
> | `IndexOutOfRangeException` in `SystemPathTimeSet.GetPathTimesForSystem` (67 that night, 518 in the 2026-09-08 two-instance run) | **The galaxy swap — the first candidate above, which was right.** |
>
> **What separates them, measured 2026-09-08.** A single instance playing a correctly
> chosen empire, with no state apply, ran 58,000 cycles and produced **zero** dumps. The
> two-instance run, where both sides perform a `StartGameExisting` galaxy swap at cycle
> 1600, produced **518** — 407 of them `Colony.CalculateCorruption` →
> `FindNearestCapitalByCorruptionReductionRatio` → `GetPathTimesForSystem`. The swap is
> the variable.
>
> So the original reading stands: **a state swap leaves derived path caches keyed to the
> previous galaxy's shape.** That matters much more than the `Independent` bug did,
> because the swap *is* the multiplayer mechanism — this is not a bug beside the design,
> it is a bug inside it. It is the next thing to fix.
>
> **Why the wrong conclusion was reached.** Both families were sitting in one directory of
> 17,686 dumps, the `Independent` bug explained the overwhelming majority of them, and the
> minority family was assumed to be more of the same rather than counted separately. The
> `at` frames were grouped, the *exception types* were not cross-referenced against them.
> A 99%-correct explanation absorbed the 1% that disproved it.
>
> **DW2 swallows all of these** — dump and continue — so a run looks healthy in our own log
> while emitting them. At ~17.7k accumulated dumps the game stopped launching altogether.

**Process note:** M4 was reported as working on the strength of our own log without
checking the game's own `SessionLog.txt`. That was the right result but an incomplete
check — the game's log should be read on every run from here.

---

## Decisions 2026-09-08

- **Co-op = one shared empire.** Both players issue orders to the same empire. No empire
  assignment, no fog concern, works on the architecture already proven. Fastest path to
  something playable.
- **Competitive stays gated on evidence** — investigate DW2's fog model before committing.
- **Target is internet play**, so `SteamNetworkingSockets` eventually replaces raw TCP
  (`Facepunch.Steamworks` is already loaded in-process).

## Fog investigation — RESULT: competitive is a milestone, not a rewrite

**DW2 has a real, serialised, per-empire knowledge model.** `Empire` owns it directly:

```
f  VisibilityMap            VisibilityMap
f  ExplorationMap           ExplorationMap
f  ResourceQuantityList     ResourcesKnown
f  PrioritizedTargetList    KnownFuelSources
f  EmpireList               _EmpiresWithSharedVisibility
```

`VisibilityMap` is a genuine knowledge store, **with its own `ReadFromStream` /
`WriteToStream`** — so it persists per empire rather than being derived at render time:

- `VisibilityStatus { Undefined, Unexplored, Explored, Visible }` per system
- Per-system arrays: `ColonyInvasion[]`, `InBattle[]`, `PlagueIdInSystem[]`,
  `PotentialColonyInSystem[]`, plus dictionaries for colony events and ruins
- Queries already exist: `CheckBaseKnown(Ship)`, `CalculateLocationVisibilityLevel(Location,
  Empire, out bool)`, `CalculateSystemVisibility(int, Galaxy, Empire)`

And `ShipVisibility` carries **`ApparentEmpireId`** and **`ApparentRole`** — the game
models what an observer *believes* a ship is, separately from what it actually is. That is
proper fog-of-war modelling, not a display filter.

### What this changes

**Correction to the earlier framing.** Competitive play was described as blocked by
information disclosure. That overstated it:

- **A competitive session would work and display correctly today.** Each client's UI
  filters through *its own* empire's `VisibilityMap`, so a player sees what their empire
  knows, not the whole galaxy.
- **The leak is a cheat-resistance problem, not a correctness one.** The unfiltered data
  sits in client memory, exploitable by someone reading process memory or writing their own
  mod — not by playing normally.

*(Strongly indicated by the API shape rather than proven: the render path has not been
traced. Worth confirming before relying on it.)*

**Hardening is a milestone.** Redacting before send means walking the galaxy and blanking
what the recipient does not know — and the predicates for "does empire E know about X"
already exist. The costs are real (a redacted copy per recipient per sync, or a custom
serialiser instead of `Galaxy.WriteToStream`) but this is ordinary work, not the
open-ended problem that closed lockstep.

**`_EmpiresWithSharedVisibility` is a gift for co-op.** The game already models empires
sharing visibility, which is exactly what allied co-op needs — should allied empires be
wanted later.

---

## M4b VERIFIED — commands cross the wire, 2026-09-08

```
client: sent synthetic command #1 tick=400 (39B)
host  : INJECTED command #1 serial=400 tasks=0 added=True
...     8 injected, serials 400 → 3200, every one added=True
```

Serial numbers match end to end and `GameServer.InputQueue.TryAdd` accepted every packet.
**The full bidirectional loop is proven** between two live DW2 instances: host → client
full-state sync, and client → host command relay, using the game's own `MessagePacket`
format in both directions.

The relayed packets carry `tasks=0` because they are synthetic — nobody is at the client's
keyboard. A real player's orders travel this exact path with tasks attached; the transport,
serialisation and injection are what was unproven, and they are now proven.

### What made this run work

Both instances loading simultaneously starved the client — 450 s with 0 cycles in the
previous attempt. Launching the client only **after the host is confirmed ticking** fixed
it. Worth remembering for any future two-instance test on one machine.

### Session handshake

`Hello` now carries protocol version, game version and mode, and **both ends check** —
the client answers the host's Hello with its own descriptor, because a one-sided check
only warns the person who did not choose the session.

Game version matters as much as protocol version: state transfer is DW2's own serialised
galaxy, so two players on different builds would exchange bytes that deserialise into
nonsense or throw somewhere deep and unhelpful. `ProtocolVersion` is bumped whenever the
wire format changes.

---

## Session-driven galaxy generation — VERIFIED 2026-09-08

**Both players pick their empire in the lobby and the host builds a galaxy containing
exactly those empires.** This closes the last gap between "two games share a universe"
and "two people play their own empires in a universe they agreed on".

What the host does, in order:

1. Reads the agreed `session.json` (mode, star count, AI count, seed, one `EmpireConfig`
   per slot).
2. Adds one `GameStartSettingsEmpire` per human slot — name, race, government,
   `AllowAnyGovernment`, `IsPlayer` — competitive adds every slot, co-op adds slot 0 only,
   since both players drive one empire there.
3. Sets `EmpireAutoGenerationState = SpecifiedAndAutoGenerated (3)`.
4. Calls `DWGame.StartGameNew(settings)`.

Step 3 is the one that was missing, and without it steps 1–2 were writes into a list DW2
never read. It is a **flags** enum (`SpecifiedEmpiresOnly=0, AutoGenerated=1,
SpecifiedEmpires=2, SpecifiedAndAutoGenerated=3`) read via `DataHelpers.GetFlag`; it
defaults to `AutoGenerated`, so the "use my empire list" bit was never set. Verified
output and the accompanying `Independent`-promotion fix: `lobby.md`.

**A retraction goes with it.** This document previously recorded that `IsPlayer` set on a
`GameStartSettingsEmpire` does not survive `Galaxy.Generate`. It does. What did not
survive was the entire `Empires` list, for the reason above. `EnsurePlayerEmpire` was
built to work around an unset flag; it is retained only for the solo/no-session path.

### Still not done

- **Per-empire state filtering.** Competitive still ships the whole galaxy to the client,
  so both players can read each other's empire out of the synced state. The fog data
  exists (`Empire.VisibilityMap`, `ExplorationMap`, `ShipVisibility.ApparentEmpireId`);
  filtering it is its own milestone.
- **The client is still handed the host's galaxy wholesale**, so slot 1's empire exists
  because the host generated it, not because the client contributed anything at
  generation time. That is correct for host-authoritative and worth stating plainly.
- **No cross-machine run yet.** Everything above is two processes on one PC.

---

## Two instances, session-driven, each playing its own empire — 2026-09-08

First run of the whole chain: lobby session → host generates the agreed galaxy → client
joins → each side drives its own empire. Host slot 0, client slot 1, relay between them.

**What worked.**

```
HOST    # player: slot 0 drives 'Terran Union' (GetPlayer patched)
CLIENT  # player: slot 1 drives 'Ackdarian Compact' (GetPlayer patched)
CLIENT  # player: GetPlayer -> 'Ackdarian Compact' at index 3 (was 'Terran Union')
CLIENT  # net: handshake OK — protocol and game version match
CLIENT  # net[client]: state received packed=4,364,374B raw=24,108,073B
```

Per-side perspective holds in a real two-process run, and the host's state crosses the
relay at the expected size. Both sides generated their configured empires.

**Three defects, in priority order.**

1. **The client's real apply failed: `no DWGame captured yet`.** The host sent sync #1 at
   tick 1200 before the client had a `DWGame` to adopt into, so the frame was queued and
   then dropped. The client spent the rest of the run in its *own* throwaway galaxy —
   which is not the host's: 10 empires against the host's 11, and different auto-generated
   names from the same seed 4242. Two people in two universes, which is the one thing this
   whole design exists to prevent. **This is the next thing to fix**: the client must not
   discard a state frame that arrives before it is ready, and the host should not sync
   until the client says it can adopt.

2. **The self-apply measurement runs during a live session.** `Determinism.cs` installs
   `ApplyState` when `NetSession.Active`, and `ApplyState` then performs its own M3b
   capture-and-reapply at cycle 1600 — on *both* sides. That is a galaxy swap nobody
   asked for in the middle of a networked run, and it is what produced the 518 pathing
   exceptions. The measurement and the transport need separating.

3. **Same seed, different galaxies.** Host and client generated 11 and 10 empires from
   seed 4242. Harmless today because the client's galaxy is meant to be overwritten — but
   it means the seed is *not* by itself a shared input, so nothing should ever be assumed
   to match across the two without a sync.

**The relay stopped at 3 frames** with `game=False` on both ends afterwards, which is
consistent with the games dropping their local sockets after the failed apply. Worth
re-checking once (1) is fixed rather than chasing separately.

### Both defects fixed, re-run 2026-09-08 — the client adopts the host's universe

```
CLIENT  # net[client]: state received ... (queued)              x4, before the game existed
CLIENT  # net[client]: state arrived before the game was ready — holding it
CLIENT  # makesave: player empire already set by generation — 'Ackdarian Compact' (slot 1)
CLIENT  # net[client]: APPLIED sync #1  24,042,942B  read=159ms  apply=9ms  ships=54 empires=10
```

**Crash dumps went 523 → 1**, and the one that remains is a UI popup
(`EmpireMessageDialog.BindData` via `ShowInvestigationDecisionMessage`), not the
simulation — a message dialog whose subject was swapped out from under it.

That 523 → 1 is also the **experimental confirmation of the corrected diagnosis above**:
the only change touching crashes was removing the unrequested self-apply, and the pathing
family vanished with it. The swap causes them; `Independent` never did.

| Defect | Fix |
|---|---|
| Client dropped state that arrived before it had a `DWGame` | `ApplyState.CanApply` distinguishes "path resolved" from "there is a game to adopt into"; the client now **holds** the frame instead of dropping it |
| M3b self-apply ran during live sessions | Gated on `DW2MP_MEASURE_APPLY` alone. `ApplyState` is still installed when `NetSession` is active — the transport needs `ApplyBytes` — but the measurement no longer fires |

**Still open, and the top item is now client throughput.** The client managed 2 server
cycles to the host's 8 and applied only sync #1 while later frames queued behind it. Two
DW2 instances on one machine is part of it, but a post-apply stall has not been ruled out
and the relay showed the client's traffic flat-lining afterwards. Measure before assuming
it is only contention.

**Same seed, different galaxies** remains true and remains harmless: the host generated 10
empires and the client 10 with different auto-generated names, and the client's copy is
overwritten by the sync. It does mean the seed alone is not a shared input.

---

## The two blockers, worked 2026-09-08

Both items from the previous section turned out to be misdiagnosed by their symptoms, and
both are now measured rather than guessed.

### Blocker 1 — "client throughput" was a HOST DEADLOCK and a CLIENT CRASH

Neither half was contention.

**The host froze in a socket write.** It stopped at exactly tick 15,600 in one run and
13,200 in another — both exact multiples of the 1,200-tick sync interval, which is the
tell: a clock stall stops anywhere, a sync stall stops on the boundary. `Send()` was
called inline from the simulation thread; when the client stopped reading, the relay's
buffer filled, the relay stopped reading from the host, and a ~4.4 MB socket write blocked
forever **with the simulation thread inside it**.

The watchdog now diagnoses a stall instead of printing a flat number, and its first run
disproved the theory it was written for:

```
179s: 15600 cycle(s) — STALLED. IsRunning=False, AutoPaused=False, GetGamePaused=True
      resumed the simulation
209s: 15600 cycle(s) — STALLED. IsRunning=True,  AutoPaused=False, GetGamePaused=False
```

Unpausing worked and the count still did not move, because the thread was not paused — it
was blocked in a syscall.

**Fix: the host sends from its own thread**, through a one-slot outbox where the newest
state supersedes the old (an older full state has no value once a newer exists — the same
reasoning the client already applied to its inbound backlog). Result: 24,000 cycles and 20
syncs with no freeze, and the outbox reported `superseded 1, 9 total` — nine stale states
dropped rather than nine host stalls. **A host must survive any client behaviour**, so
this is correctness, not throughput.

**The client was DYING, not hanging.** Process sampling settled in one run what its own
log could not: `CLIENT gone` at ~45 s. A dead process stops writing watchdog lines, which
reads exactly like a hang.

The killer was the crash dump previously dismissed as "a UI popup, not the simulation":
`EmpireMessageDialog.BindData` throws `NullReferenceException` when a message refers to
objects the swap discarded, and the chain `DWGame.Update → MessageListView.Update → …`
means **nothing catches it**. Dismissing it as cosmetic was wrong — it was the whole
failure.

Fixes: `DWGame._ShouldRegenerateMessageLog` is set on adoption so DW2 rebuilds the message
list from the galaxy it now has; and the message-UI subtree is guarded by Harmony
finalizers. Guarding only `BindData` was tried first and merely **moved** the crash one
frame up into `SetEmpireMessageDialogData`, so the guard now sits at the top of the subtree
with the inner methods as belt and braces.

The client went from 1 cycle and 1 apply to **sync #2 applied and cycle 2000 reached**.

### Blocker 2 — the path cache, fixed and A/B proven

`SystemPathTimeSet._PathTimesPerSystem` is a raw `SortedList[]` indexed directly by
`systemId`, and `GetPathTimesForSystem` does `_PathTimesPerSystem[systemId]` with **no
bounds check** — it validates that the id is non-negative and that the system exists in
`galaxy.Systems`, then indexes an array that may be shorter than either. Adopting a galaxy
is exactly the case where the cache and the systems stop agreeing.

`SystemPathTimeSet.Clear(Galaxy)` is the game's own fix: allocate
`galaxy.Systems.GetNextIdRaw()` entries and `Interlocked.Exchange` them in. It is now
called on every adoption, along with resetting `SystemPathTimeExpiry`.

**Proven by A/B**, same build, one environment variable apart (`DW2MP_RESET_PATH_CACHE`,
which exists so the fix can be tested against itself):

| Arm | `IndexOutOfRangeException` |
|---|---:|
| Reset enabled | **0** |
| Reset disabled | **245** |

The control arm also stopped logging entirely after the apply — the crash storm was
hanging the process, not merely filling a directory.

### What this uncovered: the path cache was the FIRST of several

With the swap no longer crashing on paths and the client no longer dying on messages, the
next layer is visible — 355 `NullReferenceException`s in a run, on a different axis:

```
354  Empire.DoTasks
297  Empire.IdentifyRefuellingPointsPerSystem -> IdentifyRefuellingPointBasesWeCanDockAt
192  Empire.DoTasksPirateEmpire_PreExpansion
 65  Empire.DoTasksIndependentEmpire
```

These are **per-empire derived caches** that a swap leaves inconsistent — `Empire` also
carries `RepairBasesPerSystem` and `RepairSystems`, which are the same shape of problem.
The general statement, and it is the thing to design around rather than patch case by
case: **`StartGameExisting` replaces the galaxy but does not invalidate state derived from
it.** Every such cache is a crash waiting for the first adoption.

Note these are pirate and independent empires, not the players' — so they are not the
`Independent`-promotion bug returning. They are the ordinary AI empires of an adopted
galaxy, doing ordinary work against caches that describe a galaxy that is gone.

---

## Derived-state invalidation on adoption — fixed 2026-09-08

**Crash dumps across a two-instance run: 355 → 106 → 1.** The client went from dying at
45 s having applied one sync, to applying **26 syncs and running 37,119 cycles**.

### The method that mattered: read the game's own load path

The first instinct was to enumerate stale caches. That would not have finished — `Empire`
alone carries `RefuellingPointsPerSystem`, `RefuellingPointsPerSystemMilitary`,
`RefuellingSystems`, `RefuellingSystemsMilitary`, `RepairBasesPerSystem`, `RepairSystems`,
`ConstructionBasesPerSystem` and `_PathFindingDangerousSystems`, and two of the three real
fixes are not cache fields at all.

DW2 deserialises galaxies constantly — every save load — so the question worth asking was
**what does the game do that we don't**:

| Step | `BeginLoadGame` / `LoadGame` | `StartGameExisting` | Ours, before |
|---|---|---|---|
| `Galaxy.WaitAllTasksCompleted()` | ✅ | ❌ | ❌ |
| `DWGame.ClearMessageQueues()` | ✅ | ❌ | ❌ |
| `PathFindingSystem.CalculateSystemDistances(galaxy)` | ✅ | ❌ | ❌ |
| `Resource.ClearCachedValues()` | — | ✅ | ✅ |
| `CheckInitializeOrBindSinglePlayerGame` | ✅ | ✅ | ✅ |

Adoption was doing the bind and none of the preparation.

**`WaitAllTasksCompleted` is the load-bearing one.** DW2 runs galaxy work across many
threads; replacing the galaxy mid-flight leaves workers walking half-old, half-new
structures. That matches the evidence in a way no single null field does — the failures
were scattered across research, refuelling, corruption and pirate code, and were transient
rather than every-call. It also explains the `PauseGame(true)` inside `StartGameExisting`:
the game expects to be quiesced around this, and we were calling it on a live simulation.

### The one genuine missing rebuild: `Ship.Summary`

`ShipSummary` is derived and `ReadFromStream` does not restore it, so ships arrive with
`Summary` null. `IdentifyRefuellingPointBasesWeCanDockAt` then does
`ship.Summary.DockingBayCount` with no null check.

`Ship.RegenerateSummary()` is the game's own rebuild, and only null summaries are touched
so the count is evidence rather than a blanket action:

```
# apply: rebuilt 64 of 64 ship summaries (null after deserialisation)
```

**64 of 64** — the field is simply not serialised.

This one change took the run from 106 dumps to 1, and it removed the *research* family too
(`CalculateResearchFundingAvailableAndActualAmounts` → `GetScientistsAtResearchStations`)
even though nothing research-specific was touched. Research funding walks scientists at
research **stations**, which are ships, so both families shared the same root.

### The general statement

**`StartGameExisting` replaces the galaxy but neither quiesces the threads using it nor
rebuilds state derived from it.** Any future adoption bug should be checked against DW2's
own load sequence first.

`DW2MP_ADOPT_FIXUP=0` disables the whole fixup so it can be A/B tested against itself, the
same way `DW2MP_RESET_PATH_CACHE=0` does for the path cache.

### NEXT: the client leaks memory and dies at ~215 s

The remaining blocker, and it is not a crash. Client private bytes climb steadily across
the run — 3.6 GB → 8.4 GB over 26 adoptions — and the process is killed at ~215 s. Growth
is far more than our own buffers can account for (26 × 24 MB ≈ 0.6 GB), so **old galaxies
are being retained**. `Galaxy.CopyGalaxyInstanceToStaticBaseData` is the first place to
look: there is static state holding galaxy data, and every adoption creates another galaxy.

---

## The client's memory growth — measured 2026-09-08, and it is NOT a leak in the assumed sense

The previous section closed by predicting a managed leak, with old galaxies retained via
`CopyGalaxyInstanceToStaticBaseData`. **That prediction was wrong.** What follows is what
the instrumentation actually showed.

Every adoption now logs three numbers, chosen because they separate three different causes:

| Number | Meaning if it grows |
|---|---|
| `managed` | GC heap — objects RETAINED |
| `afterGC` | managed after a forced blocking collect — a real leak, not just uncollected garbage |
| `private` | whole process — native memory, or Large Object Heap fragmentation |

```
managed=740MB private=8,293MB afterGC=603MB afterLOHCompact=8,162MB
managed=725MB private=8,413MB afterGC=604MB afterLOHCompact=8,341MB
managed=727MB private=8,656MB afterGC=605MB afterLOHCompact=8,576MB
```

**`afterGC` is flat to within 2 MB across the whole run.** Nothing is retained. Three
hypotheses were tested and all three are eliminated:

| Hypothesis | Test | Result |
|---|---|---|
| Retained galaxies (managed) | forced blocking collect each checkpoint | ❌ `afterGC` flat at 603–605 MB |
| LOH fragmentation from 24 MB state buffers | `LargeObjectHeapCompactionMode.CompactOnce` | ❌ reclaims ~80–100 MB, floor still rises |
| Facility textures reloaded per adoption | patched `LoadImagesForFacilities` to load once | ❌ growth unchanged, and it caused 6 crashes — reverted |

### What it actually is

**Native memory, ~25–36 MB per adoption, and adoption-driven rather than time-driven.**
Established by halving the sync rate:

| Sync interval | Adoptions in 210 s | Growth |
|---:|---:|---:|
| 1,200 ticks | ~30 | ~800 MB |
| 2,400 ticks | ~15 | ~420 MB |

Halving the adoptions halved the growth. DW2 was never built to swap galaxies repeatedly:
`StartGameExisting` acquires native resources for the incoming galaxy without releasing
the outgoing one's, and nothing in the game's own lifecycle ever asks it to.

(2026-11-11: identified. It is not `StartGameExisting` itself but the renderer's
re-initialisation one frame later; see "Adoption memory: what leaks, measured" at the end.)

### What was done, and what it does not do

The default sync interval is raised from 600 to 1,800 ticks. **That is a stopgap that
lowers the rate, not a fix**, and the comment at `NetSession._syncEveryTicks` says so.

The real answer is architectural and was always the plan: **full-state adoption should
bootstrap a join and repair a divergence, not carry the steady state.** Commands should
carry everything in between — which is exactly what the M4b command relay exists for. At
~30 MB per adoption, any design that adopts on a timer leaks by construction, however
often the timer fires.

## Unhandled task exceptions no longer kill the client

Separately, and this is what was actually killing the client at ~180 s: DW2 runs
Empire/Ship/Orb/Colony/Fleet tasks across worker threads and guards MOST of them itself —
which is why hundreds of `NullReferenceException`s were survivable, each writing a dump and
continuing. The paths it does not guard take the process with them, because an unhandled
exception on a .NET background thread terminates the process.

Harmony finalizers now guard those six task entry points. Caught live:

```
# apply: SURVIVED a task failure in Ship.DoTasks — NullReferenceException
  at Design.CalculateAdvancedTechScore (distinct #1, 3 total)
```

That is the identical exception that killed the client in the previous run. It fired three
times and the client survived all three, finishing the run.

**Swallowing is defensible here specifically because of host-authoritative design**: the
host owns the truth and re-syncs the whole galaxy, so a skipped tick for one ship on the
client is corrected by the next sync. It would NOT be defensible on the host, and it would
not be defensible in peer-to-peer lockstep, where a skipped tick is a divergence.

It is also not a licence to stop fixing roots. Every **distinct** failure — method,
exception type and throwing frame — is logged once with a running count, so a new root
cause is visible the first time it happens instead of being absorbed silently.
`Design.CalculateAdvancedTechScore` is the one known outstanding root.

---

## Commands carry the steady state — 2026-09-08

**Full galaxy state is no longer sent on a timer.** Commands carry what players do; a full
state bootstraps a join and repairs a divergence, and nothing else.

### The protocol now

| Message | Direction | Size | When |
|---|---|---|---|
| `Command` | client → host | ~39 B | continuously, as the player acts |
| `StateSummary` | host → client | **25 B** | every 300 ticks |
| `ResyncRequest` | client → host | 0 B | on join, or on sustained structural divergence |
| `FullState` | host → client | **4.37 MB** | only in answer to a request, plus a long safety net |

A summary is ~175,000× smaller than a full state.

### Measured, one 240-second two-instance run

| | Result |
|---|---|
| Commands sent → injected | **100 → 100** — every one crossed and was accepted into the host's `InputQueue` |
| Full states | 12, **all on request**; none on a timer |
| Client memory across the run | **+67 MB**, against +400–800 MB under timer syncing |
| Crash dumps | **0** |

### Why the fingerprint is structural, and why that decision is the design

`ApplyState.StructuralFingerprint` hashes which ships exist (id, plus destroyed folded into
the key) and the sizes of the big collections. It deliberately excludes positions, health
and every other continuous value.

That is not an optimisation, it is the policy. **DW2 is not deterministic**, and the client
runs its own simulation between corrections, so a fingerprint including positions would
mismatch within a tick or two and demand a resync every time — the fixed timer again,
wearing a smarter hat. What we want to notice is a ship built or destroyed on one side and
not the other, because that is a difference no amount of local simulation will repair.

A run of `MismatchesBeforeResync` (3) summaries must disagree before asking. The two sides
sample at different instants, so a single mismatch is often just the seam between two
clocks resolving itself.

### The honest limit: the client still asks about every 20 seconds

The client diverges structurally within a few summaries and then requests a correction as
often as the rate floor allows. **That is real divergence, not a false positive** — it runs
its own non-deterministic simulation and builds and loses different ships from the host.

So the effective correction rate is now set by `MinTimeBetweenResyncRequests` (20 s) rather
than by a tick interval. That is a better place for it — it is a bound on cost rather than a
guess at a period, and it degrades gracefully: a client that happens to agree pays nothing.

The tension worth stating plainly: **the client simulates for visual continuity between
corrections, and simulating is exactly what makes it diverge.** Stopping the client's
simulation would make divergence impossible by construction, but then its world would be
frozen between corrections, so it would need MORE state, not less. The way out is delta
updates rather than full galaxies — which is a much larger piece of work, and is the next
architectural step whenever adoption cost matters again.

---

## The client stops simulating, and deltas move its world — 2026-09-08

Two changes that only work together.

**The client no longer simulates.** `ServerCyclePrefix` returns false on the client, so
DW2's simulation step never runs there. A host-authoritative client has authority over
nothing, and simulating was precisely what made it diverge: it built and lost different
ships from the host, so its structure disagreed within a few summaries and it demanded a
4.4 MB correction every 20 seconds. (`DW2MP_CLIENT_SIMULATES=1` restores the old behaviour.)

**Ship deltas move it instead.** `StateDelta` sends position, hull damage and destroyed-ness
per ship, only for ships that actually moved, and the client applies them **in place** —
no `StartGameExisting`, so no adoption, so none of the ~30 MB of unreleasable native memory
an adoption costs.

### Measured, one 240-second two-instance run

| | Timer syncing | Commands + summaries | **+ deltas, client not simulating** |
|---|---:|---:|---:|
| Full-state adoptions | ~30 | 13 | **5** |
| Delta traffic | — | — | **1,401 deltas, 283 KB total** |
| Client memory | +400–800 MB | +67 MB | **flat** (6.54 → 6.66 GB, no trend) |

A representative delta: `91/92 ships, 1,923B`. A full state is 4.33 MB — deltas carry the
motion at roughly **1%** of the cost, and adoptions dropped six-fold.

### The bug that made the first attempt do nothing

The first version rejected any delta whose ship count disagreed with the host's, on the
reasoning that a mismatch meant the client's structure had drifted. That was right while
the client still simulated, and became wrong the moment it stopped: **a client that does
not simulate can never create a ship**, so the instant the host built one the counts
diverged permanently and every delta was rejected. Deltas moved nothing for an entire run
while looking like they were working — 1,400 sent, 0 applied.

A delta now moves ships both sides know about and silently skips ids the client has not
seen. Those arrive with the next full state, so the client is briefly behind on what
*exists* while everything it can see keeps moving. That is the right trade: structure
changes rarely, position changes constantly, and only the first needs a full state.

With deltas carrying motion, `MinTimeBetweenResyncRequests` rose 20 s → 60 s. A full state
is now purely a structure refresh.

### One hypothesis disproved, recorded so nobody re-tries it

`Design.CalculateAdvancedTechScore` is still the one root cause escaping into the task
guards, and it reads `Empire.Policy.ComponentSelectionFactors` — which looked like the same
shape of problem as `Ship.Summary`: derived state `ReadFromStream` does not restore. A probe
at adoption reports:

```
# apply: 0 of 10 empires have a null Policy after adoption
```

**Policy is not the null.** It is somewhere deeper in that method — the component or
design-template lookups are the remaining candidates. `ConstructionSystem.DoTasks` was added
to the task guards on evidence, being the path that carried this root to the client.

---

## CalculateAdvancedTechScore — root cause found and fixed, 2026-09-08

The last root cause escaping the task guards. **Two consecutive 240-second two-instance
runs now produce 0 crash dumps and 0 guard hits**, where every previous run produced one to
three.

### What it is

`Design.CalculateAdvancedTechScore` reads, on one line:

```
newobj   Component::.ctor(Int16, Byte)     // built from (componentId, researched level)
callvirt Component::get_Definition()
ldfld    ComponentDefinition.Category      // <-- no null check
```

and `Component.Definition` is:

```csharp
if (_wrDefinition.TryGetTarget(out d) && d.ComponentId == ComponentId) return d;
d = Galaxy.ComponentsStatic.GetByComponentId(ComponentId);   // can return null
_wrDefinition = new Weak<ComponentDefinition>(d);
return d;
```

A **weak reference cached over a mutable, process-wide static table**. On a weak-reference
miss it re-resolves from `Galaxy.ComponentsStatic`.

`ShipComponent.GetComponentLevel` null-checks `Definition` twice on adjacent lines.
`CalculateAdvancedTechScore` does not — and `Empire`, `Empire.Research`,
`ResearchedComponents` and its own `GetHighestComponent` result are all null-checked either
side of it. It is one missing check in otherwise careful code.

### Why adoption triggers it, and why it is a race

`Galaxy.ReadFromStream` calls `CopyGalaxyInstanceToStaticBaseData`, which **rewrites the
static definition tables**. DW2 only ever does this at load time, when nothing else is
running. We do it on a live process, so a worker thread resolving a component mid-rewrite
gets a null.

That it is a race and not corrupt data is what two failed hypotheses established:

| Hypothesis | Probe | Result |
|---|---|---|
| `Empire.Policy` is null after adoption | count null policies at adoption | ❌ **0 of 10** |
| A ship's component id does not resolve | resolve every ship component id | ❌ **all 77 resolve** |
| A *design's* component id does not resolve | extend the probe to `Galaxy.Designs` | ❌ **all 83–98 resolve** |

Every id always resolved, while the crash kept happening once or twice a run. Persistent
data problems do not behave like that; a race does.

The second probe is worth its own note: ships alone reported a clean bill of health because
the crash arrives through `Ship.ShouldRetrofitTechScore(Empire, Design)`, which passes the
**design's** component list. A retrofit is evaluated against a design the ship does not yet
carry, so the ids that mattered were the ones no ship was using. Reading the stack properly
was worth more than another probe.

### The fix

**Quiesce before deserialising, not just before the swap.** `QuiesceBeforeSwap()` moved to
the top of `ApplyBytes`, ahead of `ReadFromStream`. It was already being called — but after
the statics had been rewritten, which is the wrong side of the thing it protects.

The task guards stay as the backstop. This is a missing null check in code we cannot edit,
reachable whenever the static tables are swapped on a live process; shrinking the window to
near zero is the fix available to us, and the guard is what makes the residual harmless.

### State of the run

```
0 crash dumps, 0 guard hits (two consecutive runs)
5 full states, 1,501 deltas, 383 KB of delta traffic
client memory flat, both processes alive for the full 240 s
```

---

## Deltas extended to colonies and research — 2026-09-08

Deltas now carry three sections instead of one:

| Section | Fields | What it is |
|---|---|---|
| ships | position, hull damage, destroyed | what moves on screen |
| colonies | corruption, approval, quality boost, max population | what changes on the map |
| research | per-project progress and researched flag | what changes in the UI |

Each field has its own change threshold, and only records that crossed it are sent, so a
quiet galaxy still costs nothing.

```
# net[client]: delta applied — 15 ship(s) +6 absent, 1 colony, 6 project(s), 461B
```

### Measured, 240-second two-instance run

| | Ships only | **+ colonies and research** |
|---|---:|---:|
| Full-state adoptions | 5 | **4** |
| Delta traffic | 383 KB | **564 KB** |
| Crash dumps | 0 | **0** |
| Client memory | flat | **flat** |

Roughly 180 KB buys colony and research state for the whole run — against 4.33 MB for a
single full state.

### Two bugs found on the way, both worth recording

**`Empire.EmpireId` is `Int16`, not `Int32`.** The pattern `is not int empireId` therefore
failed for *every* empire, and the research section came out empty in every delta — silently,
because an empty section is indistinguishable from a quiet one. Same class of mistake as
reading `Empire.Name` as a field when it is a property: reflection type assumptions fail
quietly and produce a plausible-looking nothing. Colonies worked immediately, which made the
gap easy to see only because they were being tested side by side.

**`ResetBaseline` after a full state was re-sending the entire galaxy.** Clearing the
baseline means every record counts as changed, so the first delta after each full state was a
complete dump: 9,270 research projects and 103 KB, five times a run — about half of all delta
traffic, all of it re-sending what the client had just received in the state itself.

`PrimeBaseline` records the galaxy as it is at serialisation time without emitting anything.
The client is about to adopt exactly that galaxy, so nothing in it has changed as far as that
client is concerned. Priming at serialisation time rather than adoption time is the correct
choice: deltas then describe changes since the snapshot the client is starting from.

**Traffic halved: 1,054 KB → 564 KB.**

### What is deliberately still absent

`Colony.Population` is a per-race `PopulationList`, not a number. Carrying it means carrying
a collection whose members appear and disappear — which is structure, and structure is what
full states are for. The same reasoning excludes fleets, characters and diplomacy: each is a
collection rather than a set of scalars on an object that already exists on both sides.

---

## Creation and removal, client-side — 2026-09-08

Deltas now carry **structure** as well as change, for ships and colonies.

### The objection was real and the answer was to look harder

This was documented as out of scope on the grounds that constructing a DW2 `Ship` from
outside would mean reproducing a constructor we cannot read, and getting it subtly wrong
would corrupt the client's galaxy silently. That reasoning was sound. It was also beside
the point, because **the game already knows how**:

```
Ship.WriteToStream(BinaryWriter)
Ship.ReadFromStream(Galaxy, BinaryReader)
```

and `ShipList.ReadFromStream` — DW2's own save loader — does exactly:

```csharp
var ship = new Ship(galaxy);
ship = ship.ReadFromStream(galaxy, reader);
list.Add(ship);
```

`Colony` exposes the identical pair and its list deserialiser the identical idiom. So a
created object is built by DW2's constructor and populated by DW2's field reading; nothing
here knows what a Ship contains. **The limitation was in the approach, not the game.**

### What it fixes

Before, the client could never gain an object, so its sets drifted from the host's
indefinitely and every update naming an unknown id was skipped forever:

```
have 38 of host's 46 ships
have 72 of host's 78 ships
```

After, across a 240-second run:

| Ship-count gap | Samples |
|---:|---:|
| **1** | 12 |
| 2–4 | 8 |
| 5–8 | 5 |

A gap of 1 is a ship built between the host's snapshot and the client's apply — inherent
latency, not drift. The run ends at 97 of 98.

### Measured

| | Update-only | **+ creation and removal** |
|---|---:|---:|
| Full-state adoptions | 5 | **5** |
| Delta traffic | 564 KB | **523 KB** |
| Crash dumps | 0 | **0** |
| Guard hits | 0 | **0** |
| Client memory | flat | **flat** |

Traffic went *down*: an added ship costs its serialised bytes once, where before the client
never got it at all and the host re-sent an update for it on every delta forever.

### One detail that would have been a silent corruption

`RegenerateSummary()` is called on every created object that has one. `ShipSummary` is
derived and **not** serialised — established earlier when adopting a full state left 64 of
64 ships with a null `Summary`. A per-object deserialise is the same code path, so a newly
created ship would have arrived with the same hole and crashed the first thing that read
it. Knowing that from the earlier work is what made this safe first time.

### Still not carried, and why

- **Research projects** are created and removed with an empire rather than during play, so
  they stay update-only.
- **Anything behind a nested collection** — `Colony.Population` is per-race, plus fleets,
  characters, diplomacy — is left to full states. Those are collections whose *members*
  come and go, which is the same problem one level down.

Full states remain the repair path when a delta does not fit.

*(2026-11-10: per-race `Colony.Population` and diplomacy are carried now, and full states
arrive as keyframes; see "Desync reduction: measured, then built" at the end of this file.
Fleet and character CONTENTS are still left to full states.)*

---

## Fleets and characters — 2026-09-08, and the refresh half was measured out again

Deltas now carry **fleet and character membership**: created and removed client-side, using
DW2's own per-object serialisation like ships and colonies.

### The exclusion was half right

Both were excluded as "reached through a nested collection" — a fleet's membership, a
character's traits — which a field-level diff cannot express. That part was correct. What it
missed is that the nested collection is not a problem if you never touch it: serialising the
whole object hands it to DW2's writer, exactly as for ships.

Two details the ship path did not cover:

- **`CharacterList` uses `new Character()`, not `new Character(galaxy)`.** `ShipList`,
  `ColonyList` and `FleetList` all take the galaxy; `CharacterList` does not. The resolver
  looked only for the `(Galaxy)` form, so it returned a null constructor for `Character` and
  would have failed **silently** — the object simply never created, reported absent forever.
  It now falls back to the parameterless form.
- **`ReadFromStream` returns `this`.** `Ship` and `Character` both end `ldarg.0; ret`, so it
  populates the instance it is *called on*. That makes refresh-in-place possible: an object
  the client already has can be repopulated from the host's bytes without being replaced,
  preserving identity and list order. Remove-and-re-add would have worked and would have
  broken every reference another object held to the old instance, for no gain.

### Then the refresh half was measured, and turned off

Refreshing contents was built, and cost this, over three 240-second runs against a **523 KB**
baseline:

| Policy | Delta traffic |
|---|---:|
| Refresh on every change | **32.4 MB** — more than the four full states it replaces |
| Refresh every 10th delta | **5.4 MB** — four characters eating ~90% of the budget |
| **Membership only** | **546 KB** ✅ |

The cause is that the change detector is a **byte hash** — exact, and therefore unable to
tell "this character's allegiance flipped" from "a timestamp advanced". `Character` carries
`DateArrivedAtLocation`, `LastBattleDate`, `GhostCountdown` and a growing `GameEvents` list,
so every character was changed on every delta at ~3.5 KB each.

At every-10th, the refresh interval is 30 s against a full state every 60 s — **twice as
fresh for ten times the traffic, to keep four objects current.** That is a bad exchange rate,
and the number decided it rather than the taste.

What survives is the part deltas are actually needed for. **Set membership is
irrecoverable**: an object the client does not have is one it can never be told about again,
because every update naming it is skipped as absent. Contents are merely stale, and the next
full state fixes them. `RefreshEvery` is one constant and the machinery is intact, so a
client-side feature that ever needs fresher internals turns it back on.

### Honest limits

- **The fleet path is implemented but UNEXERCISED.** The census added for exactly this
  reason says `Ships=45 Colonies=3 Fleets=0 Characters=5 Empires=11` — this early-game
  galaxy has no fleets at all, so nothing has ever run through that half. "0 fleets were
  sent" and "the section is broken" look identical in a delta log, which is why the census
  exists.
- **Delta build now costs avg 5.3 ms, worst 29.8 ms** on the simulation thread. This is
  newly *visible* rather than newly introduced — the timing was added in the same change.
  The likely bulk is the research section walking ~10,300 projects every delta, which
  predates fleets and characters. Worth measuring properly before it matters.

---

## The research walk — 2026-09-09

The delta build cost avg 4.6 ms on the simulation thread, and the previous note *guessed*
research. Per-section timing settled it first:

```
research=3.97ms  ships=0.24ms  colonies=0.20ms  characters=0.08ms  fleets=0.07ms
```

**87% of the build, for ~3% of the records sent.** ~10,300 projects walked every delta, each
costing one indexer `Invoke` plus three `FieldInfo.GetValue` — roughly 41,000 reflection
calls, three times a second.

### Two fixes, and the order matters

**1. Compiled accessors (`FastAccess.cs`) — 3.97 → 1.96 ms.** `FieldInfo.GetValue` boxes and
does access checks per call; an expression-compiled `Func<object,float>` is a direct field
load. Cached per (type, member) and compiled once.

The alternative was to walk *less*: `ResearchSystem` also exposes `ResearchQueue` and
`NextProjects`, and walking those would have been faster still. It would also have been a
**guess** — a project changed by anything outside the queue would be missed silently, and an
under-report is indistinguishable from "nothing changed". Compiling keeps the walk
exhaustive and removes the cost instead of moving it, so there is no correctness question to
get wrong.

**2. Cadence, every 5th delta (~15 s) — 1.96 → 0.46 ms.** Safe here in a way it explicitly
is **not** for fleet and character membership, and the difference is the whole reason both
decisions can coexist:

| | Delayed update costs | Delayed creation costs |
|---|---|---|
| research (update-only) | staleness, repaired by the next delta | — nothing is ever created |
| fleets / characters | staleness | **irrecoverable** — every later update naming an unknown id is skipped as absent forever |

Research projects are created with their empire and already exist on the client. Membership
is the thing that cannot wait; contents can.

**Net: 3.97 → 0.46 ms, and the whole build 4.6 → 0.8 ms.**

### A blind spot built out of two cadences

The first run after the cadence change reported **zero** research deltas reaching the client
— while the host reported the section running. Neither was wrong. The client logs every 50th
delta and research runs every 5th, and `50k+1 mod 5` is **always 1**, so every sampled delta
was guaranteed to be a non-research one. The section looked dead while working perfectly.

Two cadences sharing a factor is an easy way to build a permanent blind spot into a sampler,
and the failure is silent in the direction that matters — it reports *absence*. The fix is
cumulative totals rather than a better sample rate, because a total has no phase:

```
[totals: characters=18 colonies=390 research=22,269 ships=15,747]
```

### State of the run

```
0 crash dumps, 0 guard hits
5 full states, ~1,650 deltas, 770 KB of delta traffic
delta build avg 0.8 ms  [research=0.46 ships=0.14 fleets=0.07 characters=0.03 colonies=0.03]
```

---

## The 24.8 ms spike, attributed — 2026-09-09

`worst=24.8ms` against a 0.8 ms mean. It is **two different things**, and the larger one is
not ours.

### The steady-state spikes are GC pauses charged to whatever was running

40 builds in a 240-second run exceeded 5 ms, mostly 5–12 ms. The evidence, in the order it
arrived:

| Observation | |
|---|---|
| A gen0 collection ran inside **42 of 42** slow builds | |
| …and inside **0 of 6** sampled typical builds | |
| The section blamed was `research` 20×, `ships` 17×, `fleets` 3× | |
| **`fleets[n=0 +0 -0]` was charged 4.63 ms** | ← the one that settles it |

A section that saw zero objects, sent none and removed none **cannot take 4.63 ms doing it.**
So a section's elapsed time can be almost entirely stall, and `Timed` — which measures
wall-clock around the call — attributes a pause to whichever section was executing when it
landed.

**This corrects an inference made earlier the same day.** The first reading of the GC
correlation argued causation ran work → GC, on the grounds that "the elapsed time is inside
a named section, so the work is ours". That reasoning was wrong, and the empty fleet section
is what disproved it. Time inside a section is not evidence the section spent it.

Research and ships lead the table because they are the **longest** sections, so they are the
most likely to be mid-flight when a collection lands. That is a selection effect, not a cost:
a 0.2 ms build rarely overlaps a collection, a 2 ms build often does.

**DW2 allocates across ~230 threads and a gen0 collection suspends all of them.** A mod
cannot avoid a process-wide pause. What it can do is be running for less of the time a pause
might land in — which is exactly what the research work already did, and why the spike
population shrank along with the mean.

### The two genuine outliers are startup, and they are real work

| Build | Cost | What |
|---:|---:|---|
| #1 | 20–22 ms | first delta — creating every ship, colony and character from an empty baseline |
| #5 | 20 ms | first research window — 10,300 projects compared against a baseline primed several seconds earlier |

Both are one-off. Neither recurs, and reporting a `worst` without saying *which build* made
the pair look like steady-state behaviour. The log now carries `worst=20.9ms@#5`.

### What this changes, and what it does not

Nothing needs fixing. The steady-state spike is not delta work, and the two large outliers
are unavoidable first-time costs paid once per session. The useful outcome is the
instrumentation:

- per-build section breakdown, not just running averages — an average cannot explain an outlier
- GC counts **on typical builds too**, because a correlation with no control is not evidence
- collection sizes per section, which is what exposed the empty-section charge
- `worst` carries the build index, so startup cannot masquerade as steady state

---

## Fleets: the collection was wrong, not the galaxy — 2026-09-09

The fleet path had never executed. Three runs reported `Fleets=0`, and that was read as "this
early-game galaxy has not formed any yet". A bigger galaxy was run to make them form.

**It did not, and that is what gave the answer.** A 50-star, 19-empire galaxy over 480 seconds
scaled everything else and not fleets:

| | 15-star | 50-star |
|---|---:|---:|
| Ships | 273 | 576 |
| Colonies | 3 | 15 |
| Empires | 9 | 19 |
| **Fleets** | 0 | **0**, across all 330 sampled deltas |

Everything scaling except one collection is not what "too small" looks like. **`Galaxy.Fleets`
exists, is a `FleetList`, and is permanently empty — fleets live on `Empire.Fleets`, one list
per empire.**

### The census worked and was misread

`Fleets=0` meant *"this collection has no fleets, ever"*, not *"this galaxy has none yet"*. The
census had been added specifically to tell those apart, and was then read the wrong way anyway.

**A count of zero cannot distinguish an empty collection from the wrong collection.** Only
knowing where the objects live can, and a plausible field name at the galaxy level is not
evidence — the same trap as `Empire.Name` being a property and `EmpireId` being `Int16`.

Two things had to line up to hide it: a convincing field name, and a galaxy small enough that
"no fleets yet" was a believable story. The bigger run removed the second, which exposed the
first.

### What changed

- Fleets moved out of the galaxy-level whole-object table into their own **two-level walk**
  over `Galaxy.Empires` → `Empire.Fleets`.
- **The key packs both ids.** `Fleet.FleetId` is an `Int16` scoped to its empire's list, so two
  empires can each own a fleet #1. A baseline keyed on `FleetId` alone would have treated one
  empire's fleet as a change to another's. `(empireId << 16) | fleetId` makes the collision
  impossible rather than unlikely.
- The census now sums `Empire.Fleets` across empires, so the number it prints is about the
  place the code actually reads.
- Wire version 6 → 7: the section layout changed.

### Verified end to end

```
host    fleets[n=1 +1 -0]   once -- a fleet formed and was sent
host    fleets[n=1 +0 -0]   x171 -- present, nothing to send (membership only)
client  totals: ... fleets=1     -- applied
0 crash dumps, both processes alive at 480 s, peak 8.7 GB each
```

`new Fleet(galaxy)` → `Fleet.ReadFromStream` → `CheckAdd` ran on a real fleet and worked. None
of the three risks flagged before the run materialised: serialising a fleet was not expensive,
the `Int16` `GetById` overload resolved, and ships-before-fleets ordering held.

**The per-delta log never showed it** — the fleet arrived on a delta the every-50th sampler did
not land on. The cumulative totals caught it, which is the mechanism added after the research
blind spot. A fix for one measurement trap paid for itself on an unrelated one.

### Honest limit

**One** fleet formed in eight minutes across eighteen empires. The path is now exercised rather
than unexercised, but lightly: one creation, no removal, and no case where two empires both own
a fleet — which is precisely what the packed key exists to handle. That specific collision is
still untested.

## Designs, read-back, tombstones, and delta baselines — 2026-09-09/10

Four findings from the last two loopback runs, each with a number. Full narrative is in
the commit messages (`ae5d108`, `331dfaa`); this is the record.

### The fleet key collision is tested — by construction, not by luck

The previous section ended on "no case where two empires both own a fleet — untested".
Waiting for the AI to form two fleets in two empires at once is a coin toss per run, so it
was made deterministic instead: **`StateDelta.SelfTest()` runs at every launch** and checks
that `PackFleetKey(1,1) != PackFleetKey(2,1)`, that packing round-trips across
`{0, 1, 2, 255, 256, short.MaxValue, -1, -2, short.MinValue}²` (the sign bit of an `Int16`
is exactly where a naive `<<16` would corrupt), and that a baseline keyed on the packed
value keeps two empires' fleets apart. It logs `fleet key self-test PASS` or names the
failing case. Two real fleets have since crossed end to end in one run (`fleets[n=2]`).

### The ship-creation failure was a missing collection

Client-side ship creation failed at `RegenerateSummary` with a `NullReferenceException`,
100–173 times a run, and three hypotheses were wrong in a day (list membership first;
`CheckAdd` a no-op; a skipped resolve pass). A probe that reads each input of
`ShipSummary.DetermineShipSummary` **under its own try/catch** named it:

```
_Galaxy=ok Empire(item)=ok GetShipHull=NULL(HullId=?) BattleData=ok Components=ok _Design=ok
```

A ship resolves its hull through `Galaxy.Designs.GetById(DesignId)`, and the AI designs a
ship and then builds one, so a created ship routinely names a design the client has never
seen. **Designs are now a galaxy-level whole-object kind, membership only, written and read
before ships.** Crash dumps 506–626 → **0**; mutation failures 100–173 → **0**; 1,373–1,439
designs carried per run.

The first version of that probe read every input in one block, and one
`AmbiguousMatchException` on an overloaded member aborted it 100 times without answering.
An instrument that can fail as a unit fails on the exact input it was built to see.

### Every mutation is read back, and `Count` is not membership

`catch { return false; }` had hidden the 173 failures above as a slowly widening ship-count
gap. Every create and remove is now verified by `GetById` afterwards, with the *stage* of
any failure logged once per distinct reason.

With zero removal failures the client's ship count then swung to **178 more than the
host's**. Read-back said the ships were gone; `Count` said otherwise:

```
NOTE — Ship removed by id but ShipList.Count did not drop (596 -> 596)
```

`IndexedList` removal clears the index and leaves a tombstone that only the simulation's
compaction reclaims — and the client does not simulate. **On a non-simulating client,
`Count` includes tombstones.** The host/client gap is not a sync metric there; the totals
by kind are.

### Deltas were overtaking their full state

With creation never failing, 1–20 updates per delta were still "absent". The host sends
full states through the sender thread's one-slot outbox and deltas inline from `HostTick`,
so a delta built after snapshot T could arrive before T, be applied to the galaxy T was
about to replace, and vanish with it.

**Every delta now carries an 8-byte baseline** — the number of the full state it was built
after. The client applies a delta whose baseline matches the full state it holds, holds
one whose state is still in transit and re-queues it in order when that state lands, and
drops one for a state already superseded.

| | before | after |
|---|---:|---:|
| Deltas held at join, then released | — | 198 |
| Held per later resync | — | 3 |
| Dropped | — | 0 |
| Sampled deltas with absent updates | 27 / 63 | **10 / 53** |
| Crash dumps / mutation failures | 0 / 0 | 0 / 0 |

The remaining 10 are **not attributed**. `ShipList.WriteToStream` does not skip destroyed
ships, so it is not that. It is the open item going into the first two-machine test, where
a real link will reorder things differently than loopback does.

## The first two-PC run: content is a second compatibility axis — 2026-09-10

Everything before this was two processes on one PC. The first real join, over Tailscale
between two machines, **worked end to end** — handshake, 4.5 MB full state, adoption
(`ships=189 empires=13`), 65 held deltas released, deltas applying — and then the joiner
crashed 18 seconds later in `ScaledRenderer.LoadBillboardTextures`:

```
Missing texture: Ships/Planet Destroyers/FX/Shakturi/projectile
```

### Same version, different game

Both games were 1.3.6.3 and the handshake said so. What differed was **DLC**. The texture is
in `PlanetDestroyers.bundle`; `DWGame`'s static constructor assigns that bundle to the
*Return of the Shakturi* content pack, and `DWGame.CheckBundleAllowed` mounts a pack's
bundles only if `ContentPack.Installed` — set by the store's ownership check in
`DWGame.Initialize` — is true. The host owns the pack, so its galaxy carries Planet
Destroyer designs (component 397, *Super Inferno Torpedo*, `FlipbookTextureFilepath` = that
path). The joiner's game never mounted the bundle, and the first frame that drew the
adopted galaxy asked for a texture it did not have.

The mod's own adoption checks passed: `all 140 component id(s) resolve in ComponentsStatic`
was true because the component *definitions* ship with the base game — only the *bundle*
is gated. A definition resolving says nothing about whether its assets are mounted.

### Two checks, because one side of them lies harmlessly

- **The launcher** sends each player's content packs — by which bundle files exist under
  `data\db\bundles` — with their slot. Steam only downloads a DLC's depot when the account
  owns it, so presence on disk is a good proxy and it costs nothing to check before the
  game is even launched. The host compares on join, shows a dialog naming who has what,
  and **Start session stays disabled**.
- **The mod** reads the real `Installed` flags after `DWGame.Initialize` (they are false for
  everything before that), exchanges them as a `Content` message, and on a mismatch **refuses
  to exchange state in either direction**. Protocol version 1 → 2.

Both are needed: the disk check runs early and is visible in the UI but is a proxy; the
in-game check is authoritative but only fires once two games have loaded. A GOG or Matrix
install, or a pack disabled in the game's own settings, is exactly the case where they
would disagree.

### Two more findings from the same run

**"Speed stuck at max"** was the M2 harness leaking into the session. `FixedStep` pinned
the host's clock to 100 ms per server cycle, so the speed setting had no effect on
simulated time, while `StartTheClock` forced `ChangeGameSpeed(4)`, so the UI showed 4x. Ten
cycles a second at 100 ms each is exactly 1x; the game ran at 1x with a 4x label and
buttons that changed a number nothing read. In a lobby session `DW2MP_STEP_MS` now
defaults to 0 — DW2's own `AdvancedStopwatch` clock, speed buttons and pause all work,
start at 1x — and the forced speed is opt-in via `DW2MP_GAME_SPEED`. The harness scripts
still set 100 explicitly, so the determinism runs are unchanged.

The client needed its own answer: it does not simulate, so its `GameServer.Now` — which
`Galaxy.GetServerNow()` and every date display, ETA and countdown read — was running from
its own start at its own rate. **Every delta now carries the host's `Now` and speed** (20-byte
tag: baseline, time ticks, speed; speed 0 when the host is paused), and the client's
`get_Now`/`get_NowPrecise` return the host's time extrapolated at the host's speed since
the last delta (`FixedStep.FollowHost`). Continuous, and it stops when the host pauses.

**"The joiner cannot edit the empire"** in co-op is correct — one empire, the host
configures it — but the form took the input and discarded it without saying so. The empire
fields now carry a note in every state, and in co-op they lock once the session arrives
and name the host's empire. Also fixed the same morning, before the run: the host accepted
exactly one connection and stopped listening, so the joiner's own **Test** probe consumed
the lobby (`f1b5554`).

### What the run proved

Adoption over a real link on the first attempt, with latency and a real TCP stack, behaved
exactly as loopback had: 352 ms read, 38 ms apply, deltas held for the in-flight full
state and released in order. The transport was never the problem. Content parity was —
and it is the kind of problem that only two different machines can find.

### The tick had to be redefined with the clock — same day

Running DW2's own clock changed what a "tick" was. `UpdateGameAsServer` is called **every
frame** and runs a logic cycle only when `GameLogicCycleLengthInMilliseconds` of wall time
has passed (its own gate, on `DateTime.Now`, independent of speed and pause). Under
FixedStep every call advanced the clock and so every call was a cycle; under the real clock
the mod's tick ran at frame rate — the first real-clock loopback run sent the "hourly"
safety full state after three minutes and would have sent a delta every 150 ms. A tick is
now a call on which a logic cycle is due (`Determinism.LogicCycleDue`), ~30/s in either
role at any speed; FixedStep runs are unchanged.

Two more things the same run exposed, both fixed: the watchdog inferred "stalled" from a
flat cycle count, which a pause no longer produces (the server is still called and does
nothing) — it now asks `IsRunning`/`GetGamePaused` and reports `PAUSED`; and in a lobby
session `DW2MP_KEEP_RUNNING` is now off, because a pause there is the host's decision and
a watchdog that un-pauses the game the host just paused is a bug wearing a safety feature.

Verified on loopback, 15 stars, 180 s: content OK both ways, host at 1x (67 s of game time
per 67 s of wall time), deltas every ~1 s at 135 B, client following, **0 crash dumps, 0
mutation failures**.

## Content parity, second pass: refused in the lobby, versions, and one comparison — 2026-11-09

mitch re-enabled the four DLC on his PC and Taylor's PC probably owns none, so the
2026-09-10 content check got its second pass before the next two-PC attempt. The lobby side
is described in `lobby.md` (*Content packs are checked before anyone launches*); the
mod-side changes are these.

- **Any handshake mismatch now blocks state.** `ReadHello` used to log `PROTOCOL MISMATCH`
  and `GAME VERSION MISMATCH` and carry on exchanging state anyway; only a content mismatch
  stopped it. All three axes now feed one `_incompatibility` reason (first one wins, so the
  log names the cause), and `IncompatibilityBlocksState` refuses to send or adopt state on
  any of them. The wire format is unchanged -- Hello still carries protocol, game version
  and mode in that order; Content is still `Name=1;Name=0` -- so `NetSession.ProtocolVersion`
  stays at 2 and an older peer logs its own mismatch line against our number.
- **One comparison, tested.** `ContentPacks.Compare` now delegates to
  `src\Shared\ContentPackCompare.cs`, the same source the lobby compiles in, and
  `src\Dw2MpTests` covers it (the protocol-2 wire string parsing exactly is one of the
  checks, so the format cannot drift without a failure).
- **The session file carries a protocol number the mod now checks.** `SessionConfig` logs
  `*** PROTOCOL MISMATCH ***` when `session.json` is not `ExpectedProtocolVersion` (2). A
  loud line, not a refusal: the lobby writes the file fresh at every launch, so a mismatch
  means the lobby and the mod on one machine came from different packages.
- **What the game reports, exactly.** `DistantWorlds.Types.ContentPack` has `Name`,
  `Installed`, `SteamAppId`, `GOGClientId`, `MatrixRegKey`, `BundleNames`, `BundleSubFiles` and
  store URLs -- no version field. Versions therefore come from Steam's depot manifests in the
  lobby, and the in-game check compares installed sets only, as before.
- **Build drift noted.** `DistantWorlds2.exe` is now `1.3.8.4+820e12…` (Steam buildid
  26240608); several docs still say 1.3.6.3. The checks compare whatever both sides have,
  so the number in the docs is informational.

Verified: `dotnet build` of the mod, the lobby and the test runner (0 new warnings; the two
`CS0169` unused-field warnings in `NetSession` predate this), 44/44 checks, and the
inventory reader run against the real install. Not verified: anything that needs a launch or
a second PC -- see the end of the lobby.md section for what to look for.

## The harness and the game's session lock — decision 2026-11-10

DW2 writes `data\SessionActive` in `DWGame.Initialize` and removes it in `DWGame.Destroy`, the
only caller of `RemoveSessionFile`. A session that ends any other way leaves it behind, and the
next launch then stops at a Win32 "Send Error Information" prompt before Stride starts: no
SessionLog, no GameServer, nothing in the mod log. The harness met this three times on
2026-11-09/10 — after `Environment.Exit` (now routed through `Determinism.ExitProcess`, which
calls the game's own `RemoveSessionFile` first), and after runs the script had to end by force.

**Decision (mitch, 2026-11-10): the harness may remove its OWN lock after a run it had to end
by force, and nothing else.** Own means: no `DistantWorlds2` process is running, and the lock's
`Session Started dd-MM-yyyy HH-mm-ss.fff` time falls inside that run's launch window. A lock
found before any launch, or one with a time outside the window, is left for a human with the
fail-fast message, because it may be a session mitch started. `Scripts/determinism-run.ps1` and
`Scripts/make-save.ps1` both apply this (`Remove-OwnSessionLock`) and log what they removed.

Two more facts from the same night shaped the scripts. `--continue` loads the newest `.DWGame`
by write time (`MenuRenderer.ContinueGameClick`, `MaxBy GetLastWriteTime`), so an autosave
during run 1 is what run 2 loads; the harness now switches `GameSettings.AutoSave` off in
memory for the run (`AutosaveBlock`, `DW2MP_BLOCK_AUTOSAVE=1`) and refuses a verdict if the
newest save changes between runs. And under Windows PowerShell 5.1 `Process` has no
one-argument ending overload: the call threw into an empty `catch` and every timed-out DW2 of
the night stayed alive, so the scripts call the parameterless one.

## What differed at cycle 1, and what still differs later — measured 2026-11-10

The first valid two-run comparison on 1.3.8.4 (06:43, `MPBase1384.DWGame`, all DLC off) read
"diverged at the FIRST snapshot" with identical byte counts at cycle 1. The run was repeated
with `determinism-run.ps1 -DumpDir` (new; the mod already had `DW2MP_DUMP_DIR`) and the two
cycle-1 streams were diffed byte for byte.

**Cycle 1: 125 bytes in five groups, all renderer caches.** Each group is one `ShipHull`
record (hulls are definitions, serialised early in the stream at 1.5–4.3 MB). In every group
`ShipHull._ExteriorComponentBaysLoadedFromModel` was `00` in run 1 and `01` in run 2, and 23 or
35 bytes later (MaximumSize, DisplaySize, an empty BonusList and 2 or 4 ResourcesRequired
entries in between) the hull's `BoundingBox` was Stride's `BoundingBox.Empty`
(`7F7FFFFF ×3, FF7FFFFF ×3`) in run 1 and a computed box in run 2. Both are filled by the
renderer when it first loads a hull's 3D model (`ShipHull.ExamineModelForExteriorBays`), so
they record which five hull types the camera happened to draw in the first second. The
simulation never reads them and the game recomputes them on load. **Classification: (a),
presentation noise.** It is the same signature the 2026-09-07 M2b run attributed to "lazy
caches"; this time the field is named.

**Fix.** The determinism hash now serialises through `SnapshotHashWriter`
(`src\Shared\SnapshotHashWriter.cs`, pure .NET, tested) with two Harmony patches in
`src\Dw2Mp\SnapshotHash.cs` that only act when the writer is that type: the first bool a hull
writes is written as `false`, and `SerializationHelper.WriteBoundingBox` is redirected to the
empty box. Nothing on the galaxy is mutated; real saves and the FullState a joiner adopts use
a plain `BinaryWriter` and are untouched. The run log says how many hulls were normalised
(`# hash: normalised hulls=781 flags=781 boxes=781`) and warns if the counts disagree or the
patch is missing. The host/client comparison (`ApplyState.StructuralFingerprint`) never used
the serialised stream, so it did not need this.

**Rerun with the fix, same save, 5 snapshots, 2,000 cycles apart:**

```
   cycle       bytes  run1                run2
       1    12861009  09518E127AF08DA9    09518E127AF08DA9
    2001    12987093  5965108122665244    9DAA7DA3B1EE52CE    <-- DIVERGED
    4001    12989009  4C64F3D1038AB52B    F50530ADD9153386    <-- DIVERGED
    6001    12992998  CC2E5C599C9C35E2    3DBD9549B94D08BA    <-- DIVERGED
    8001    13011286  8B08F27358658FEF    12359C0B22EFD62A    <-- DIVERGED
VERDICT: identical at first, diverged at cycle 2001.
```

`fc /b` on the two cycle-1 dumps: no differences. Cycle 1 is now proven byte-identical, which
is what makes the later rows evidence.

**Cycles 2001 and later: real simulation divergence, (b).** The harness table prints run 1's
byte column only; run 2's streams were 9.7–14.1 KB longer at every later snapshot, and the
diffs cover 728 K–988 K bytes (5.6–7.6 % of the stream), growing each snapshot. Different
sizes mean different numbers of entities, not drifting values. The first differing offset is
6,513 in every case: a block of game-time `DateTime` ticks just after the galaxy header (the
last-run times of periodic galaxy jobs) that differ by a few seconds of game time, i.e. the
two runs scheduled the same periodic work on different ticks. DW2 also auto-paused on an
empire message at cycle 2,398 in one run and 2,733 in the other. This matches M2b/M2c
(2026-09-07): fixed timestep and pinned block sizes are not sufficient, and the cause is
thread timing, not float rounding. Nothing here is a hash defect, and DW2 is not going to be
made deterministic from a mod.

**What it means for multiplayer.** Nothing changes in the design, and the measurement now
supports it cleanly: a joiner cannot run its own copy of the simulation and stay in step, so
the joiner does not simulate and takes the host's state (full states plus deltas), exactly as
`mp/host-authoritative` does. The determinism harness remains the way to tell a harness or
hash defect (cycle 1) from DW2 itself (later cycles).

## Desync popup: who desynced, save and quit, attempt resync — mitch, in chat 2026-11-10

"if the game desyncs please bring up an in game error and try to add a resync feature and
button to the game. lets do the usual if there is a detected desync have an option come up
that says who desynced. in the pop up lets have two options save and quit 1 attempt resync 2".

Built on `fix/hash-noise-1110`, in `src\Dw2Mp\Desync.cs` with the pure logic in
`src\Shared\DesyncPolicy.cs` (compiled into the mod and into `src\Dw2MpTests`).

- **Detection is unchanged:** the client compares the host's structural fingerprint on every
  `StateSummary`; three mismatches in a row are a divergence. What changes is the response.
- **Who desynced.** The client sends `Msg.DesyncReport` (its player name and slot from the
  session file, host tick, both fingerprints, game time). The host pauses `GameServer` and
  both machines show the game's own two-button dialog
  (`UserInterfaceController.ShowMessageDialogCentered`, 9-argument overload, through
  reflection like the rest of the mod): "DESYNC DETECTED — Taylor (joiner, slot 1) is out of
  sync with the host's game at &lt;game time&gt; (host tick N)".
- **1. Save and quit** saves `Desync-yyyyMMdd-HHmmss.DWGame` through `DWGame.SaveGame` (its
  own name, so the pinned harness save and the Autosave slots are never overwritten), reads
  the file back, and exits through `Determinism.ExitProcess` so the session lock is removed.
  The other side is told (`Msg.DesyncChoice`) and, if it is the host, resumes.
- **2. Attempt resync**, pressed on either side: the host serves a fresh FullState (the
  existing `_resyncRequested` path; a client press bypasses the 60 s rate floor), the client
  adopts it, and the first summaries after the new state has landed decide — a match is
  success, three mismatches are a failed attempt. The client sends `Msg.DesyncOutcome`; both
  sides show "Resync succeeded" (host resumes) or the choice again. After two failed attempts
  only "Save and quit" is offered. Both popups stay in step because every choice and outcome
  carries the failure count and the peer mirrors it.
- **The sim pauses** on the host while the popup is open (`GameServer.PauseGame`); the
  watchdog's auto-resume is suppressed while `Desync.HoldsPause`.
- **Wire format:** three new message types, so `NetSession.ProtocolVersion` is 3. Hello is
  unchanged, so a protocol-2 peer still reads it and logs its own mismatch. All three payloads
  are parsed defensively (strings capped, every reader in a try/catch).
- **Dev flag:** `DW2MP_FORCE_DESYNC_AT_CYCLE=N` makes a client report a fake divergence once it
  reaches cycle N (its fingerprint is replaced until the popup is raised, then the forcing
  stops so a resync can genuinely succeed).
- **Logging:** every step is a `# desync:` line in the mod log, including the popup text.

Verified: `dotnet build` of the mod (0 errors, the two pre-existing CS0169 warnings) and
126/126 checks, 22 of them on `DesyncPolicy` (who-text, detect-once, both buttons then only
save-and-quit, save-and-quit always allowed, success resets, mirroring, save name). **Not
verified:** the popup on screen and the resync round trip. That needs the lobby's two-instance
session (the lobby is a WinForms launcher with no headless mode, so an agent cannot drive it
unattended). To exercise it on one PC: start the lobby as host and joiner, set
`DW2MP_FORCE_DESYNC_AT_CYCLE=600` in the joiner's environment before launching, and read both
mod logs for `# desync:` lines; the popup should appear on both after the third summary past
cycle 600.

**Security review, same day (game-security on c9e152e), applied on `fix/desync-popup-1110`:**
nothing from the wire decides what a side offers. The failure counter is no longer mirrored
from the peer (one `DesyncChoice` with `int.MaxValue` used to remove the resync button); each
side counts its own transitions, and an outcome only counts while a resync is in flight (tested
with 1,000 hostile outcomes). Who desynced is read from this machine's `session.json`, never
from the wire, and the report, choice and outcome payloads now carry only primitives plus two
fingerprints and a game-time string, sanitised with `PeerText` (now linked into the mod) and
capped at 1 KB per frame -- a bigger desync frame drops the connection. A resync not verified
within 60 s fails; a lost launcher link resumes the host and resets the incident; reports are
accepted at most once per 30 s; the dialog queue holds two; a rejected local press re-shows the
choice so a paused host is never left without a dialog; the host ignores served-state requests
within 600 ticks of the last except one per accepted desync choice; the three message cases
honour the handshake block; the lobby strips `DW2MP_FORCE_DESYNC_AT_CYCLE` from the child
environment unless `DW2MP_DEV=1`; and `SnapshotHash` skips its positional normalisation (and
says so) on a game build other than 1.3.8.4. The popup keeps mitch's two options; a third button
is his call.

## Desync reduction: measured, then built — mitch, in chat 2026-11-10

Five things asked for, to make desyncs less likely: measure what diverges, serve periodic
keyframes, carry more state in deltas, make the desync check tolerant, and make pause the
host's alone. Built on `feat/desync-reduction-1110`. Net protocol **4**, delta body **9**.

### The harness: two instances, no lobby

`Scripts\net-twoinstance.ps1` runs a host and a joiner on one PC with no lobby window. The
lobby only pumps frames between two sockets once the games are up, so `dw2mpprobe relay
<hostPort> <joinerPort>` (new, `src\Dw2MpProbe\DevModes.cs`) does that from a console and
counts traffic per message type. The script writes both session files, launches both games
**at the same moment** (two instances share `data\SessionActive`, so the second must pass its
start-up check before the first creates the lock; both passed in both runs), and both mods
leave by themselves after `-Cycles` server cycles through `Determinism.ExitProcess`, which
removes the lock. The host runs with `DW2MP_KEEP_RUNNING=1`, the joiner with `0` exactly as
the lobby launches it.

With `-DivergenceDir`, both games write a census of their galaxy at the same host tick
(`src\Dw2Mp\DivergenceDump.cs`: every primitive field, every one-level struct and the `Count`
of every collection field, for ships, colonies, characters, designs, empires, relations and
fleets), the joiner after applying every delta that preceded the summary. `dw2mpprobe
divergence <dir>` diffs each pair and ranks fields by how many objects differ
(`src\Shared\DivergenceReport.cs`, tested).

**The tick is not 10 a second.** Both games counted about 29 cycles a second (870 per 30 s
watchdog interval). `LogicCycleDue` follows the game's own `GameLogicCycleLengthInMilliseconds`.
So on 1.3.8.4 a summary goes every ~10 s, not 30; three mismatches are ~30 s, not 90; and the
600-tick served-state floor was ~20 s, not the 60 it was reviewed as. The summary and delta
constants are unchanged; the keyframe interval is measured in wall time for this reason, and
since the security pass below so is the served-state floor (60 s, as documented).

### Measurement 1 (before any fix): the desync popup was a false alarm

First run, origin/main logic plus the dump. Thirty seconds after the join both machines showed
DESYNC DETECTED and the host paused behind the popup:

```
joiner  # net[client]: structure diverged — host B0B9C3BB6E2A8C19 vs mine ADAD4834A190E1E9 at host tick 1200
host    # desync: REPORTED by HarnessJoiner (joiner, slot 1) at host tick 1200 ... host B0B9C3BB6E2A8C19 vs theirs ADAD4834A190E1E9
joiner  # net[client]: APPLIED sync #2 ... ships=282
joiner  # net[client]: structure diverged — host B0B9C3BB6E2A8C19 vs mine ADAD4834A190E1E9 at host tick 3000
```

The same two fingerprints before and after a fresh full state. The census at tick 1200 and
again at tick 3000 says why: **282 ships on both sides with identical ids, 12 colonies, 12
characters, 669 designs, 14 empires, all equal; `Orbs` 1,401 on the host and 1,400 on the
joiner.** `ApplyState.StructuralFingerprint` hashed `e=<Empires>;o=<Orbs>;s=<Ships.Count>;`
in front of the ship ids. Deltas never carried orbs, and the one-orb difference survives a
full state, so nothing could repair it: both resync attempts were bound to fail and leave only
"Save and quit". A desync, by this project's own definition, is state the host changed that
the sync does not carry; here the CHECK compared state the sync does not carry.

Two more things the same run showed:

- **The joiner sat auto-paused for the whole session**: `GameServer.IsRunning=False,
  DWGame.AutoPaused=True, GetGamePaused=True` at every watchdog line from its first adoption.
- The host's first delta, before any full state, was **364,534 B built in 439 ms** on the
  simulation thread (every design and ship as "new") and was applied to the joiner's throwaway
  galaxy.

### Measurement 1: what really differed, ranked

Objects whose value differed between host and joiner, same host tick, ~10 s and ~30 s after a
full state (host at 4x). 282 ships, 12 colonies, 669 designs:

| State | Objects differing | Kind |
|---|---:|---|
| `Design.BuildCost`, `MaintenanceCost` | 542–622 of 669 (host 3,228 / joiner 1,500) | derived on the host, not restored by a full state |
| `Colony` per-race population | 12 of 12 | simulation state |
| `Colony._DevelopmentLevel` | 12 of 12 (host 0.52 / joiner 0.78) | derived, not restored |
| `Colony._MigrationFactor` | 10 of 12 | simulation state |
| `Colony.TaxRate` | 6 of 12 | simulation state |
| `Colony.ColonyInfluenceRadius`, `GrowthFundingRatio` | 3 of 12 each | simulation state |
| `Ship.Shields` | 40 | simulation state |
| `Ship.HullConstructionLevel` | 19 (ships being built) | simulation state |
| `Ship.Energy`, `Ship.Fuel` | 15, 11 | simulation state |
| `Ship.UnbuiltComponentCount`, `NormalComponentCount`, `#Weapons` | 12, 16, 8 | a ship's component list while it is built |
| `Galaxy.#Troops` 12 / 11, `#SpaceItems` 136 / 129, `#Orbs` 1,401 / 1,400 | — | collections deltas do not carry |
| `Character.#Events` | 4 of 12 | contents (membership only, by design) |
| `Ship.SummarySize`, `StrengthCached`, `NebulaPermeability`, `ScannerLongRange`, `#ComponentDisplayData`, `Colony.#BonusesComplete` ... | 131–282 | caches the simulation derives; the joiner never computes them |

Membership was exact in every sample: no ship, colony, character or design on one side only.
Empires and relations could not be compared in this run: the host-side dump died on one
unreadable field before it reached them (fixed; every field is now read under its own guard).

### 2. Keyframes

The host serves a full state by itself (`src\Shared\KeyframePolicy.cs`, tested; wired in
`NetSession.HostTick`):

- **every N minutes of RUNNING game**, N from `%LOCALAPPDATA%\Dw2Mp\sync-settings.json`
  (`{ "keyframeMinutes": 5, "keyframeOnEvents": true }`; default 5, clamped to 1–120, 0 = off)
  or `DW2MP_KEYFRAME_MINUTES`. Paused time does not count: a paused galaxy has not changed
  and every adoption costs the joiner native memory. (mitch asked for a default of 3; it
  was raised to 5 the same day, once the cost per adoption below had been measured.)
- **after a big event**: war declared, peace made, first contact, a colony gained or lost, an
  empire eliminated. Detected by diffing a small census every summary (`GalaxyEventWatch`,
  pure half in `src\Shared\GalaxyEvents.cs`), not by hooking: DW2 changes a relation through
  fourteen `ChangeEmpireRelation` overloads. Wars, peace, contact and colonies are watched for
  the **players' empires** only; a dozen AI empires meeting each other would otherwise cost a
  keyframe each. Elimination counts for any empire.
- **inside the existing floor** (`MinTicksBetweenServedStates`): an event inside it waits and
  is served when the floor has passed; it is never dropped. A joiner's request inside the
  floor is still ignored, except the one a desync choice exempts.

A keyframe is an ordinary FullState, so the baseline number every delta already carries goes
up, and every summary now carries it too. A summary built against a full state the joiner has
not adopted yet, or one older than the state it has just adopted, is **not compared**, so a
keyframe can never start, extend or end a run of mismatches (tested).

**Cost, stated plainly:** each adoption still costs the joiner native memory that is not
returned: ~25–36 MB when measured on 2026-09-08, 43–48 MB in the 2026-11-10 run below. At the
default 5 minutes that is twelve adoptions and roughly 550 MB an hour, before any event
keyframe. The interval is in a file for that reason, and a per-session memory budget (stop
serving periodic keyframes once the joiner has adopted N) is a follow-up, not built.
(2026-11-11: measured and attributed in "Adoption memory: what leaks, measured" below. The
budget that was built is a one-time notice to the player, not a stop to keyframes.)

### 3. More state in deltas (delta body 9)

Five sections appended after fleets (`src\Dw2Mp\StateDelta.Carried1110.cs`), so the older
sections' layout is untouched:

| Section | Carries | How |
|---|---|---|
| ship vitals | shields, fuel, energy, hull construction level | field level, thresholded |
| colony extras | development level, tax rate, migration factor, influence radius, growth funding, and the **per-race population list** | field level, thresholded; races added and removed on the joiner |
| design costs | build cost, maintenance cost | field level, exact |
| empire money | state and private cash | field level, thresholded |
| relations | an empire's whole relation list | **DW2's own `EmpireRelationList.WriteToStream`**, sent when a relation's flags change |

Relations follow the ship path. On the joiner a NEW `EmpireRelationList` is filled by DW2's
own `ReadFromStream` and swapped into `Empire.Relations`, which is what loading a save does;
the swap only happens when the reader consumed exactly the bytes the host wrote. The trigger is
a signature of every bool, byte and short on each relation (HaveMet, AtWar, Subjugated, the
thirteen Treaty flags, TreatyTradeLevel), not a byte hash: relations also hold incident history
and dates that move constantly, the same trap that made refreshing characters cost 32 MB.

Colony extras and design costs are **not primed** after a full state. The host cannot assume
the joiner's copy equals its own for values a full state does not restore, so those two
baselines are cleared and the first pass after every full state sends them all. Research
progress was already carried and stays as it was.

Thresholds are one tested function (`src\Shared\SyncThresholds.cs`).

### 4. The desync check

- **Compares only what the sync carries.** `StateSummary` now carries a `SyncDigest`
  (`src\Shared\SyncDigest.cs`): per collection that deltas create and remove members in
  (ships, colonies, designs, characters, fleets), a count and an order-independent hash of the
  member ids, read from live objects and never from `Count` (lists keep tombstones). Built by
  `StateDelta.BuildDigest` from the same lists the delta sections walk. No orbs, no empires.
- **Is ordered.** The host builds a delta on the same tick as every summary, and the joiner
  compares on the main thread only after every delta that arrived before the summary has been
  taken off the queue. The check used to run on the network thread the moment the packet
  landed.
- **Tolerates known latency** (`DesyncTolerance`, tested): members the host says it has not
  sent yet (`Unsent`: created or removed since its last delta) are latency for as long as the
  host says so; one more than that is tolerated for one summary; the same collection still
  off on the next summary is a mismatch.
- **Only a sustained real mismatch raises the popup**: three in a row, unbroken by a match,
  as before. On the first real mismatch the joiner now quietly asks for a full state (both
  rate floors apply); if that repairs it nobody is asked anything.

### 5. Host-owned pause

A pause in DW2 is local: `DWGame.PauseGame(bool)` stops the local `GameServer` stopwatch and
tells the local `GameClient`s; it sends nothing. So a joiner's pause never reached the host,
and that half was already true. The other half was not: `StartGameExisting` pauses on every
adoption, nothing resumed a joiner, and an empire message auto-pauses the same way.
Separately, the host's own pause only reached the joiner on a delta, and a paused galaxy
sends none, so the joiner's clock ran on at the last speed.

Fixed in `src\Dw2Mp\HostPause.cs` (decision in `PauseMirror`, tested): on the main thread the
joiner's pause state and speed follow the host's clock sample, whoever paused it. And the host
now sends the 20-byte delta tag alone when nothing changed, so pause and speed always arrive.
A save on the joiner is left alone. The joiner's own pause key therefore does nothing for
longer than a quarter of a second; a pause REQUEST from the joiner to the host would be a
vote, and is not built.

### Measurement 2 (the new build): one 347-second two-instance run

Same harness, host at 4x, `DW2MP_KEYFRAME_MINUTES=1` so several keyframes fit in the run, and
on the joiner `DW2MP_FORCE_DESYNC_AT_CYCLE=6500` with the new dev flag
`DW2MP_DESYNC_AUTO_CHOICE=2` (presses "Attempt resync" five seconds after the popup; the
lobby strips it from a real session unless `DW2MP_DEV=1`). Ships grew 217 to 313, designs 508
to 788, characters 9 to 16. Both games left by themselves and removed the session lock.

**The check.** Every comparable summary before the forced one matched, 21 in a row, none
skipped, none tolerated, where measurement 1 raised the popup on the third:

```
joiner  # net[client]: sync check OK #21 at host tick 6600 -- ships=305:894CA2CF colonies=12:E4764427 designs=764:D6001C8B characters=14:77025E29 fleets=0:00000000 (skipped 0, tolerated 0)
```

Across ten sampled ticks the census found **no object on one side only**, and 0 mutation
failures on the joiner.

**Keyframes.** Seven full states: the join, one event keyframe, three periodic, and the two
the forced desync caused.

```
host  # net[host]: full state #2 tick=1500 (keyframe after empire 2 eliminated, empire 4 eliminated, empire 6 eliminated)
host  # net[host]: full state #3 tick=3331 (periodic keyframe)
host  # net[host]: full state #4 tick=5165 (periodic keyframe)
host  # net[host]: full state #5 tick=6902 (client asked)
host  # net[host]: full state #6 tick=7655 (desync popup: attempt resync)
host  # net[host]: full state #7 tick=9637 (periodic keyframe)
```

Three eliminations in one summary shared one keyframe. No keyframe produced a mismatch.

**The popup and the resync round trip, in game for the first time** (it was built and
unverified on PR #13). The forced digest mismatched three times; the first asked for a quiet
repair (#5), which a forced mismatch survives by construction; the third raised the popup:

```
joiner  # net[client]: sync check MISMATCH 3 of 3 at host tick 7500 -- ships host 306 mine 313
joiner  # desync: DETECTED -- HarnessJoiner (joiner, slot 1) diverged at host tick 7500 ...
host    # desync: REPORTED by HarnessJoiner (joiner, slot 1) at host tick 7500 ...
host    # desync: host simulation PAUSED while the popup is open
joiner  # pause: the host paused; joiner paused to match (#3)
joiner  # desync: DEV auto-choice pressing AttemptResync (DW2MP_DESYNC_AUTO_CHOICE=2)
host    # net[host]: full state #6 tick=7655 (desync popup: attempt resync)
joiner  # net[client]: APPLIED sync #6 ... ships=306
joiner  # desync: RESYNC SUCCEEDED -- structure agrees at host tick 7800
host    # desync: RESYNC SUCCEEDED -- the joiner verified the new full state
host    # desync: host simulation RESUMED
joiner  # pause: the host is running at 4x; joiner resumed to match (#7)
```

The dialogs were queued and logged as SHOWN on both sides; nobody looked at the screen.

**Pause.** The joiner's watchdog said PAUSED three times in the run, each time while the host
was paused (the pause DW2 leaves after generation, DW2's own event auto-pause on the host at
60 s with `DWGame.AutoPaused=True`, and the desync popup), and it resumed with the host each
time. Eight resumes in all, five of them logged on the line straight after an adoption:

```
joiner  # net[client]: APPLIED sync #2 ...
joiner  # pause: the host is running at 4x; joiner resumed to match (#3)
```

**What deltas carried.** The joiner's totals at delta #251: `colonyextras=540
designcosts=4,544 money=159 relations=43 vitals=2,401`. What still differed at tick 6300, about
40 s after a full state, against measurement 1:

| State | Measurement 1 | Measurement 2 |
|---|---:|---:|
| Design build and maintenance cost | 542–622 of 669 | **0 of 764** |
| Colony development level, tax rate | 12 and 6 of 12 | **0 and 0 of 12** |
| Colony per-race population | 12 of 12, growing | 7 of 12, all within 0.3 % (the send threshold) |
| Relation flags (HaveMet, AtWar, Treaty*) | not measured | **0 of 212 in every sample** |
| Empire cash | not measured | 3 of 15, within 0.6 % in nine samples of ten |
| Ship shields, fuel | 40, 11 | still top of the ranking, inside their 2 % threshold and three-delta cadence |

Still differing, and not carried: a ship's heading, speed and component list while it is built,
`Design.BuildCount`, `Galaxy.#SpaceItems` (24 / 11), character events and traits, troops, and
every cache the simulation derives (`Colony.#BonusesComplete` 26 / 0, ship summaries, scanner
ranges). The derived caches are a client-side rebuild question, like `Ship.Summary` in
September, not a sync one.

**Traffic**, host to joiner, 347 s at 4x: 333 deltas, 1,228,849 B (about 3.5 KB/s; not split
by section, and it includes 280 whole designs the AI created); 7 full states, 17.6 MB; 33
summaries, 4,719 B. Delta build averaged 1.5 ms, worst 8.8 ms.

### Honest limits

- **One PC, loopback, two short runs, a 30-star galaxy.** Nothing here was run over Steam or
  between two machines, and no late-game galaxy was tried. Wars, peace, first contact and
  colony events are tested as pure logic; the only event the run produced was elimination.
- **In the harness galaxy the players' empire is eliminated after a minute.** "Harness Host
  Empire" (id 2) and two AI empires start with no colony and DW2 removes them at its first
  check. That is generation, not sync, and it is why no player war or colony event fired. The
  lobby's defaults are the same 30 stars, 4 AI, seed 12345, so it is worth one look in a real
  session.
- **Relations are replaced whole.** 43 lists were swapped with no failure, but nothing
  checked that a UI panel holding the old list refreshes.
- **Keyframes cost the joiner memory** (see above). Private bytes on the joiner went from
  5,491 MB at the first adoption to 5,825 MB at the seventh, in under six minutes with the
  interval forced down to one minute.
- **The first run of the harness was left unsupervised.** A script error one second after
  launch ended the script while both games ran on; they left by themselves three minutes
  later. The supervision is now inside a try block that always reaches the clean-up.
- The new message parsing (summary digest, delta sections 9, the empty delta as a clock
  sample) was reviewed the same day; see the next section.

### Security review, same day (game-security on 0e4ad64), applied on the same branch

One merge blocker and a list of hardening. The host is the other player's PC, not a trusted
server, so every count, length, time and name in a frame is a number somebody else chose.
Protocol stays **4**: nothing on the wire changed shape, only what the reader will accept.

- **H1, the blocker: a flood of summaries was a flood of checks.** Every queued summary was
  checked in one frame, and a check walks every ship, colony, design, character and fleet on
  the main thread. `SummaryGate` (`src\Shared\SyncGuards.cs`) now hands the main thread at
  most one summary per call and at most one a second: the newest whose earlier deltas have
  been taken; older ready ones are discarded. Tested with 1,000 summaries: one check in the
  call, none in the next hundred frames of the same second. The tolerated and mismatch log
  lines are throttled like the skipped line.
- **Lengths and counts** (`WireLimits`): every length-prefixed object block in a delta is
  capped at 4 MB and checked against the bytes left in the frame before anything is allocated
  (four older sites read `ReadBytes(length)` uncapped); every record count is refused when it
  is negative, more than the frame could hold, or, for update and removal sections, more than
  the local collection plus a margin; a Delta frame is capped at 16 MB; `MaxRacesPerColony`
  is enforced on read; NaN and infinity are refused in vitals, colony rates, design costs and
  cash.
- **Full states:** decompression is a bounded copy with a 256 MB cap (the link is dropped past
  it), and at most one full state waits to be adopted.
- **Held deltas:** at most 512 deltas or 16 MB wait for a full state in transit, and a delta
  for a state more than one ahead of ours is refused.
- **Clock samples** (`ClockSampleCheck`): followed only when the time is a real DateTime, the
  speed is finite and at most 8x, and the time has not leapt beyond what the wall time since
  the last sample explains; thirty insisting samples in a row are then believed.
- **Digest** (`DesyncTolerance`): a host digest that leaves out a collection this side carries
  is a mismatch, so an empty digest never reads as agreement; "not sent yet" excuses at most 32
  members and three summaries in a row; collection names are lowercase letters and digits or
  the digest is refused, and digest text is passed through `PeerText` before it is logged.
- **Served-state floor is wall-clock 60 s** (`KeyframePolicy`), not 600 ticks. On the joiner,
  a full state it did not ask for waits until 30 s have passed since the last adoption, newest
  kept (`AdoptionGate`); the join, a repair it asked for and a popup resync are never held.
- **Mutation failures** are remembered by a fixed key (stage and exception type), capped at
  64; the variable detail is logged once and not remembered (`FailureLedger`).
- **Relation lists:** before DW2's reader sees the bytes, their header must carry the same
  list EmpireId as the list being replaced and a relation count the bytes could hold.
- **Dev flags:** the mod itself now needs `DW2MP_DEV=1` for `DW2MP_DESYNC_AUTO_CHOICE`,
  `DW2MP_DIVERGENCE_DIR` and `DW2MP_FORCE_DESYNC_AT_CYCLE`, and logs `*** DEV FLAG LIVE ***`
  when one is on. The lobby's strip list became an allow-list (`LaunchEnvironment`): every
  inherited `DW2MP_*` variable the lobby does not set itself is removed, which also covers
  `DW2MP_CLIENT_SIMULATES`, `DW2MP_MAX_TICKS_BETWEEN_STATES` and `DW2MP_DIVERGENCE_EVERY`;
  `DW2MP_KEYFRAME_MINUTES` stays because the mod clamps it.
- **Smaller:** `HostPause` stops following when the link is down or the last clock sample is
  over 10 s old; `StateSummary` is behind the handshake gate; the tolerance state is reset on
  the main thread; the divergence dump samples by host tick on both sides and stops at 400
  files; the host's "client requested a resync" line is throttled.
- **A bug the review found:** new `Population` objects on the joiner were built with
  assimilation and reproduction rate swapped. DW2's constructor is `(raceId, amount,
  reproductionRate, assimilation)`; fixed in `PopulationArguments`, with a test.

**Third two-instance run, on the hardened build** (same harness, 8,000 cycles, keyframes forced
to 1 min, forced desync at joiner cycle 4,500). Five full states; the event keyframe waited for
the 60 s floor; the joiner's quiet repair request was refused by it; the popup round trip
succeeded again; 51 relation lists passed the header check; 0 mutation failures, no clock
sample ignored, no delta refused, no unsolicited state held, no summary dropped:

```
host    # net[host]: full state #2 tick=2133 (keyframe after empire 2 eliminated, empire 4 eliminated, empire 6 eliminated)
host    # net[host]: resync request ignored -- 27 s since the last full state (floor 60 s); 1 ignored so far
host    # net[host]: full state #4 tick=5555 (desync popup: attempt resync)
joiner  # desync: RESYNC SUCCEEDED -- structure agrees at host tick 5700
joiner  [totals: ... colonyextras=446 designcosts=3,684 ... money=152 relations=51 ... vitals=2,257] [mutation failures=0 distinct=0]
```

That run shows an honest host is not hindered. Nothing in it was hostile: the limits
themselves are exercised by the tests, not by a run.

Not done: a session memory budget for adoptions (follow-up, above); a local-collection bound
on sections that CREATE members (ship, colony, design, character and fleet additions), which
have only the bytes-left bound because a creation is by definition not in the local
collection.

(2026-11-11: the session memory budget is built; see the next section.)


---

## Adoption memory: what leaks, measured -- 2026-11-11

Built on `fix/adoption-memory-1111`. Wire protocol unchanged (net protocol **4**).

The question was "what holds the old galaxy alive after an adoption". **Nothing does.** The
growth is graphics memory that DW2's renderer allocates again for every galaxy it is given and
never frees. Two two-instance runs on one PC (`Scripts\net-twoinstance.ps1 -Cycles 16000`,
keyframes forced to one a minute, 30 stars, 16 empires, nine adoptions each).

### How it was measured

`src\Dw2Mp\AdoptionMemory.cs` logs one reading per adoption and a second one 20 s later
("SETTLED": the renderer rebuilds on later frames, so the cost is not visible at once). One
process number cannot separate the causes, so each reading carries all of them:

| Number | What it is | Growing means |
|---|---|---|
| `managed` / `managedAfterCollect` | the GC heap; the second is `GC.GetTotalMemory(true)` | objects are retained |
| `gcCommitted` | what the collector has committed from Windows | the collector is holding freed memory (DW2 runs **server GC**, 24 heaps) |
| `other` | private bytes minus `gcCommitted` | native memory: Stride, the graphics driver |
| `textures`, `buffers` | Stride's own counters (`GraphicsDevice.TextureMemory`, `BuffersMemory`) | graphics resources nobody disposed |
| `private`, `workingSet` | the process as Windows sees it | |

The developer probe (`DW2MP_ADOPTION_MEMORY_PROBE=<first adoption>` with `DW2MP_DEV=1`) adds
forced-collection readings BEFORE and AFTER each adoption, a count of replaced galaxies still
alive (weak references), a census of ~3,600 collection sizes reachable from the game's statics
and renderer, and a list of the graphics resources created since the previous adoption.

### Before and after each adoption

Run 1, adoptions 1 to 4, nothing forced (what a player's game does), settled readings, MB:

| Adoption | private | workingSet | gcCommitted | other |
|---:|---:|---:|---:|---:|
| 1 | 5,434 | 2,600 | 576 | 4,858 |
| 2 | 5,496 (+62) | 2,631 | 593 (+17) | 4,903 (+45) |
| 3 | 5,545 (+49) | 2,650 | 600 (+7) | 4,945 (+42) |
| 4 | 5,668 (+123) | 2,707 | 631 (+31) | 5,037 (+92) |

Run 1, adoptions 6 to 9, `GC.GetTotalMemory(true)` before and after, MB:

| Adoption | managed BEFORE | managed AFTER | managed SETTLED | workingSet BEFORE / AFTER / SETTLED | textures | buffers | other |
|---:|---:|---:|---:|---|---:|---:|---:|
| 6 | 314 | 340 | 312 | 2,581 / 2,589 / 2,572 | 2,373 | 119 | 5,079 |
| 7 | 314 | 340 | 312 | 2,529 / 2,558 / 2,553 | 2,405 | 126 | 5,120 |
| 8 | 314 | 340 | 312 | 2,538 / 2,577 / 2,567 | 2,437 | 133 | 5,164 |
| 9 | 315 | 338 | 312 | 2,480 / 2,533 / 2,539 | 2,469 | 140 | 5,207 |

Run 2, forced readings on every adoption, settled, MB:

| Adoption | managedAfterCollect | gcCommitted | other | private | workingSet | textures | buffers |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 332 | 482 | 5,329 | 5,811 | 2,760 | 2,449 | 101 |
| 2 | 333 | 483 | 5,375 | 5,859 | 2,775 | 2,481 | 108 |
| 3 | 334 | 450 | 5,425 | 5,875 | 2,758 | 2,513 | 115 |
| 4 | 334 | 477 | 5,462 | 5,938 | 2,788 | 2,545 | 122 |
| 5 | 335 | 473 | 5,507 | 5,980 | 2,795 | 2,577 | 128 |
| 6 | 339 | 483 | 5,554 | 6,036 | 2,811 | 2,609 | 135 |
| 7 | 339 | 474 | 5,598 | 6,071 | 2,814 | 2,641 | 142 |
| 8 | 339 | 506 | 5,645 | 6,151 | 2,855 | 2,673 | 149 |
| 9 | 339 | 466 | 5,685 | 6,152 | 2,819 | 2,705 | 156 |

What the tables say:

- **The managed heap is flat**: 312 MB after every adoption in run 1, 332 to 339 MB across run
  2. A galaxy is about 26 MB of managed objects (AFTER minus SETTLED), and it is collected.
  Of all the galaxies replaced, exactly one was still alive after a forced collect in every
  reading: the joiner's own throwaway galaxy, the one the first adoption replaces. That is a
  one-off 26 MB, not growth, and it was not chased.
- **`textures` grows by exactly 32 MB and `buffers` by 7 MB per adoption**, every adoption, in
  both runs. `other` grows 37 to 50 MB with them (about 45), and private bytes follow: +341 MB
  over eight adoptions in run 2, 43 MB each.
- **The collector's share is slack, not a leak**: unforced, `gcCommitted` drifted up 7 to 31 MB
  an adoption; one aggressive collect (40 ms) gave back 122 MB of it and 2 MB of `other`.

### Root cause: DW2 rebuilds its render systems for every galaxy and disposes none

The resource list was identical after every adoption: **63 new graphics resources, 2 gone**:

```
x17  RootSignature
x16  Buffer`1
x17  Buffer 224B to 288B flags=ConstantBuffer
x8   PipelineState
x2   Texture 2048x2048 B8G8R8A8_UNorm mips=1 flags=ShaderResource, RenderTarget
x3   Buffer 288144B / 293808B flags=IndexBuffer
```

Read in the game's IL with `dw2inspect`:

- `DWRendererBase.SetGalaxyAndPlayer` (called for every renderer by
  `ScaledRenderer.InitializeGalaxy`, which `DWGame.StartGameExisting` calls) sets
  `ForceGraphicsInitialization`.
- On the next frame `DWRendererBase.CheckInitializeGraphics(forceReinitialization: true)`
  assigns a **new** instance to every galaxy-bound render system: `_NebulaCloudSystem`,
  `_GalaxyBackgroundSystem`, `_TerritoryMapSystem`, `_LongRangeScannerSystem` and
  `_ExplorationDiplomacyOverlaySystem` (both `MapOverlaySystem`), `_SymbolSystem`,
  `_FlowSystem`, `_TravelVectorSystem`, `_FleetOverlaySystem`, `_AttackVectorSystem`. The
  instances it overwrites are not disposed, and none of these classes disposes anything.
- `MapOverlaySystem.Initialize` alone creates a `SpriteBatch`, a `BackBufferSize` (2048) square
  render target, ~~two effect instances~~ and a plane's vertex and index buffers. Two of them are
  the two 16 MB textures: **32 of the 39 MB counted by Stride**. (corrected 2026-11-11, same
  day: the two effect instances are **not** created per overlay system. They are the statics
  `EffectHelper._BasicTextureEffect` and `_BasicTextureEffectLRS`, obtained once and shared by
  every `MapOverlaySystem`, so they must never be disposed with a discarded one. See "Overlay
  release" below.)
- Stride keeps every graphics resource in `GraphicsDevice.Resources` and a graphics resource
  has no finalizer, so one nobody disposes is never released. The census showed that set
  growing by 61 at every adoption, and nothing else growing with it.

This is DW2's own behaviour: loading a save from inside a running game takes the same path and
leaks the same 45 MB. A single-player session does it a handful of times; a joiner does it at
every keyframe. At the default 5 minutes that is about 540 MB an hour.

**One wrong theory, recorded so it is not tried again.** `DrawingHelper.GenerateTextureTinted`
(called by `Empire.LoadFlagIfNecessary` for an empire whose secondary colour is not its race's
default) really does leave a sprite batch and a depth buffer undisposed, and 15 tinted flags at
4 resources each would have been 60 of the 61. A build that handed each empire's existing flag
to the same empire in the next galaxy (so DW2 built none) was run from adoption 5 of run 2:
16 flags carried, DW2 built 0, and the adoption still created the same 63 resources. No empire
in the harness galaxy is tinted. The flag code was removed again; that leak is real in the IL
and unmeasured here.

### What was built: the measurement, and a session memory budget

- **`AdoptionMemory`** replaces the old `MemoryReport`, which forced a compacting collect in a
  player's game on every fifth adoption. A player's game now forces nothing: one line at the
  adoption, one settled line 20 s later, with the change since the previous adoption.
- **Session memory budget** (`src\Shared\SessionMemoryBudget.cs`, tested): the first settled
  reading after the join is the baseline. When private bytes have grown **1.5 GB** over it, the
  joiner gets **one** in-game notice recommending that the host saves and both restart, and the
  log says `*** SESSION MEMORY BUDGET ***`. It never blocks an adoption: a refused keyframe
  leaves two players in two galaxies, which is worse than a large process.
  `DW2MP_MEMORY_BUDGET_MB` overrides the limit (clamped to 16 MB to 64 GB; the lobby strips it
  from a real session like every other variable it does not set). Seen in run 2 with the limit
  at 64 MB: the notice was queued and shown at the fourth adoption.
- New dev flags, all needing `DW2MP_DEV=1` and all logged as `*** DEV FLAG LIVE ***`:
  `DW2MP_ADOPTION_MEMORY_PROBE=<n>` (the probe from adoption n on) and
  `DW2MP_ADOPTION_MEMORY_AGGRESSIVE_AT=<n>` (one aggressive collect at adoption n's settled
  reading, to see how much the collector was holding).

### Not done

- **The leak itself is not released in this commit.** (2026-11-11, later the same day: the 32 MB
  of render targets now are, on `fix/overlay-release-1111`; see "Overlay release" below. The
  other systems' buffers still are not.) The recipe the evidence points to: a
  Harmony prefix and postfix on `DWRendererBase.CheckInitializeGraphics` that notice which
  system fields were given a new instance, then, some seconds later on the main thread,
  dispose the discarded `MapOverlaySystem.BackBuffer` textures (32 of the 45 MB) and after that
  the other discarded systems' buffers. `CustomOrbRenderer` and `DWRendererBase` read those
  render targets through the renderer's fields, so a discarded instance should no longer be
  drawn; that "should" needs a run of its own before it ships, because a texture disposed while
  something still draws it can end the joiner's game.
- The one surviving throwaway galaxy (26 MB, once) was not traced.
- The 48 MB buffer `NetSession.Decompress` allocates for each full state was left alone: it is
  collector slack, it is given back, and that path was security-reviewed the day before.
- Nothing was tried on the late-game save, with DLC content, or between two machines.

## Overlay release: the 32 MB of textures per adoption, released -- 2026-11-11

Built on `fix/overlay-release-1111`, the follow-up the section above asked for. Wire protocol
unchanged (net protocol **4**); nothing in the simulation is read or written.

### What it does

`src\Dw2Mp\OverlayRelease.cs`, joiner only. A Harmony prefix and postfix on
`DWRendererBase.CheckInitializeGraphics(bool)` compare the renderer's two `MapOverlaySystem`
fields (`_LongRangeScannerSystem`, `_ExplorationDiplomacyOverlaySystem`) before and after DW2's
own code. An instance that was replaced goes into a waiting list
(`src\Shared\DiscardedResourceQueue.cs`, pure, tested). After **10 seconds and 60 main-thread
updates**, on the main thread, its `BackBuffer` (the 2048x2048 render target, 16 MB) is disposed.

Guards, in the order they apply:

- The patch is not applied while `CheckInitializeGraphics` still has DW2's four-byte stub body
  (Harmony copies the body it finds; patching a stub would replace the real method with an empty
  one). It is retried once per update until the body is real, and given up after 7,200 updates.
- An instance that is in either field of any renderer seen is never touched: it is dropped from
  the list, and noticed again if DW2 discards it again.
- A texture that a live overlay system also holds is not disposed. Nor is one already disposed.
- The discarded instance gets `Enabled = false` first. Both callers of `MapOverlaySystem.Draw`
  check `Enabled`, so if the proof below were ever wrong, the old overlay would draw nothing
  rather than draw into a disposed render target.
- The first exception anywhere is logged once (`# overlay: *** FAILED ...`) and the release stops
  for the rest of the session. That leaves DW2's own behaviour: the leak.
- The list holds at most 64 instances; past that a replacement is counted and not queued.

### Why the old render target is no longer referenced (read in the IL, game 1.3.8.4)

A sweep of every method body in the four `DistantWorlds*` assemblies (1,904 types, 26,657
methods, with a scratch tool built on the `dw2inspect` loader and IL printer) for
`MapOverlaySystem`, the two field names, `ObtainEffectBasicTexture` and `BasicTextureKeys.Image`:

- The two renderer fields are the **only** fields of type `MapOverlaySystem` in the game, and no
  compiler-generated closure captures one. They are written only in `CheckInitializeGraphics`
  (`newobj MapOverlaySystem::.ctor` then `stfld`).
- They are read only in `CheckInitializeGraphics`, `DWRendererBase.UpdateMapOverlaySystems` and
  `CustomOrbRenderer.DrawCorePreSceneInternal` / `DrawCorePostSceneInternal`. Each of those loads
  the field again for every use (`ldarg.0; ldfld`); none keeps an instance in a local across
  frames. An overwritten instance is therefore unreachable from the game's code.
- `MapOverlaySystem.BackBuffer` is written only by `Initialize` (`Texture.New2D`, a new texture
  every time) and read only by that instance's own `UpdateOverlay` and `Draw`.
- `Draw` hands `BackBuffer` to an effect's parameters (`BasicTextureKeys.Image`). **Those two
  effects are shared**: `EffectHelper.ObtainEffectBasicTexture` and `...LRS` return the statics
  `_BasicTextureEffect` and `_BasicTextureEffectLRS`, and only `MapOverlaySystem.Initialize`
  obtains them. So after an adoption a shared effect can still name the old texture, and a
  disposed one once it is released. That stale entry is never drawn: the only code that applies
  those effects is `MapOverlaySystem.Draw`, which sets `Image` to its own `BackBuffer` first.
  It is also why the effects themselves are **not** disposed with a discarded instance.
- `MapOverlaySystem.Draw` itself skips a `BackBuffer` that is null or `IsDisposed`.
  `UpdateOverlay` does not, which is the reason for the `Enabled = false` above.

What the IL of DW2 cannot show is what Stride does with a disposed texture that is still named
in a parameter collection nobody applies. The run below is the evidence for that part.

### Measured: one two-instance run, seven adoptions

`Scripts\net-twoinstance.ps1 -Cycles 12000`, keyframes one a minute, the memory probe on from
adoption 1 (`DW2MP_DEV=1`, `DW2MP_ADOPTION_MEMORY_PROBE=1`), both windows minimised. Settled
readings on the joiner, MB. "Before" is run 2 of the section above (same harness, same settings).

| Adoption | textures before | textures after | buffers before | buffers after | private before | private after |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 2,449 | 2,421 | 101 | 110 | 5,811 | 5,728 |
| 2 | 2,481 (+32) | 2,421 (+0) | 108 (+7) | 117 (+7) | 5,859 | 5,766 |
| 3 | 2,513 (+32) | 2,421 (+0) | 115 (+7) | 124 (+7) | 5,875 | 5,830 |
| 4 | 2,545 (+32) | 2,421 (+0) | 122 (+7) | 131 (+7) | 5,938 | 5,756 |
| 5 | 2,577 (+32) | 2,421 (+0) | 128 (+6) | 138 (+7) | 5,980 | 5,826 |
| 6 | 2,609 (+32) | 2,421 (+0) | 135 (+7) | 145 (+7) | 6,036 | 5,763 |
| 7 | 2,641 (+32) | 2,421 (+0) | 142 (+7) | 152 (+7) | 6,071 | 5,847 |

- **Stride textures: +32 MB per adoption before, +0 MB after.** Every release logged
  `Stride textures 2,453 MB -> 2,421 MB`; 14 render targets released in seven adoptions, none
  skipped, the first pair being the ones built for the joiner's own throwaway galaxy.
- Buffers still grow 7 MB an adoption: nothing was done about them (see below).
- Private bytes went from +43 MB an adoption (5,811 to 6,071 over six) to about +20 (5,728 to
  5,847 over six). That number is noisy from one adoption to the next (-74 to +84) because the
  collector's committed memory moves with it; `other` (private minus collector) went from +45 an
  adoption to +22. At the default five-minute keyframe that is roughly 240 MB an hour instead
  of 540.
- No `# overlay: *** FAILED` line, no exception and no failed sync check in either log, and no
  desync popup. Both games reached `# run complete` and left on their own with the session
  lock removed; the harness exited 0. No "Send Error Information" window was found at the
  three checks made while the games ran.

**A second, shorter run on the committed build** (`-Cycles 5500`, three adoptions). The first
run was made before the stub-body guard was added, so the build that is committed was run
again: `CheckInitializeGraphics body 2613 bytes, patched after 0 wait(s)` (DW2 had already
initialised the renderer type when the mod installed, so the waiting path was **not**
exercised), textures 2,278 MB at all three settled readings, buffers 101, 108, 114, private
5,504, 5,512, 5,510, six render targets released, no failure line, both games left on their
own. The texture level differs between runs (2,421 and 2,278 here, 2,373 and 2,449 in the
section above); within a run it no longer moves.

### The off switch

`DW2MP_OVERLAY_RELEASE=0` switches the release off (`# overlay: OFF` in the log). Only `0` does;
unset or anything else leaves it on. It is on the lobby's allow-list (`LaunchEnvironment`),
unlike a dev flag: it is local, it changes nothing in the simulation or on the wire, and a
player whose graphics driver disliked the release would otherwise have no way to turn it off
short of `DW2MP_DEV=1`, which enables every dev flag. The off path was not run in the game.

### Not done

- **Nobody looked at the picture.** The harness runs both windows minimised. "No render errors"
  above means no exception, no failure line and a normal exit; it does not mean the long-range
  scanner and exploration overlays were seen drawing correctly after a release, and the log does
  not say whether either overlay was switched on during the run. That wants one attended session
  with both overlays on across an adoption.
- **The other 7 MB of buffers per adoption**, and the 61 small resources (root signatures,
  pipeline states, constant buffers) are still DW2's leak. The same proof was not done for the
  other eight systems `CheckInitializeGraphics` re-creates (`_NebulaCloudSystem`,
  `_GalaxyBackgroundSystem`, `_TerritoryMapSystem`, `_SymbolSystem`, `_FlowSystem`,
  `_TravelVectorSystem`, `_FleetOverlaySystem`, `_AttackVectorSystem`): each needs its own sweep
  for who else holds its buffers, and four of them are handed out through property getters
  (`get_FlowSystem`, `get_TravelVectorSystem`, `get_AttackVectorSystem`,
  `get_FleetOverlaySystem`), which the overlay systems are not.
- **A discarded overlay system's `SpriteBatch` and plane vertex/index buffers** are not disposed.
  They are small, `Draw` binds the plane buffers before its disposed-check, and whether a Stride
  `SpriteBatch` shares its buffers with other batches of the same size was not read.
- Host side: not installed. The host re-creates its systems once, when it loads the save.
- One PC, one GPU and driver, the 30-star harness galaxy. Not tried on the late-game save, with
  DLC content, or between two machines.

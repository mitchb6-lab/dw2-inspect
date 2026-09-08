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

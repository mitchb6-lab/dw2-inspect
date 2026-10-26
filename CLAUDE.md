# CLAUDE.md — dw2-inspect

A CLI that reads the code inside **Distant Worlds 2** (Stride / .NET 8). One project,
`src/Dw2Inspect`, no dependencies beyond the .NET 9 SDK.

## The one thing to know

The game's assemblies ship with **method bodies stripped** — 22,527 of ~26,000 methods in
`DistantWorlds.Types.dll` are the four bytes `00 00 00 2A` (`nop nop nop ret`). Bodies are
restored by an obfuscated dispatcher invoked from each type's static constructor.

So `Loader.Prepare(type)` — `RuntimeHelpers.RunClassConstructor` — is the load-bearing
line in this codebase. Call it before reading any body. After it, plain
`MethodBase.GetMethodBody().GetILAsByteArray()` returns real IL.

**Two approaches that look right and are not:**
- Reading IL length from the PE file. Everything reads as 4 bytes, implemented or not.
- JIT-compiling and disassembling native code. Every method fronts an identical
  ~85-instruction tiered-compilation thunk, so you measure the thunk.

## Layout

| File | Holds |
|---|---|
| `Program.cs` | CLI parsing, usage text, exit codes (0 ok, 1 error, 2 bad command) |
| `Commands.cs` | The verbs: types, find, members, enum, strings, il, refs |
| `Loader.cs` | Assembly loading, dependency resolution, the static-constructor trick |
| `IlPrinter.cs` | IL decoding and metadata-token resolution |
| `GameLocator.cs` | Finds the install via `--game`, `DW2_PATH`, or `libraryfolders.vdf` |

## Conventions

- **Read-only, always.** The game's DLLs are inputs to this process. Never write to the
  game directory, never inject, never patch. If a feature would need that, it does not
  belong here.
- **Warnings to stderr**, results to stdout, so redirecting gives a clean dump.
- **Report failure honestly.** Some types (`Galaxy`) throw during initialisation outside
  the game host; say so and show the stub marker rather than printing nothing.
- Nested types matter — async bodies, iterator bodies and lambdas all live in generated
  nested classes. `strings` recurses into them and `il` follows state machines by default.

## Findings

`docs/findings.md` records what has been learned with it, including why DW2 multiplayer
cannot be modded in and why setting `PlayMode = Multiplayer` breaks the game.

## Readable code -- mitch, 2026-10-26 (applies to every language here)

Code must be readable and debuggable by a human, so mitch and outside programmers can troubleshoot it
by hand. This came from an experienced C++/Lua programmer who offered to help and declined because of
dense one-liner code.

- **One statement per line.** Never chain statements with `;`; never put a loop, a conversion and an
  early return on one line.
- **One assignment per line.** No multi-target tuple assignments such as
  `a.x, a.y, a.z, a.ttl = q.x, v[1], v[2], TTL[q.kind]`.
- **Descriptive names** that say what a thing is: `API`, `projectile`, `query`, `velocity` -- never
  `a`, `q`, `m`, `e`, `s` (short loop indices such as `i` are fine). Declare a variable where its block
  starts, not in the middle of a line.
- Example: not `local ffi=require('ffi');local C=ffi.C;local s=A.state`, but one line each, and
  `local API = X4LAN_BUBBLE_API`, not `local A = X4LAN_BUBBLE_API`.
- **When editing a file that has dense lines, unpack the ones in the function you touch.**
- **Agents too:** every brief to a programmer, art or other agent repeats this rule.

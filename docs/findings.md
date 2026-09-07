# Findings

Notes from using this tool on **Distant Worlds 2 v1.3.6.3** (Steam appid `1531540`).

---

## 1. What the game is built on

- **Stride** (open-source C# engine, formerly Xenko) on **.NET 8** — about 35
  `Stride.*.dll` assemblies, `BulletSharp` for physics, SharpDX and Silk.NET below that.
- Game logic lives in four assemblies: `DistantWorlds.Types.dll` (650 types, the
  simulation), `DistantWorlds2.dll`, `DistantWorlds.Core.dll`, `DistantWorlds.UI.dll`.
- `DistantWorlds2.exe` is only the native apphost stub; everything is managed.
- Assemblies are protected: method bodies stripped at rest, restored by a static-constructor
  dispatcher (`hIDAYxH6DWLHIthQgC::UsGqDsZitN`, ~31 KB), plus renamed helper types.

## 2. The three modding tiers

| Tier | Supported? | Notes |
|---|---|---|
| **Data** | Yes, first class | `data/` holds 174 plain XML files plus `GameText.txt`. A mod is `<gamedir>/mods/<Name>/mod.json` with override files. |
| **Asset bundles** | Yes | `.bundle` files are Stride content bundles. `Stride.Core.Assets.CompilerApp.dll` ships with the game. |
| **Code** | **Yes — via `--low-level-inject`, not via the mod manager** | See below. |

`DwModSupport` is the whole mod system: `GameModsDialog`, an ordered enable list in
`mods.json`, order profiles, preview images, and a built-in Steam Workshop publisher
(`PublishSteamMod`, `SyncSteamMods`).

**Its entire content surface is `ListDataFiles` and `ListModBundles`** — no assembly or
plugin loading anywhere in it; every `LoadFrom*` reachable from it is XML
deserialisation. So a code mod cannot be shipped as a mod-manager mod, cannot be
enabled from the Modifications dialog, and cannot be distributed through Steam Workshop.

**But the game ships its own injector.** `DWCommandLineArgs.LowLevelInjections` is
`--low-level-inject`, documented in its own attribute as *"Inject a 3rd party library
and/or invoke an export on it"*, and implemented in `DistantWorlds.Core.ModHelpers`.
Decompiled, it works like this:

```
--low-level-inject "<path>[!<entrypoint>]"        (repeatable)
```

1. Split on the **last `!`**. The path may be relative (resolved against the current
   directory) or absolute; it is `Path.GetFullPath`-ed and must exist, or the request is
   silently ignored.
2. `AssemblyName.GetAssemblyName(path)` decides the kind: success means managed, a
   `BadImageFormatException` means native, and native is only attempted when the caller
   passes `supportUnmanaged`.
3. **Managed with no `!`** — `AssemblyLoadContext.Default.LoadFromAssemblyPath(path)`
   then `RunModuleConstructor`. Your assembly's **module initializer** runs inside the
   game process. This is the clean hook.
4. **Managed with `!Something`** — if `Something` resolves as a type, its **static
   constructor** is run; otherwise it is split at the last `.` into type and method, and
   the method is invoked.

That, plus `0Harmony.dll` already sitting in the game directory, makes Harmony patching
of game code entirely practical. Note 0Harmony is *not* referenced by the shipping
assemblies (`dw2inspect refs Harmony` returns nothing) — the game's own
`DistantWorlds2App.ApplyHarmonyPatches` loads it reflectively for its own use, and it is
there for mods to use too.

**The distinction that matters:** code modding is supported by the *game*, not by the
*mod system*. It needs a launch flag, so it cannot be shipped to players through
Workshop the way a data mod can.

## 3. Multiplayer does not exist, and the flag that looks like a switch is a trap

The metadata is genuinely misleading. All of this is real and readable:

- `PlayMode { Singleplayer = 0, Multiplayer = 1 }`, Stride-serialised, carried on
  `Galaxy`, `GameStartSettingsEmpire`, `GameClient`, `GameServer`, and even
  `UI.StartNewGameDialog`.
- `GameServer` (`UpdateGameAsServer`, `SendMessageToAllClients`, `AdjustClientBlockSizes`;
  fields `NetAddress`, `GameClients`, `InputQueue`).
- `GameClient` (`UpdateGameAsClient`, `SendClientCommands`, `SendMessageToServer`,
  `GetServerTimeDirect`, and `LastServerTime` / `LocalTimeAtLastServerTime` clock sync).
- `MessagePacket` with `ReadFromStream` / `WriteToStream`, `SerialNumber`, `SenderEmpireId`.
- `GameTaskType` — an enum with about **205 command values**, the complete vocabulary a
  command-forwarding netcode would need.

Decompiling settles it. Both send paths branch on `PlayMode`, and the branch runs the
**opposite** way to expectation:

```
GameClient::SendMessageToServer(MessagePacket message)
   ldfld    GameClient.PlayMode
   brfalse  IL_0012          <- Singleplayer (0) jumps to the actual work
   ldloc.0 / ldc.i4.1 / pop / pop
   ret                       <- Multiplayer (1) returns, doing nothing
 IL_0012:
   ... Server.InputQueue.TryAdd(message.SerialNumber, message)
```

The Multiplayer branch body was **stripped out**, leaving a dead comparison and an
immediate `ret`. `GameServer::SendMessageToAllClients` has the identical shape.

**Consequence: setting `PlayMode = Multiplayer` does not enable multiplayer. It disables
the message queue that single-player depends on.** It breaks the game.

Three more confirmations:

- **The "network" is a dictionary.** The client `TryAdd`s into `GameServer.InputQueue`,
  a `ConcurrentDictionary<int, MessagePacket>`; the server writes back to
  `GameClients[0].InputQueue` — **index 0, never a loop.** One client, always.
- **It is a work partitioner, not netcode.** Combined with `AdjustClientBlockSizes` and
  `TimeToProcessLastShipBlockMilliseconds`, this is the DW1-inherited architecture for
  splitting one simulation into sized blocks of ships and locations.
- **`NetworkHelper` is a misnomer.** `SendData(string)` is an `async void` HTTP POST to
  `http://www.slitherine.com/dev_feed/distant_worlds_2/feed.php`. Telemetry, not transport.
  (`dw2inspect strings NetworkHelper` shows this in one line.)

There is no transport anywhere — no listener, no connection manager, no handshake, no
state sync for a joining client. And Steam integration is **Workshop/UGC only**: no
`SteamMatchmaking`, no lobbies, no `SteamNetworkingSockets`, no P2P.

**Conclusion:** the `Multiplayer` enum value and the 205-entry command vocabulary are the
fossil of an intention, not a disabled feature. Adding multiplayer would mean writing the
entire netcode against a commercially protected binary.

What it would actually take, in order of difficulty:

1. **A transport** — none exists. `Facepunch.Steamworks` is already loaded in-process, so
   `SteamNetworkingSockets` is reachable even though the game never calls it.
2. **Reinstate the stripped branches** — Harmony-patch both send paths. The wire format
   is already there: `MessagePacket.ReadFromStream`/`WriteToStream` are real,
   implemented methods (205 and 214 IL bytes).
3. **A second client slot** — `SendMessageToAllClients` writes to `GameClients[0]` and
   never loops.
4. **Initial state sync** — a joining player needs the whole galaxy, and there is no path
   for that short of the save format.
5. **Determinism — this is the one that kills it.** Lockstep needs both machines to
   simulate bit-identically. DW2 is `float`/`double` throughout (`IncomeFactors` is a
   `Single[]`), and .NET floating point is not guaranteed identical across CPUs and JIT
   versions. The alternatives are fixed-point throughout, or an authoritative server with
   state reconciliation. Both are rewrites of the simulation, not patches to it.

Delivery is **not** on that list, because `--low-level-inject` solves it (§2). It is
worth being precise about that: the obstacle is arithmetic, not access.

## 4. Enabling a mod, and the command line

`mods/mods.json` holds `{"order":[...]}`, and **that array is the enabled set, not just
a sort order** — `EnableModInternal` appends the mod id to it and `DisableModInternal`
splices it out by index. A freshly created `mods.json` is `{"order":[]}`, meaning every
listed mod is *installed but off*. Mod ids are `mods/<Name>` or `steam/<workshopId>`.

Mods **replace** same-named data files rather than merging, so two mods shipping the
same filename conflict outright and the load order decides. Checking for collisions is
just comparing filenames between mod folders.

`DWCommandLineArgs` (option names recovered from the attribute blobs, since they are
metadata rather than IL string literals):

| Flag | Purpose |
|---|---|
| `--tool-mode` | Non-game tooling mode |
| `--ugc-publish` | Publish a mod to Steam Workshop |
| `--ugc-id` | Workshop item id to publish to |
| `--ugc-log` | Workshop changelog text |
| `--ugc-dont-open` | Do not open the Workshop URL afterwards |
| `--ugc-sync` | Sync subscribed Workshop mods |
| `--gen-xsd` | **Generate XSD schemas for the data files** |
| `--new-game`, `--skip-splash`, `--non-interactive` | Startup control |
| `--use-dx11`, `--use-dxvk`, `--use-dxvk2` | Renderer selection |
| `--wait-for-debugger`, `--debug-graphics`, `--debug-fatal-exceptions` | Diagnostics |

`--gen-xsd` is the useful one for modding. Run it and the game writes **29 XSD schemas**
into `data/schema/` — `GovernmentList.xsd`, `ComponentDefinitionList.xsd`,
`ResearchProjectDefinitionList.xsd`, `ShipHullList.xsd`, `RaceList.xsd` and the rest —
with full element names, types, cardinality and enum definitions:

```xml
<xs:complexType name="EconomyFactorSet">
  <xs:all>
    <xs:element minOccurs="0" maxOccurs="1" name="IncomeFactors" type="ArrayOfFloat" />
    <xs:element minOccurs="0" maxOccurs="1" name="ExpenseFactors" type="ArrayOfFloat" />
  </xs:all>
</xs:complexType>
```

```
DistantWorlds2.exe --tool-mode --gen-xsd --non-interactive --skip-splash
```

This beats inferring structure from XML comments, and it is authoritative — the schemas
are generated from the same serialiser that reads the files at runtime. It only creates
a new `data/schema/` directory; nothing shipped is modified.

## 5. Method-body sizes on disk mean nothing

Worth recording as a methodology note, because it nearly produced a wrong conclusion.

The first pass measured IL length straight from the PE file and found every networking
method at 4 bytes — which reads as "unimplemented stub". But **22,527 methods across the
whole assembly are 4 bytes**, including ones that certainly work. Dumping the raw bytes
showed `00 00 00 2A`, i.e. the protector's placeholder.

A second wrong turn: forcing JIT compilation and disassembling the native code. Every
method fronts an identical ~85-instruction tiered-compilation thunk, so native size
measures the thunk, not the method.

The cheap reflection path — run the static constructor, then read `GetMethodBody()` — was
the one that worked.

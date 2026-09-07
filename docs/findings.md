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
| **Code** | **No** | See below. |

`DwModSupport` is the whole mod system: `GameModsDialog`, an ordered enable list in
`mods.json`, order profiles, preview images, and a built-in Steam Workshop publisher
(`PublishSteamMod`, `SyncSteamMods`).

**Its entire content surface is `ListDataFiles` and `ListModBundles`.** Searching every
type in all four assemblies finds **no assembly or plugin loading anywhere** — every
`LoadFrom*` in the game is XML deserialisation.

This is worth stating plainly because the install is misleading: it ships `0Harmony.dll`,
`Mono.Cecil`, the full Roslyn compiler and twelve `NuGet.*` assemblies, and
`DistantWorlds2.Windows.DistantWorlds2App.ApplyHarmonyPatches` runs at startup. That is
the game patching *itself*. `dw2inspect refs Harmony` returns nothing — 0Harmony is not
even a static reference. **A code mod cannot go through the in-game mod manager**; it
would need its own injector.

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

`--gen-xsd` is the useful one for modding: it emits schemas for the XML formats, which
beats inferring structure from comments.

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

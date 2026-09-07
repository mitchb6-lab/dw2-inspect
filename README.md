# dw2-inspect

A small command-line tool for reading the code inside **Distant Worlds 2**.

DW2 is a .NET 8 application built on the [Stride](https://www.stride3d.net/) engine,
so its game logic is managed IL with all its type, method and field names intact.
That should make it trivially readable — except that the shipping assemblies have
their **method bodies stripped**.

## The problem this solves

Point any normal decompiler at `DistantWorlds.Types.dll` and you get nothing. On
disk, **22,527 of its ~26,000 methods are literally four bytes**:

```
00 00 00 2A     nop / nop / nop / ret
```

The real bodies are restored at runtime by an obfuscated dispatcher that each
type's static constructor invokes.

The workaround is smaller than it sounds: **let the type initialise.** Call
`RuntimeHelpers.RunClassConstructor`, and from that point ordinary reflection —
`MethodBase.GetMethodBody().GetILAsByteArray()` — returns genuine IL.

| Method | IL on disk | IL after the static constructor runs |
|---|---:|---:|
| `NetworkHelper.SendData` | 4 | 43 |
| `MessagePacket.WriteToStream` | 4 | 214 |
| `GameClient.SendClientCommands` | 4 | 224 |
| `GameClient.UpdateGameAsClient` | 4 | 771 |

The game's DLLs are only ever **inputs** to this tool's own process. Nothing is
injected, nothing is patched, and nothing is written to the game directory.

## Build

Needs the .NET SDK 9 or later. Windows x64 only, because that is all the game ships.

```bash
dotnet build src/Dw2Inspect
```

The binary lands at `src/Dw2Inspect/bin/Debug/net9.0/win-x64/dw2inspect.exe`.

## Use

```
dw2inspect <command> [argument] [options]
```

| Command | Does |
|---|---|
| `types [substring]` | List type names, optionally filtered |
| `find <substring>` | Search type, method **and** field names |
| `members <Type>` | Methods and fields of a type |
| `enum <Type>` | Enum members with their values |
| `strings <Type>` | Every string literal in a type's methods, nested types included |
| `il <Type[::Method]>` | Decompile method bodies to IL; method may be `*` |
| `refs [substring]` | Assembly references of each game assembly |

Options: `--game <path>` (otherwise `DW2_PATH`, otherwise a Steam library scan),
`--no-follow`, `--quiet`.

### Examples

```bash
dw2inspect enum PlayMode
dw2inspect find Multiplayer
dw2inspect strings NetworkHelper
dw2inspect il GameClient::SendMessageToServer
dw2inspect il "DistantWorlds.Types.MessagePacket::*" > MessagePacket.il.txt
```

`strings` is the fastest way to orient yourself in an unfamiliar type — it is what
identified `NetworkHelper` as an HTTP telemetry poster rather than game networking.

`il` follows async and iterator methods into their generated state machines by
default, because the outer method is just a stub that starts one. `--no-follow`
turns that off.

## Known limits

- **Some types refuse to initialise outside the game host.** `Galaxy`, for one,
  throws `DirectoryNotFoundException` from its static constructor. When that
  happens the tool says so and its methods keep reading as `00 00 00 2A` stubs —
  reported honestly rather than silently returning nothing.
- **Output is IL, not C#.** It is readable enough to answer "what does this
  actually do", which is the point, but it is not a source reconstruction.
- Warnings go to **stderr**, so redirecting stdout gives you a clean dump.

## Scope

This reads a game the user owns, in their own process, to understand its data and
rules. It does not modify the game, defeat licensing, or redistribute any game
code. Findings from it are in [`docs/findings.md`](docs/findings.md).

## Licence

Apache-2.0. See [LICENSE](LICENSE).

# Pirate Economies

A data mod for Distant Worlds 2. Gives the nine pirate governments economies that
match their names.

## Why

DW2 ships nine pirate governments with distinct names, leaders and flavour —
**Smugglers** led by a Chief Trader, **Raiders** led by a Chief Raider,
**Mercenaries** led by a Bounty Hunter, plus five pirate-empire governments like
the **Trade Guild** and the **Banditocracy**.

Every one of them has the identical set of income factors, and every factor is
exactly `1.0`. A Smuggler earns from raiding exactly as well as a Raider does.

```
32750  Pirates              Tribute=1  Raiding=1  Missions=1  Crime=1  Trading=1
32751  Smugglers            Tribute=1  Raiding=1  Missions=1  Crime=1  Trading=1
32752  Raiders              Tribute=1  Raiding=1  Missions=1  Crime=1  Trading=1
...
```

The archetypes exist in name and in AI behaviour; they just don't exist in the
economy. This mod makes them.

## What it changes

Only `EconomicFactors/IncomeFactors` on the nine pirate governments — 34 numbers.
Nothing else in the file is touched.

| Government | Leans toward | Leans away from |
|---|---|---|
| **Pirates** (Balanced) | *unchanged — the control* | |
| **Smugglers** | Resource trading ×2, fuel ×1.75, trade bonuses ×1.75 | Raiding ×0.5 |
| **Raiders** | Raiding ×2, tribute ×1.5 | Trading ×0.5, trade bonuses ×0.5 |
| **Mercenaries** | Missions ×2, information ×1.5 | Raiding ×0.75 |
| Fringe Confederacy | Tribute ×1.5 | Colony tax ×0.85 |
| Trade Guild | Trading ×1.5, trade bonuses ×1.5 | Raiding ×0.75 |
| Banditocracy | Crime ×1.5, raiding ×1.5 | Trading ×0.75 |
| Frontier Authority | Missions ×1.5, colony tax ×1.25 | Crime ×0.75 |
| Free Anarchy | Crime ×1.5, raiding ×1.25 | Colony tax ×0.75 |

The strongest specialisation is `2.0` against a `0.5` weakness — deliberately an
edge, not a wall. The intent is that an archetype feels like it earns differently,
not that the others become unplayable. **Balanced is left entirely alone** so there
is something to measure the rest against.

Indices are `IncomeType` ordinals verified against the enum in
`DistantWorlds.Types`, not guessed from the XML comments. `EconomyFactorSet.GetIncomeFactor`
indexes the array directly and returns `1.0` past its end, so anything unlisted is
untouched.

## Build and install

```powershell
.\build.ps1
```

It finds the game (or takes `-GamePath`), applies the table to a copy of the base
file, and writes `<game>\mods\PirateEconomies\`. Nothing in the install is modified —
the only thing created is that new folder.

Then: **Main menu → Game Mods → tick "Pirate Economies"**.

## Why this is a generator, not a checked-in XML

The mod loader (`DwModSupport.ListDataFiles`) overlays an enabled mod's root
directory onto the game's `/data` mount, so a same-named file **replaces** the base
file outright — there is no merging. That means any data mod has to carry a full
copy of a game file, and will silently revert or conflict when the game is patched.

Shipping the generator instead of the output makes that recoverable: re-run
`build.ps1` after a game update and the 34 edits reapply to the new base file. The
script fails loudly if a government ID it expects has gone missing, rather than
quietly producing a mod that changes less than it claims.

It also preserves the base file's mixed CRLF/LF line endings and omits a BOM, so
`diff` against the game file shows only the 34 changed values.

## Tuning

The whole design is one table at the top of `build.ps1`. Edit the numbers, re-run.

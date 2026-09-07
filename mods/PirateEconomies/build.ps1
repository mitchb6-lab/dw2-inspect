<#
.SYNOPSIS
    Builds the "Pirate Economies" mod for Distant Worlds 2.

.DESCRIPTION
    DW2 ships nine pirate governments whose names promise different economies --
    Smugglers, Raiders, Mercenaries, Trade Guild, Banditocracy -- and gives every
    one of them the identical set of income factors, all exactly 1.0. This mod
    gives each an economy that matches its name.

    The mod REPLACES data/Governments_Pirate.xml rather than patching it, because
    that is how the game's mod loader works: DwModSupport.ListDataFiles overlays an
    enabled mod's root directory onto the /data mount, so a same-named file wins
    outright. That means the mod carries a full copy of a game file and will drift
    when the game is patched -- which is exactly why this is a GENERATOR rather
    than a checked-in XML. Re-run it after a game update and the edits reapply to
    the new base file.

    Nothing in the game install is modified. The only thing written is a new
    directory under <game>\mods\.

.PARAMETER GamePath
    The Distant Worlds 2 install directory. Defaults to $env:DW2_PATH, then a scan
    of the usual Steam locations.

.PARAMETER OutputPath
    Where to write the built mod. Defaults to <GamePath>\mods\PirateEconomies.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Distant Worlds 2"
#>
[CmdletBinding()]
param(
    [string] $GamePath,
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------------------
# The design.
#
# Indices are IncomeType ordinals, confirmed against the enum in
# DistantWorlds.Types: 1 ColonyTax, 2 ResourceTrading, 3 Fuel, 4 Information,
# 5 Tribute, 6 Raiding, 7 Missions, 8 ColonyCrime, 9 Tourism, 10 ShipBuilding,
# 11 Discoveries, 12 TradeBonuses, 13 PrivateColonyRevenue.
#
# EconomyFactorSet.GetIncomeFactor indexes the array directly and returns 1.0 for
# any index past its end, so unlisted factors are simply left alone.
#
# Values are deliberately EDGES, not walls: the strongest specialisation is 2.0
# against a 0.5 weakness. A pirate faction should feel like it earns differently,
# not like the other archetypes are unplayable.
# --------------------------------------------------------------------------
$Design = @{
    # --- The four pirate faction archetypes (these map onto PirateFactionType) ---

    # 32750 Pirates / Balanced -- deliberately untouched, as the control.

    32751 = @{   # Smugglers -- "Chief Trader". Contraband and fuel running.
        Name = 'Smugglers'
        Income = @{ 2 = 2.0; 3 = 1.75; 4 = 1.5; 12 = 1.75; 6 = 0.5; 5 = 0.75 }
    }
    32752 = @{   # Raiders -- "Chief Raider". Takes it rather than trades it.
        Name = 'Raiders'
        Income = @{ 6 = 2.0; 5 = 1.5; 2 = 0.5; 12 = 0.5; 4 = 0.75; 7 = 0.75 }
    }
    32753 = @{   # Mercenaries -- "Bounty Hunter". Paid for jobs.
        Name = 'Mercenaries'
        Income = @{ 7 = 2.0; 4 = 1.5; 5 = 1.25; 6 = 0.75; 2 = 0.75 }
    }

    # --- The five pirate-empire governments: lighter leans, same directions ---

    32746 = @{   # Fringe Confederacy -- "Boss". Runs on tribute from below.
        Name = 'Fringe Confederacy'
        Income = @{ 5 = 1.5; 13 = 1.25; 1 = 0.85 }
    }
    32747 = @{   # Trade Guild -- "Trade Viceroy". Went legitimate, mostly.
        Name = 'Trade Guild'
        Income = @{ 2 = 1.5; 12 = 1.5; 3 = 1.25; 6 = 0.75 }
    }
    32748 = @{   # Banditocracy -- "Gangster King". Crime is the state.
        Name = 'Banditocracy'
        Income = @{ 8 = 1.5; 6 = 1.5; 5 = 1.25; 2 = 0.75 }
    }
    32749 = @{   # Frontier Authority -- "Kingpin". Order, for a fee.
        Name = 'Frontier Authority'
        Income = @{ 7 = 1.5; 1 = 1.25; 8 = 0.75 }
    }
    32730 = @{   # Free Anarchy -- "Pirate King". No taxes, no order.
        Name = 'Free Anarchy'
        Income = @{ 8 = 1.5; 6 = 1.25; 1 = 0.75 }
    }
}

$SourceFile = 'Governments_Pirate.xml'

# --------------------------------------------------------------------------

function Find-GamePath {
    param([string] $Explicit)

    $candidates = New-Object System.Collections.Generic.List[string]

    if ($Explicit)      { $candidates.Add($Explicit) }
    if ($env:DW2_PATH)  { $candidates.Add($env:DW2_PATH) }

    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        $candidates.Add((Join-Path $steam 'steamapps\common\Distant Worlds 2'))

        # Secondary libraries are listed in libraryfolders.vdf.
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s*"([^"]+)"')) {
                $lib = $m.Groups[1].Value -replace '\\\\', '\'
                $candidates.Add((Join-Path $lib 'steamapps\common\Distant Worlds 2'))
            }
        }
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "data\$SourceFile"))) { return (Resolve-Path $c).Path }
    }

    throw "Could not find Distant Worlds 2. Pass -GamePath, or set DW2_PATH. The path is the folder containing data\$SourceFile."
}

function Format-Factor {
    param([double] $Value)
    # Invariant culture: the game parses these with '.' as the decimal separator,
    # and a machine set to a comma-decimal locale would otherwise emit "1,5".
    return $Value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

$game = Find-GamePath -Explicit $GamePath
Write-Host "Game:   $game"

$source = Join-Path $game "data\$SourceFile"

if (-not $OutputPath) { $OutputPath = Join-Path $game 'mods\PirateEconomies' }
Write-Host "Output: $OutputPath"
Write-Host ''

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($source)

$governments = $xml.SelectNodes('//Government')
if ($governments.Count -eq 0) { throw "No <Government> elements in $source -- has the file format changed?" }

$changed = 0

foreach ($gov in $governments) {
    $idNode = $gov.SelectSingleNode('GovernmentId')
    if (-not $idNode) { continue }

    $id = [int] $idNode.InnerText
    if (-not $Design.ContainsKey($id)) { continue }

    $plan = $Design[$id]

    # <float> children of IncomeFactors, in document order. Their position IS the
    # IncomeType ordinal -- the game reads IncomeFactors[(int)incomeType].
    $floats = $gov.SelectNodes('EconomicFactors/IncomeFactors/float')
    if ($floats.Count -eq 0) {
        Write-Warning "government $id ($($plan.Name)) has no IncomeFactors; skipped."
        continue
    }

    $applied = @()

    foreach ($index in ($plan.Income.Keys | Sort-Object)) {
        if ($index -ge $floats.Count) {
            Write-Warning "government $id ($($plan.Name)): income index $index is past the end of a $($floats.Count)-entry list; skipped."
            continue
        }

        $before = $floats[$index].InnerText
        $after  = Format-Factor $plan.Income[$index]

        $floats[$index].InnerText = $after
        $applied += "[$index] $before -> $after"
    }

    Write-Host ("{0,-6} {1,-20} {2}" -f $id, $plan.Name, ($applied -join '  '))
    $changed++
}

if ($changed -ne $Design.Count) {
    throw "Expected to modify $($Design.Count) governments but modified $changed. The base file may have changed; check the GovernmentIds."
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

# UTF8Encoding($false) = no byte-order mark. XmlDocument.Save would write one, and
# the base game file has none; matching it keeps the diff to just the values, which
# is what makes this mod reviewable against a patched game.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$writerSettings = New-Object System.Xml.XmlWriterSettings
$writerSettings.Encoding = $utf8NoBom
$writerSettings.Indent = $false          # PreserveWhitespace already keeps the original layout

# The base file has MIXED CRLF and LF endings. NewLineHandling.Replace (the default)
# would rewrite every one of them to CRLF -- harmless to the parser, but it turns a
# 70-line diff into a 3,278-line one and destroys the ability to review this mod
# against a patched game file.
$writerSettings.NewLineHandling = [System.Xml.NewLineHandling]::None

$writer = [System.Xml.XmlWriter]::Create((Join-Path $OutputPath $SourceFile), $writerSettings)
try     { $xml.Save($writer) }
finally { $writer.Dispose() }

# Hand-written rather than ConvertTo-Json: this file is read by people as well as
# by the game, and PowerShell's formatter renders an empty array across three lines.
$description = 'Smugglers, Raiders and Mercenaries earn their money the way their names promise. The base game gives all nine pirate governments identical income factors.'
$json = @"
{
  "displayName": "Pirate Economies",
  "version": "1.0.0",
  "shortDescription": "$description",
  "bundles": []
}
"@

[System.IO.File]::WriteAllText((Join-Path $OutputPath 'mod.json'), $json, $utf8NoBom)

# Re-read what was written: a mod whose XML does not parse fails silently in-game.
$verify = New-Object System.Xml.XmlDocument
$verify.Load((Join-Path $OutputPath $SourceFile))
$verifyCount = $verify.SelectNodes('//Government').Count

Write-Host ''
Write-Host "Wrote mod.json and $SourceFile ($verifyCount governments, re-parsed clean)."
Write-Host ''
Write-Host 'Enable it in-game: Main menu -> Game Mods -> tick "Pirate Economies".'

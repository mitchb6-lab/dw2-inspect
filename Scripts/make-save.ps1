<#
.SYNOPSIS
    Generate a small saved game without touching the New Game screen.

.DESCRIPTION
    Launches Distant Worlds 2 once with the Dw2Mp MakeSave path armed. The mod builds a
    GameStartSettings in-process, calls DWGame.StartGameNew, lets the simulation settle, saves
    through the game's own DWGame.SaveGame, and exits through the game's session cleanup.

    Why a script and not just the environment variable: an unattended launch has three ways
    to produce nothing, and they look the same from the save folder. The game may never start
    (a stale data\SessionActive puts DW2 at a Win32 "Send Error Information" prompt before
    Stride runs; 2026-11-09), the save may fail inside the game (DW2 logs "Error while saving
    game" to its own SessionLog and returns normally), or the mod may not have been injected
    at all. This script checks each in turn and says which one happened.

    The save is written by the game itself into data\SavedGames, which is where the game's
    own saves live. Nothing else in the game folder is written, and nothing is deleted.

    Use this when the determinism harness needs a save that matches the installed content:
    with every DLC disabled, the old autosaves need races the game no longer has, and
    --continue loads the NEWEST .DWGame by write time, which this one will be.

.EXAMPLE
    .\make-save.ps1
    .\make-save.ps1 -SaveName MPBase1384 -Stars 15 -OtherEmpires 2 -Configuration Release
#>
[CmdletBinding()]
param(
    [string] $SaveName = 'MPBase1384',

    [int]    $Stars = 15,

    [int]    $OtherEmpires = 2,

    # Server cycles the new game runs before it is saved.
    [int]    $SettleCycles = 300,

    [int]    $TimeoutMinutes = 8,

    [string] $GamePath,

    # The determinism harness loads bin\Debug; pass Release to exercise that build instead.
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$modProject = Join-Path $repoRoot 'src\Dw2Mp'
$modDll = Join-Path $modProject "bin\$Configuration\Dw2Mp.dll"
$logDir = Join-Path $env:LOCALAPPDATA 'Dw2Mp'
$runLabel = 'makesave'
$modLog = Join-Path $logDir "determinism-$runLabel.log"

function Find-GamePath {
    param([string] $Explicit)

    $candidates = @($Explicit, $env:DW2_PATH)

    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        $candidates += (Join-Path $steam 'steamapps\common\Distant Worlds 2')
        $libraryFile = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $libraryFile) {
            $libraryText = Get-Content $libraryFile -Raw
            foreach ($match in [regex]::Matches($libraryText, '"path"\s*"([^"]+)"')) {
                $libraryPath = $match.Groups[1].Value -replace '\\\\', '\'
                $candidates += (Join-Path $libraryPath 'steamapps\common\Distant Worlds 2')
            }
        }
    }

    foreach ($candidate in $candidates) {
        if (-not $candidate) {
            continue
        }
        if (Test-Path (Join-Path $candidate 'DistantWorlds2.exe')) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw 'Could not find Distant Worlds 2. Pass -GamePath or set DW2_PATH.'
}

# ---------------------------------------------------------------------------

$game = Find-GamePath -Explicit $GamePath
$gameLog = Join-Path $game 'data\Logs\SessionLog.txt'
$sessionLock = Join-Path $game 'data\SessionActive'
$savePath = Join-Path $game "data\SavedGames\$SaveName.DWGame"

Write-Host "Game: $game"
Write-Host "Save: $savePath  (stars=$Stars, AI empires=$OtherEmpires, settle=$SettleCycles cycles)"

# A stale session lock means DW2 will stop at "Send Error Information" and never load. With no
# game running the lock can only be stale. Say so and stop; this script does not delete it.
$gameProcesses = @(Get-Process -Name 'DistantWorlds2' -ErrorAction SilentlyContinue)
if ($gameProcesses.Count -gt 0) {
    Write-Warning "DistantWorlds2.exe is already running (pid $($gameProcesses[0].Id)). Close it first."
    exit 1
}
if (Test-Path $sessionLock) {
    $lockMarker = (Get-Content $sessionLock -Raw -ErrorAction SilentlyContinue)
    if ($lockMarker) {
        $lockMarker = $lockMarker.Trim()
    }
    Write-Warning "Stale session lock: $sessionLock ('$lockMarker')."
    Write-Warning "DW2 will stop at its 'Send Error Information' prompt and never load a game."
    Write-Warning 'Delete that one file (nothing else in data\ is involved) and run again.'
    exit 1
}

if (-not $SkipBuild) {
    Write-Host "Building Dw2Mp ($Configuration)..."
    dotnet build $modProject -c $Configuration -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Mod build failed.'
    }
}
if (-not (Test-Path $modDll)) {
    throw "Mod not found at $modDll"
}

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
Remove-Item $modLog -ErrorAction SilentlyContinue

# Capture the TIME, not the FileInfo: a FileInfo reads its timestamps lazily on first use, so
# a "before" object first inspected after the run reports the new time and the save looks
# unchanged (the first run of this script said exactly that about a save it had just made).
$saveTimeBefore = $null
$existingSave = Get-Item $savePath -ErrorAction SilentlyContinue
if ($existingSave) {
    $saveTimeBefore = $existingSave.LastWriteTime
}
$logMark = (Get-Item $gameLog -ErrorAction SilentlyContinue).LastWriteTime

$env:DW2MP_DETERMINISM = '1'
$env:DW2MP_RUN_LABEL = $runLabel
$env:DW2MP_MAKE_SAVE = '1'
$env:DW2MP_SAVE_NAME = $SaveName
$env:DW2MP_SAVE_STARS = "$Stars"
$env:DW2MP_SAVE_EMPIRES = "$OtherEmpires"
$env:DW2MP_SAVE_SETTLE_CYCLES = "$SettleCycles"
$env:DW2MP_STEP_MS = '100'
$env:DW2MP_PIN_BLOCKS = '1'
$env:DW2MP_EXIT_WHEN_DONE = '1'

# No --continue and no --new-game: MakeSave starts the game itself from the main menu.
$arguments = @('--skip-splash', '--low-level-inject', $modDll)
$exe = Join-Path $game 'DistantWorlds2.exe'

Write-Host "Launching DistantWorlds2.exe $($arguments -join ' ')"
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $game -PassThru -WindowStyle Minimized

# STARTUP GUARD, as in determinism-run.ps1: prove the engine is alive before waiting on it.
# A run that produces no save has very different causes, and they look identical from here.
$started = $false

for ($second = 0; $second -lt 90; $second++) {
    Start-Sleep -Seconds 1
    if ($process.HasExited) {
        Write-Warning "DW2 exited after ${second}s with code $($process.ExitCode) before starting."
        if ($process.ExitCode -eq -532462766) {
            Write-Warning '  0xE0434352 is an unhandled managed exception during startup.'
            Write-Warning '  Usual causes: crash-dump pile in data\Logs, or a stale data\SessionActive.'
        }
        break
    }
    $logNow = (Get-Item $gameLog -ErrorAction SilentlyContinue).LastWriteTime
    if ($logNow -and (-not $logMark -or $logNow -gt $logMark)) {
        $started = $true
        break
    }
}

if (-not $started -and -not $process.HasExited) {
    Write-Warning 'STARTUP STALL -- DW2 wrote no SessionLog in 90s. This is not a save failure.'
    Write-Warning "Look at the game window: a Win32 prompt such as 'Send Error Information' blocks it before Stride starts."
    try {
        $process.Kill()
    }
    catch {
    }
}

# Wait for MakeSave to save and exit, but stop early if the game's own log shows the save or
# the load failed; the mod cannot see either from inside.
$failurePatterns = @('Error while saving game', 'There were problems loading Ship Hulls', 'Could not Load Savegame')
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$failureLine = $null

while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $sessionText = Get-Content $gameLog -ErrorAction SilentlyContinue
    $failureLine = $sessionText | Select-String -Pattern $failurePatterns -SimpleMatch | Select-Object -First 1
    if ($failureLine) {
        # Give the mod its read-back and exit before judging.
        Start-Sleep -Seconds 10
        break
    }
}

if ($failureLine) {
    Write-Warning "DW2 reported: $($failureLine.Line)"
}
elseif (-not $process.HasExited) {
    Write-Warning "Hit the ${TimeoutMinutes}-minute timeout without the game exiting."
}

# Kill() with NO argument. Under Windows PowerShell 5.1 (.NET Framework) Process has no
# Kill(Boolean) overload; that call throws, an empty catch swallowed it, and the first
# version of this script left a DW2 alive at a Win32 prompt for an hour.
if (-not $process.HasExited) {
    try {
        $process.Kill()
    }
    catch {
        Write-Warning "Could not end DW2 (pid $($process.Id)): $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# Result

Write-Host ''
Write-Host '=== Mod log (makesave, content, exit) ==='
$modLines = @(Get-Content $modLog -ErrorAction SilentlyContinue)
if ($modLines.Count -eq 0) {
    Write-Warning "No mod log at $modLog -- the mod was never injected. Check the --low-level-inject path."
}
$modLines | Select-String -Pattern '# makesave', '# content', '# exit', '# session', '# dialog', 'FAILED', 'ERROR' -SimpleMatch | ForEach-Object { Write-Host "  $($_.Line)" }

Write-Host ''
Write-Host '=== SessionLog (content packs, galaxy, errors) ==='
Get-Content $gameLog -ErrorAction SilentlyContinue |
    Select-String -Pattern 'Starting Game Session', 'Content Pack', 'Dimensions', 'Error while saving', 'problems loading', 'Could not Load' -SimpleMatch |
    ForEach-Object { Write-Host "  $($_.Line)" }

Write-Host ''
$saveAfter = Get-Item $savePath -ErrorAction SilentlyContinue
$saveIsNew = $false
if ($saveAfter) {
    if ($null -eq $saveTimeBefore) {
        $saveIsNew = $true
    }
    elseif ($saveAfter.LastWriteTime -gt $saveTimeBefore) {
        $saveIsNew = $true
    }
}
$lockLeft = Test-Path $sessionLock

if ($saveIsNew) {
    $sizeMb = [math]::Round($saveAfter.Length / 1MB, 1)
    Write-Host "SAVED: $($saveAfter.FullName)  $sizeMb MB  $($saveAfter.LastWriteTime)" -ForegroundColor Green
    Write-Host 'This is now the newest save, so --continue (and determinism-run.ps1) will load it.'
}
elseif ($saveAfter) {
    Write-Warning "The save at $savePath was NOT rewritten (still $($saveAfter.LastWriteTime)). Read the two logs above."
}
else {
    Write-Warning "No save at $savePath. Read the two logs above."
}

if ($lockLeft) {
    Write-Warning "data\SessionActive was left behind; the next launch will stop at 'Send Error Information'. Delete it before running again."
}

Write-Host ''
Write-Host "Logs: $modLog"
Write-Host "      $gameLog"

if ($saveIsNew -and -not $lockLeft) {
    exit 0
}
exit 1

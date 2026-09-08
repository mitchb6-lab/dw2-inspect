<#
.SYNOPSIS
    M2: run Distant Worlds 2 twice and compare simulation state hashes.

.DESCRIPTION
    Lockstep multiplayer needs the simulation to be reproducible. This launches the
    game N times with the Dw2Mp determinism harness armed, each run starting a new
    game and hashing the whole galaxy at fixed GAME-time intervals, then compares
    the hash sequences.

    Reading the result:

    * "stable" column NO  -> the MEASUREMENT is unstable at that instant (the two
      back-to-back hashes within one run disagreed, i.e. a torn read while worker
      threads mutate). Those rows say nothing about the simulation. If most rows
      are NO, fix the harness before drawing any conclusion.

    * Day 0 hashes differ between runs -> GALAXY GENERATION is not reproducible
      across runs. That must be solved (seed control) before simulation
      determinism can even be tested.

    * Day 0 matches, later days differ -> the SIMULATION is non-deterministic.
      That is the finding the whole experiment exists to establish.

    * All days match across runs -> reproducible on this machine. The next question
      is cross-machine, which is a different and harder test.

    Nothing is installed into the game directory; the mod is loaded by path.

.EXAMPLE
    .\determinism-run.ps1
    .\determinism-run.ps1 -Runs 3 -IntervalDays 2 -Snapshots 8 -TimeoutMinutes 15
#>
[CmdletBinding()]
param(
    [int]    $Runs = 2,

    # Server cycles between snapshots. Under the fixed-step patch the server cycle IS
    # the simulation tick, so comparing at equal cycle counts compares identical step
    # sequences. Comparing at equal GAME TIME would not: DW2's clock is a wall-clock
    # stopwatch, so two runs reach the same time having taken different numbers of steps.
    [long]   $EveryCycles = 2000,

    [int]    $Snapshots = 5,
    [int]    $TimeoutMinutes = 12,

    # Simulated milliseconds per server cycle. 0 leaves the wall-clock clock in place,
    # which makes the comparison invalid -- only use it to demonstrate that.
    [double] $StepMs = 100,

    # Freeze ship/location/creature block sizes. AdjustClientBlockSizes derives them from
    # MEASURED milliseconds, so leaving it on means two runs divide the same work
    # differently and the comparison is invalid for a second reason.
    [bool]   $PinBlocks = $true,

    [string] $GamePath,
    [switch] $SkipBuild,

    # 'continue' loads the most recent save; 'new' generates a fresh galaxy.
    #
    # 'continue' is the default because it is the better experiment: both runs start
    # from byte-identical state, so galaxy generation is removed as a variable and any
    # divergence is unambiguously the simulation. It is also the only one that works
    # without a game having been configured -- '--new-game' against unconfigured
    # GameStartSettings throws NullReferenceException inside
    # DWGame.InitializeSinglePlayerGame (playerEmpire is null).
    [ValidateSet('continue', 'new')]
    [string] $Mode = 'continue'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$modProject = Join-Path $repoRoot 'src\Dw2Mp'
$modDll = Join-Path $modProject 'bin\Debug\Dw2Mp.dll'
$logDir = Join-Path $env:LOCALAPPDATA 'Dw2Mp'

function Find-GamePath {
    param([string] $Explicit)

    $candidates = @($Explicit, $env:DW2_PATH)

    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        $candidates += (Join-Path $steam 'steamapps\common\Distant Worlds 2')
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s*"([^"]+)"')) {
                $candidates += (Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Distant Worlds 2')
            }
        }
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'DistantWorlds2.exe'))) { return (Resolve-Path $c).Path }
    }

    throw 'Could not find Distant Worlds 2. Pass -GamePath or set DW2_PATH.'
}

function Parse-RunLog {
    param([string] $Path)

    $rows = @()
    if (-not (Test-Path $Path)) { return $rows }

    foreach ($line in Get-Content $Path) {
        if ($line -match '^\s*#' -or -not $line.Trim()) { continue }

        # cycle  bytes  hashA  hashB  stable
        $parts = $line -split '\s+' | Where-Object { $_ }
        if ($parts.Count -lt 5) { continue }

        $rows += [pscustomobject]@{
            Cycle  = [long]   $parts[0]
            Bytes  = [long]   $parts[1]
            Hash   = $parts[2]
            HashB  = $parts[3]
            Stable = ($parts[4] -eq 'yes')
        }
    }

    return $rows
}

# ---------------------------------------------------------------------------

$game = Find-GamePath -Explicit $GamePath
Write-Host "Game: $game   (mode: $Mode)"

if ($Mode -eq 'continue') {
    $saves = @(Get-ChildItem (Join-Path $game 'data\SavedGames') -File -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -ne 'PlaceHolder' -and $_.Extension -ne '.vdf' })

    if ($saves.Count -eq 0) {
        Write-Host ''
        Write-Host 'No saved game found, and -Mode continue needs one.' -ForegroundColor Yellow
        Write-Host 'Start DW2 normally, set up any game, save it, then quit. Both runs will'
        Write-Host 'load that save, which is what makes the comparison meaningful: identical'
        Write-Host 'starting state, so any divergence is the simulation and not galaxy generation.'
        exit 1
    }

    Write-Host "Save:  $($saves[0].Name) ($([math]::Round($saves[0].Length/1MB,1)) MB)"
}

if (-not $SkipBuild) {
    Write-Host 'Building Dw2Mp...'
    dotnet build $modProject -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
}
if (-not (Test-Path $modDll)) { throw "Mod not found at $modDll" }

New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$results = @{}

for ($i = 1; $i -le $Runs; $i++) {
    $label = "run$i"
    $log = Join-Path $logDir "determinism-$label.log"
    Remove-Item $log -ErrorAction SilentlyContinue

    Write-Host ''
    Write-Host "--- Run $i of $Runs ---"

    $env:DW2MP_DETERMINISM      = '1'
    $env:DW2MP_RUN_LABEL        = $label
    $env:DW2MP_SNAPSHOT_CYCLES  = $EveryCycles
    $env:DW2MP_MAX_SNAPSHOTS    = $Snapshots
    $env:DW2MP_EXIT_WHEN_DONE   = '1'
    $env:DW2MP_STEP_MS          = $StepMs.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $env:DW2MP_PIN_BLOCKS       = if ($PinBlocks) { '1' } else { '0' }

    $startFlag = if ($Mode -eq 'continue') { '--continue' } else { '--new-game' }
    $args = @('--skip-splash', $startFlag, '--low-level-inject', $modDll)
    $proc = Start-Process -FilePath (Join-Path $game 'DistantWorlds2.exe') `
                          -ArgumentList $args -WorkingDirectory $game -PassThru

    if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        Write-Warning "run $i hit the ${TimeoutMinutes}-minute timeout; killing it and using whatever it logged."
        try { $proc.Kill($true) } catch { }
    }

    $rows = Parse-RunLog $log
    $results[$label] = $rows
    Write-Host "  $($rows.Count) snapshot(s) recorded"
}

# ---------------------------------------------------------------------------
# Compare

Write-Host ''
Write-Host '=== Result ==='

$baseline = $results['run1']
if (-not $baseline -or $baseline.Count -eq 0) {
    Write-Host 'run1 produced no snapshots. Check the log and the game window:'
    Write-Host "  $logDir\determinism-run1.log"
    exit 1
}

$unstable = @($baseline | Where-Object { -not $_.Stable }).Count
if ($unstable -gt 0) {
    Write-Warning "$unstable of $($baseline.Count) snapshots in run1 were MEASUREMENT-unstable (torn reads)."
    Write-Warning 'Rows marked unstable are not evidence about the simulation.'
}

$header = "{0,8}  {1,10}" -f 'cycle', 'bytes'
for ($i = 1; $i -le $Runs; $i++) { $header += "  {0,-18}" -f "run$i" }
Write-Host $header

$allMatch = $true
$firstDivergence = $null

for ($r = 0; $r -lt $baseline.Count; $r++) {
    $row = $baseline[$r]
    $line = "{0,8}  {1,10}" -f $row.Cycle, $row.Bytes
    $match = $true

    for ($i = 1; $i -le $Runs; $i++) {
        $other = $results["run$i"]
        $h = if ($r -lt $other.Count) { $other[$r].Hash } else { '(missing)' }
        $line += "  {0,-18}" -f $h
        if ($h -ne $row.Hash) { $match = $false }
    }

    if (-not $match) {
        $allMatch = $false
        if ($null -eq $firstDivergence) { $firstDivergence = $row.Cycle }
        $line += '  <-- DIVERGED'
    }

    Write-Host $line
}

Write-Host ''
if ($Runs -lt 2) {
    # With one run the comparison is against itself and always "matches". Saying
    # anything else here would be a vacuous green light.
    Write-Host 'NO VERDICT: a single run compares against itself. Use -Runs 2 or more.' -ForegroundColor Yellow
} elseif ($allMatch) {
    Write-Host 'VERDICT: identical at every tick, across all runs on this machine.' -ForegroundColor Green
    Write-Host 'With a fixed timestep and pinned block sizes, DW2 simulates deterministically.'
    Write-Host 'Lockstep is viable in principle. Next question is cross-machine, which is'
    Write-Host 'the harder half -- float results can differ across CPU models and JIT versions.'
} elseif ($firstDivergence -eq $baseline[0].Cycle) {
    Write-Host 'VERDICT: diverged at the FIRST snapshot.' -ForegroundColor Yellow
    Write-Host 'Even loading the same save does not reproduce. Suspect the harness or load'
    Write-Host 'order before blaming the simulation -- check the "stable" column first.'
} else {
    Write-Host "VERDICT: identical at first, diverged at cycle $firstDivergence." -ForegroundColor Yellow
    Write-Host 'The simulation is non-deterministic EVEN WITH an identical step sequence and'
    Write-Host 'frozen work partitioning. That leaves thread completion order or float'
    Write-Host 'arithmetic. Next step: force the parallel block processing to run'
    Write-Host 'sequentially and re-run. If it then matches, it is ordering, not arithmetic.'
}

Write-Host ''
Write-Host "Logs: $logDir"

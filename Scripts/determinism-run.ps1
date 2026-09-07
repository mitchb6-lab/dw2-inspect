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
    [double] $IntervalDays = 5,
    [int]    $Snapshots = 6,
    [int]    $TimeoutMinutes = 10,
    [string] $GamePath,
    [switch] $SkipBuild
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

        # day  bytes  hashA  hashB  stable
        $parts = $line -split '\s+' | Where-Object { $_ }
        if ($parts.Count -lt 5) { continue }

        $rows += [pscustomobject]@{
            Day    = [double] $parts[0]
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
Write-Host "Game: $game"

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

    $env:DW2MP_DETERMINISM    = '1'
    $env:DW2MP_RUN_LABEL      = $label
    $env:DW2MP_SNAPSHOT_DAYS  = $IntervalDays.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $env:DW2MP_MAX_SNAPSHOTS  = $Snapshots
    $env:DW2MP_EXIT_WHEN_DONE = '1'

    $args = @('--skip-splash', '--new-game', '--low-level-inject', $modDll)
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

$header = "{0,7}  {1,10}" -f 'day', 'bytes'
for ($i = 1; $i -le $Runs; $i++) { $header += "  {0,-18}" -f "run$i" }
Write-Host $header

$allMatch = $true
$firstDivergence = $null

for ($r = 0; $r -lt $baseline.Count; $r++) {
    $row = $baseline[$r]
    $line = "{0,7:F1}  {1,10}" -f $row.Day, $row.Bytes
    $match = $true

    for ($i = 1; $i -le $Runs; $i++) {
        $other = $results["run$i"]
        $h = if ($r -lt $other.Count) { $other[$r].Hash } else { '(missing)' }
        $line += "  {0,-18}" -f $h
        if ($h -ne $row.Hash) { $match = $false }
    }

    if (-not $match) {
        $allMatch = $false
        if ($null -eq $firstDivergence) { $firstDivergence = $row.Day }
        $line += '  <-- DIVERGED'
    }

    Write-Host $line
}

Write-Host ''
if ($allMatch) {
    Write-Host 'VERDICT: identical across all runs on this machine.' -ForegroundColor Green
    Write-Host 'Lockstep is viable here. Next question is cross-machine.'
} elseif ($firstDivergence -eq $baseline[0].Day) {
    Write-Host 'VERDICT: diverged at the FIRST snapshot.' -ForegroundColor Yellow
    Write-Host 'Galaxy generation is not reproducible across runs. Seed control has to be'
    Write-Host 'solved before simulation determinism can be tested at all.'
} else {
    Write-Host "VERDICT: identical at first, diverged at day $firstDivergence." -ForegroundColor Yellow
    Write-Host 'The SIMULATION is non-deterministic. Next: establish which of the three'
    Write-Host 'causes it is (float, thread ordering, or wall-clock block sizing).'
}

Write-Host ''
Write-Host "Logs: $logDir"

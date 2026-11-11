<#
.SYNOPSIS
    A lobby-free two-instance session on one PC: a host DW2, a joiner DW2 and a console relay
    between them. Written 2026-11-10 for mitch's "measure first" step of the desync work.

.DESCRIPTION
    The lobby (src\Dw2MpLobby) is a WinForms window, so nobody can drive it unattended. After it
    has launched the games, all it does for them is pump frames between two sockets. This script
    does the same without it:

      1. writes session-host.json and session-client.json (the shape the lobby writes),
      2. starts "dw2mpprobe relay <hostPort> <joinerPort>",
      3. launches both games at the same moment, each with the environment the lobby gives it,
      4. waits for both to leave on their own (the mod exits after -Cycles server cycles),
      5. prints the relay's traffic, the lines that matter from both mod logs and, when
         -DivergenceDir is given, the host-versus-joiner divergence report.

    BOTH GAMES START TOGETHER ON PURPOSE. DW2 creates data\SessionActive in DWGame.Initialize and
    stops at its "Send Error Information" prompt when it finds one at startup. Two instances on
    one PC share that file, so the second must pass its check before the first creates the lock.

    The host runs with DW2MP_KEEP_RUNNING=1 (nobody is there to click through an empire-message
    pause). The joiner runs with DW2MP_KEEP_RUNNING=0, exactly as the lobby launches it, so its
    log shows whether DW2's own auto-pause ever pauses a joiner.

    Run it through the agent wrapper, never directly from an agent:
      powershell -NoProfile -ExecutionPolicy Bypass -File "$env:USERPROFILE\.claude\agent-limits\run-low.ps1" -Command "powershell -NoProfile -ExecutionPolicy Bypass -File Scripts\net-twoinstance.ps1 -Cycles 9000 -DivergenceDir C:\temp\dw2div" -Minutes 20

.PARAMETER Cycles
    How long each game runs after its galaxy exists, in server cycles (the mod's tick). Measured
    on 2026-11-10 at about 29 cycles a second, not the 10 older notes assume: 9,000 cycles is
    about five minutes.

.PARAMETER DivergenceDir
    Where both games write their census files (DW2MP_DIVERGENCE_DIR). Empty: no measurement.

.PARAMETER ExtraHostEnv / ExtraJoinerEnv
    "NAME=value;NAME=value" pairs added to one game's environment, for dev flags such as
    DW2MP_FORCE_DESYNC_AT_CYCLE or DW2MP_KEYFRAME_MINUTES.
#>
param(
    [int]    $Cycles = 9000,
    [int]    $Stars = 30,
    [int]    $AiEmpires = 4,
    [int]    $Seed = 12345,
    [double] $GameSpeed = 4,
    [string] $Mode = 'coop-shared',
    [string] $DivergenceDir = '',
    [int]    $DivergenceEvery = 1,
    [int]    $HostPort = 47811,
    [int]    $JoinerPort = 47812,
    [string] $GamePath = '',
    [int]    $StartupMinutes = 8,
    [string] $ExtraHostEnv = '',
    [string] $ExtraJoinerEnv = '',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$modProject = Join-Path $repo 'src\Dw2Mp\Dw2Mp.csproj'
$modDll = Join-Path $repo 'src\Dw2Mp\bin\Debug\Dw2Mp.dll'
$probeProject = Join-Path $repo 'src\Dw2MpProbe\Dw2MpProbe.csproj'
$logDir = Join-Path $env:LOCALAPPDATA 'Dw2Mp'
$runDir = Join-Path $logDir 'twoinstance'
$hostLabel = 'twoHost'
$joinerLabel = 'twoClient'
$hostLog = Join-Path $logDir "determinism-$hostLabel.log"
$joinerLog = Join-Path $logDir "determinism-$joinerLabel.log"
$relayLog = Join-Path $runDir 'relay.log'

function Find-GamePath {
    param([string] $Explicit)

    $candidates = @($Explicit, $env:DW2_PATH)

    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        $candidates += Join-Path $steam 'steamapps\common\Distant Worlds 2'
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'DistantWorlds2.exe'))) {
            return $candidate
        }
    }

    return $null
}

# The harness may remove ITS OWN session lock after a run it had to end by force, and nothing
# else (mitch, 2026-11-10; same rule as determinism-run.ps1). Own means: no DistantWorlds2
# process is running, and the lock's "Session Started" time falls inside this run's window.
function Remove-OwnSessionLock {
    param(
        [string]   $LockPath,
        [datetime] $LaunchedAt
    )

    if (-not (Test-Path $LockPath)) {
        return $true
    }

    $liveGames = @(Get-Process -Name 'DistantWorlds2' -ErrorAction SilentlyContinue)
    if ($liveGames.Count -gt 0) {
        Write-Warning "DistantWorlds2.exe (pid $($liveGames[0].Id)) is still running; leaving $LockPath alone."
        return $false
    }

    $marker = Get-Content $LockPath -Raw -ErrorAction SilentlyContinue
    if ($marker) {
        $marker = $marker.Trim()
    }

    $match = [regex]::Match("$marker", 'Session Started (\d{2})-(\d{2})-(\d{4}) (\d{2})-(\d{2})-(\d{2})\.(\d{3})')
    if (-not $match.Success) {
        Write-Warning "Could not read a session time from '$marker'; leaving $LockPath alone."
        return $false
    }

    $day = [int] $match.Groups[1].Value
    $month = [int] $match.Groups[2].Value
    $year = [int] $match.Groups[3].Value
    $hour = [int] $match.Groups[4].Value
    $minute = [int] $match.Groups[5].Value
    $second = [int] $match.Groups[6].Value
    $sessionStarted = New-Object DateTime $year, $month, $day, $hour, $minute, $second

    $windowStart = $LaunchedAt.AddSeconds(-5)
    $windowEnd = Get-Date
    if (($sessionStarted -lt $windowStart) -or ($sessionStarted -gt $windowEnd)) {
        Write-Warning "$LockPath says '$marker', which is not from this run (launched $($LaunchedAt.ToString('yyyy-MM-dd HH:mm:ss'))); leaving it alone."
        return $false
    }

    Remove-Item $LockPath -Force -ErrorAction Stop
    Write-Host "Removed this run's own session lock ('$marker')."
    return $true
}

function Write-SessionFile {
    param(
        [string] $Path,
        [int]    $MySlot
    )

    $session = [ordered]@{
        protocolVersion = 2
        mode            = $Mode
        hostAddress     = '127.0.0.1'
        port            = 47800
        galaxy          = [ordered]@{ stars = $Stars; aiEmpires = $AiEmpires; seed = $Seed }
        players         = @(
            [ordered]@{ slot = 0; name = 'HarnessHost';   empire = [ordered]@{ name = 'Harness Host Empire';   raceId = 0; governmentId = 0; r = 0; g = 0;   b = 176 } },
            [ordered]@{ slot = 1; name = 'HarnessJoiner'; empire = [ordered]@{ name = 'Harness Joiner Empire'; raceId = 1; governmentId = 0; r = 0; g = 255; b = 255 } }
        )
        mySlot          = $MySlot
    }

    $json = $session | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding $false))
}

function Set-ExtraEnvironment {
    param([string] $Pairs)

    $applied = @()
    if (-not $Pairs) {
        return $applied
    }

    foreach ($pair in $Pairs.Split(';')) {
        $separator = $pair.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $name = $pair.Substring(0, $separator).Trim()
        $value = $pair.Substring($separator + 1)
        Set-Item -Path "env:$name" -Value $value
        $applied += $name
    }

    return $applied
}

function Clear-ExtraEnvironment {
    param([string[]] $Names)

    foreach ($name in $Names) {
        Remove-Item -Path "env:$name" -ErrorAction SilentlyContinue
    }
}

function Set-GameEnvironment {
    param(
        [string] $Role,
        [string] $Label,
        [string] $SessionPath,
        [int]    $LocalPort,
        [string] $KeepRunning,
        [string] $Speed
    )

    # What the lobby sets (LobbyForm.StartGame), plus an exit after -Cycles cycles.
    $env:DW2MP_DETERMINISM     = '1'
    $env:DW2MP_PIN_BLOCKS      = '1'
    $env:DW2MP_KEEP_RUNNING    = $KeepRunning
    $env:DW2MP_EXIT_WHEN_DONE  = '1'
    $env:DW2MP_SNAPSHOT_CYCLES = $Cycles
    $env:DW2MP_MAX_SNAPSHOTS   = '2'
    $env:DW2MP_ROLE            = $Role
    $env:DW2MP_MODE            = $Mode
    $env:DW2MP_RUN_LABEL       = $Label
    $env:DW2MP_SESSION         = $SessionPath
    $env:DW2MP_LOCAL_PORT      = $LocalPort
    $env:DW2MP_GAME_SPEED      = $Speed

    # Disarm the other launch modes: a variable left in this shell by another script changes
    # what the mod does without anything here saying so (2026-11-10, determinism-run.ps1).
    $env:DW2MP_MAKE_SAVE       = '0'
    $env:DW2MP_STEP_MS         = ''
    $env:DW2MP_DUMP_DIR        = ''

    # No autosaves into the player's own slots during a harness run.
    $env:DW2MP_BLOCK_AUTOSAVE  = '1'

    $env:DW2MP_DIVERGENCE_DIR   = $DivergenceDir
    $env:DW2MP_DIVERGENCE_EVERY = $DivergenceEvery
}

$game = Find-GamePath $GamePath
if (-not $game) {
    throw 'Distant Worlds 2 not found. Pass -GamePath or set DW2_PATH.'
}

Write-Host "Game:  $game"

$sessionLock = Join-Path $game 'data\SessionActive'
$gameProcesses = @(Get-Process -Name 'DistantWorlds2' -ErrorAction SilentlyContinue)

if ($gameProcesses.Count -gt 0) {
    Write-Warning "DistantWorlds2.exe is already running (pid $($gameProcesses[0].Id)). This run needs both instances to start together; close it first."
    exit 1
}

if (Test-Path $sessionLock) {
    Write-Warning "Stale session lock: $sessionLock. DW2 will stop at its 'Send Error Information' prompt."
    Write-Warning 'Delete that one file (nothing else in data\ is involved) and run again.'
    exit 1
}

if (-not $SkipBuild) {
    Write-Host 'Building Dw2Mp and the probe...'
    dotnet build $modProject -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Mod build failed.'
    }

    dotnet build $probeProject -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Probe build failed.'
    }
}

if (-not (Test-Path $modDll)) {
    throw "Mod not found at $modDll"
}

$probeExe = Get-ChildItem (Join-Path $repo 'src\Dw2MpProbe\bin\Debug') -Recurse -Filter 'dw2mpprobe.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
if ($null -eq $probeExe) {
    throw 'dw2mpprobe.exe not found; build src\Dw2MpProbe first.'
}

New-Item -ItemType Directory -Path $runDir -Force | Out-Null
Remove-Item $hostLog, $joinerLog, $relayLog -ErrorAction SilentlyContinue

if ($DivergenceDir) {
    New-Item -ItemType Directory -Path $DivergenceDir -Force | Out-Null
    Remove-Item (Join-Path $DivergenceDir 'host-tick*.txt'), (Join-Path $DivergenceDir 'client-tick*.txt') -ErrorAction SilentlyContinue
}

$hostSession = Join-Path $runDir 'session-host.json'
$joinerSession = Join-Path $runDir 'session-client.json'
Write-SessionFile -Path $hostSession -MySlot 0
Write-SessionFile -Path $joinerSession -MySlot 1

Write-Host "Relay: 127.0.0.1:$HostPort (host) <-> 127.0.0.1:$JoinerPort (joiner)"
$relay = Start-Process -FilePath $probeExe.FullName -ArgumentList @('relay', $HostPort, $JoinerPort) `
                       -RedirectStandardOutput $relayLog -WindowStyle Hidden -PassThru

$gameExe = Join-Path $game 'DistantWorlds2.exe'
$gameArgs = @('--skip-splash', '--low-level-inject', $modDll)
$speedText = $GameSpeed.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$launchedAt = Get-Date

Set-GameEnvironment -Role 'host' -Label $hostLabel -SessionPath $hostSession -LocalPort $HostPort -KeepRunning '1' -Speed $speedText
$hostExtras = Set-ExtraEnvironment $ExtraHostEnv
$hostGame = Start-Process -FilePath $gameExe -ArgumentList $gameArgs -WorkingDirectory $game -WindowStyle Minimized -PassThru
Clear-ExtraEnvironment $hostExtras

Set-GameEnvironment -Role 'client' -Label $joinerLabel -SessionPath $joinerSession -LocalPort $JoinerPort -KeepRunning '0' -Speed ''
$joinerExtras = Set-ExtraEnvironment $ExtraJoinerEnv
$joinerGame = Start-Process -FilePath $gameExe -ArgumentList $gameArgs -WorkingDirectory $game -WindowStyle Minimized -PassThru
Clear-ExtraEnvironment $joinerExtras

Write-Host "Host pid $($hostGame.Id), joiner pid $($joinerGame.Id), relay pid $($relay.Id). Launched $($launchedAt.ToString('HH:mm:ss'))."

# From here to the clean-up, nothing may end the script: two games are running and this script
# is the only thing that will stop them at the deadline. Any error in the supervision below is
# caught, reported, and followed by the same clean-up as a normal end.
$bothConnected = $false
$supervisionFailed = $false

try {

# STARTUP GUARD. Both mods connect to the relay from their module initialiser, seconds after
# the process starts. A game that never says so did not start: usually the session-lock prompt.
for ($waited = 0; $waited -lt 120; $waited++) {
    Start-Sleep -Seconds 1

    # Test-Path first: under $ErrorActionPreference = 'Stop', Select-String on a log that does
    # not exist yet ends the script (2026-11-10: the first run died here one second after
    # launch and left both games running with nothing supervising them).
    $hostSays = $false
    if (Test-Path $hostLog) {
        $hostSays = Select-String -Path $hostLog -Pattern 'connected to launcher' -SimpleMatch -Quiet
    }

    $joinerSays = $false
    if (Test-Path $joinerLog) {
        $joinerSays = Select-String -Path $joinerLog -Pattern 'connected to launcher' -SimpleMatch -Quiet
    }

    if ($hostSays -and $joinerSays) {
        $bothConnected = $true
        break
    }

    if ($hostGame.HasExited -or $joinerGame.HasExited) {
        break
    }
}

if ($bothConnected) {
    Write-Host "Both mods connected to the relay after ${waited}s. Running for $Cycles cycles (about $([int]($Cycles / 29)) s at 29 a second)..."
}
else {
    Write-Warning 'STARTUP STALL: at least one game never connected to the relay within 120 s.'
    Write-Warning "If a DW2 window says 'Send Error Information', the second instance found the first one's session lock."
}

# Allow for a slow machine: the deadline assumes only 15 cycles a second.
$deadline = (Get-Date).AddMinutes($StartupMinutes).AddSeconds($Cycles / 15)
if (-not $bothConnected) {
    $deadline = Get-Date
}

while ((Get-Date) -lt $deadline) {
    if ($hostGame.HasExited -and $joinerGame.HasExited) {
        break
    }

    Start-Sleep -Seconds 5
}

}
catch {
    $supervisionFailed = $true
    Write-Warning "The script's own supervision failed: $($_.Exception.Message)"
    Write-Warning 'Ending the games it launched rather than leaving them running unattended.'
}

# End what is left. The parameterless call: Windows PowerShell 5.1 has no one-argument overload.
$endedByForce = $false
foreach ($leftover in @($hostGame, $joinerGame)) {
    if (-not $leftover.HasExited) {
        $endedByForce = $true
        Write-Warning "DW2 pid $($leftover.Id) was still running at the deadline; ending it."
        try {
            $leftover.Kill()
        }
        catch {
            Write-Warning "Could not end DW2 pid $($leftover.Id): $($_.Exception.Message)"
        }
    }
}

Start-Sleep -Seconds 3

if (-not $relay.HasExited) {
    try {
        $relay.Kill()
    }
    catch {
        Write-Warning "Could not end the relay (pid $($relay.Id)): $($_.Exception.Message)"
    }
}

if (Test-Path $sessionLock) {
    $lockRemoved = Remove-OwnSessionLock -LockPath $sessionLock -LaunchedAt $launchedAt
    if (-not $lockRemoved) {
        Write-Warning "$sessionLock is still there; the next DW2 launch will stop at 'Send Error Information'."
    }
}

# ---------------------------------------------------------------------------
# Report

Write-Host ''
Write-Host "=== relay ($relayLog)"
Get-Content $relayLog -ErrorAction SilentlyContinue | Select-Object -Last 14

$interesting = 'handshake|MISMATCH|census|full state #|keyframe|requested full state|APPLIED sync|structure|desync|sync check|PAUSED|STALLED|pause|speed|MUTATION|delta #|dropped|holding|released|ERROR|failed|run complete|exit:'

foreach ($entry in @(@{ Name = 'host'; Path = $hostLog }, @{ Name = 'joiner'; Path = $joinerLog })) {
    Write-Host ''
    Write-Host "=== $($entry.Name) log ($($entry.Path))"

    if (-not (Test-Path $entry.Path)) {
        Write-Warning 'no log: the mod never ran in this process'
        continue
    }

    Get-Content $entry.Path | Select-String -Pattern $interesting | ForEach-Object { $_.Line } | Select-Object -Last 90
}

if ($DivergenceDir) {
    Write-Host ''
    Write-Host "=== divergence report ($DivergenceDir)"
    & $probeExe.FullName divergence $DivergenceDir 12
}

if ($supervisionFailed) {
    exit 4
}

if ($endedByForce) {
    exit 3
}

if (-not $bothConnected) {
    exit 2
}

exit 0

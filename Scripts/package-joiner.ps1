<#
.SYNOPSIS
    Build the zip a second player needs to join a DW2 multiplayer session: the lobby and the mod.

.DESCRIPTION
    The joiner should not need the .NET SDK, a clone of this repo, or a matching commit by hand.
    This makes one zip in out\ holding:

      Dw2MpLobby.exe   the launcher, published self-contained, so no .NET install is needed
      Dw2Mp.dll        the mod, beside the exe (the lobby looks there first, LobbyForm.FindModDll)
      VERSION.txt      the commit it was built from and the DW2 build it was built against

    Both players must run a zip from the same commit. The mod's own dependencies (0Harmony and
    the game assemblies) come from the joiner's game install at runtime, so they are not packed.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Scripts\package-joiner.ps1
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outFolder = Join-Path $repoRoot "out"
$stagingFolder = Join-Path $outFolder "joiner-package"

$commit = (git -C $repoRoot rev-parse --short HEAD).Trim()
$uncommitted = git -C $repoRoot status --porcelain -- src
if ($uncommitted) {
    throw "src has uncommitted changes; commit first so VERSION.txt names what is really inside the zip."
}

if (Test-Path $stagingFolder) {
    Remove-Item -Recurse -Force $stagingFolder
}
New-Item -ItemType Directory -Force $stagingFolder | Out-Null

Write-Host "Building the mod (net8.0, Release)..."
dotnet build (Join-Path $repoRoot "src\Dw2Mp") -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    throw "the mod build failed"
}

Write-Host "Publishing the lobby (self-contained, single file)..."
dotnet publish (Join-Path $repoRoot "src\Dw2MpLobby") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stagingFolder --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    throw "the lobby publish failed"
}

$modDll = Join-Path $repoRoot "src\Dw2Mp\bin\Release\Dw2Mp.dll"
Copy-Item $modDll $stagingFolder

$symbolFiles = Get-ChildItem $stagingFolder -Filter *.pdb
foreach ($symbolFile in $symbolFiles) {
    Remove-Item $symbolFile.FullName
}

$gameBuild = "unknown"
$gameExe = "C:\Program Files (x86)\Steam\steamapps\common\Distant Worlds 2\DistantWorlds2.exe"
if (Test-Path $gameExe) {
    $gameBuild = (Get-Item $gameExe).VersionInfo.ProductVersion
}

$versionLines = @(
    "Dw2Mp joiner package",
    "commit:     $commit",
    "built:      $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    "DW2 build:  $gameBuild (the host's install when this was built; both players need the same build)",
    "",
    "Unzip anywhere and run Dw2MpLobby.exe. Keep Dw2Mp.dll beside it.",
    "Setup and troubleshooting: docs/second-machine.md in https://github.com/mitchb6-lab/dw2-inspect"
)
Set-Content -Path (Join-Path $stagingFolder "VERSION.txt") -Value $versionLines -Encoding UTF8

$zipPath = Join-Path $outFolder "Dw2Mp-joiner-$commit.zip"
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}
Compress-Archive -Path (Join-Path $stagingFolder "*") -DestinationPath $zipPath

$zipSizeMegabytes = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Wrote $zipPath ($zipSizeMegabytes MB)"
Get-ChildItem $stagingFolder | Select-Object Name, Length | Format-Table -AutoSize

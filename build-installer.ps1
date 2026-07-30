# Builds the installable PitLaunch and its update packages.
#
# Unlike build.ps1 (which produces the single-file portable zip), this publishes the app as loose
# files so Velopack can ship UPDATES AS DELTAS: only the files that actually changed are downloaded,
# instead of the whole ~160 MB app.
#
#   .\build-installer.ps1                      -> builds the version in PitLaunch.csproj
#   .\build-installer.ps1 -PackVersion 0.9.2   -> overrides the version
#   .\build-installer.ps1 -ReleaseDir D:\feed  -> writes the release feed somewhere else
#
# Publishing an update:
#   1. Bump <Version> in PitLaunch.csproj and AppInfo.Version.
#   2. Run this script. It reads the previous releases in the output folder and writes a delta.
#   3. Upload the CONTENTS of the release folder (keep the older files there - the delta refers to
#      them, and the RELEASES index lists every version).
#
# First-time setup for updates to work at all: set AppInfo.UpdateFeedUrl to where you upload these.

param(
    [string]$PackVersion = "",
    [string]$ReleaseDir = "",
    [string]$Channel = "win"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Workspace = Split-Path -Parent (Split-Path -Parent $Root)
$Project = Join-Path $Root "PitLaunch.csproj"
$Staging = Join-Path $Root "artifacts\installer-publish"
$LocalDotNet = Join-Path $Workspace ".tools\dotnet\dotnet.exe"
if ($ReleaseDir -eq "") { $ReleaseDir = Join-Path $Workspace "outputs\releases" }

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\')
    if (!$fullPath.StartsWith($fullParent + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside $fullParent`: $fullPath"
    }
}

if (Test-Path $LocalDotNet) {
    $DotNet = $LocalDotNet
} else {
    $DotNetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $DotNetCommand) { throw "The .NET 8 SDK is required. Install it or place it at $LocalDotNet" }
    $DotNet = $DotNetCommand.Source
}

$Vpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
if (!(Test-Path $Vpk)) {
    $VpkCommand = Get-Command vpk -ErrorAction SilentlyContinue
    if ($null -eq $VpkCommand) {
        throw "The Velopack CLI is required. Install it with: dotnet tool install -g vpk --version 1.2.0"
    }
    $Vpk = $VpkCommand.Source
}

if ($PackVersion -eq "") {
    $csproj = [xml](Get-Content $Project)
    $raw = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (!$raw) { throw "Could not read <Version> from PitLaunch.csproj" }
    $PackVersion = $raw
}
Write-Host "Packing PitLaunch $PackVersion" -ForegroundColor Cyan

Assert-ChildPath $Staging $Workspace
if (Test-Path -LiteralPath $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Staging, $ReleaseDir | Out-Null

# NOT single-file: Velopack diffs per file, so loose files make updates small.
& $DotNet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $Staging
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

$PublishedExe = Join-Path $Staging "PitLaunch.exe"
if (!(Test-Path -LiteralPath $PublishedExe)) { throw "Publish completed without producing PitLaunch.exe" }

$previousFull = @(Get-ChildItem -Path $ReleaseDir -Filter "*-full.nupkg" -ErrorAction SilentlyContinue)
Write-Host "Existing full packages in feed: $($previousFull.Count)"

$icon = Join-Path $Root "assets\pitlaunch-v3.ico"
& $Vpk pack `
    --packId PitLaunch `
    --packVersion $PackVersion `
    --packDir $Staging `
    --packTitle "PitLaunch" `
    --packAuthors "PitLaunch" `
    --mainExe "PitLaunch.exe" `
    --icon $icon `
    --channel $Channel `
    --outputDir $ReleaseDir
if ($LASTEXITCODE -ne 0) { throw "Velopack pack failed with exit code $LASTEXITCODE" }

Remove-Item -LiteralPath $Staging -Recurse -Force

Write-Host ""
Write-Host "Release feed: $ReleaseDir" -ForegroundColor Green
Get-ChildItem $ReleaseDir | Sort-Object Length | ForEach-Object {
    "{0,12:N0} KB  {1}" -f ($_.Length / 1KB), $_.Name
}
$delta = Get-ChildItem -Path $ReleaseDir -Filter "*$PackVersion*-delta.nupkg" -ErrorAction SilentlyContinue
if ($delta) {
    $full = Get-ChildItem -Path $ReleaseDir -Filter "*$PackVersion*-full.nupkg"
    $pct = [Math]::Round(100 * $delta[0].Length / $full[0].Length, 1)
    Write-Host ""
    Write-Host ("Delta update is {0:N1} MB vs {1:N1} MB full ({2}% of a fresh download)." -f ($delta[0].Length/1MB), ($full[0].Length/1MB), $pct) -ForegroundColor Green
} elseif ($previousFull.Count -eq 0) {
    Write-Host "First release - no delta yet. The next version built here will produce one." -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Give users Setup.exe. Upload the whole folder for updates." -ForegroundColor Cyan

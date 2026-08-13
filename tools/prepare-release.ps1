param(
    [string]$ReleaseDir = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Workspace = Split-Path -Parent (Split-Path -Parent $Root)
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = Join-Path $Workspace "outputs\releases"
}

function Assert-Success([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE" }
}

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\')
    if (!$fullPath.StartsWith($fullParent + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside $fullParent`: $fullPath"
    }
}

$project = [xml](Get-Content -LiteralPath (Join-Path $Root "PitLaunch.csproj"))
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read the project version." }

$appInfo = Get-Content -LiteralPath (Join-Path $Root "app\AppInfo.cs") -Raw
$manifest = Get-Content -LiteralPath (Join-Path $Root "app\app.manifest") -Raw
if (-not $appInfo.Contains("Version = `"$version`"")) { throw "AppInfo.Version does not match $version." }
if (-not $manifest.Contains("version=`"$version.0`"")) { throw "app.manifest does not match $version.0." }

Assert-ChildPath $ReleaseDir $Workspace
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

if (-not $SkipTests) {
    & dotnet build (Join-Path $Root "PitLaunch.csproj") -c Release -r win-x64
    Assert-Success "Desktop Release build"
    & dotnet (Join-Path $Root "bin\Release\net8.0-windows\win-x64\PitLaunch.dll") --self-test --output (Join-Path $Root "artifacts\self-test-release-$version.json")
    Assert-Success "Desktop self-test"
    & (Join-Path $Root "tools\verify-comparison.ps1")
    Assert-Success "Comparison-copy verification"
}

& (Join-Path $Root "build.ps1")
Assert-Success "Portable build"
& (Join-Path $Root "build-installer.ps1") -PackVersion $version -ReleaseDir $ReleaseDir
Assert-Success "Installer build"

$pluginRoot = Join-Path $Root "integrations\stream-deck"
Push-Location $pluginRoot
try {
    & npm.cmd ci
    Assert-Success "Stream Deck dependency install"
    if (-not $SkipTests) {
        & npm.cmd test
        Assert-Success "Stream Deck tests"
        & npm.cmd run validate
        Assert-Success "Stream Deck validation"
    }
    & npm.cmd run pack
    Assert-Success "Stream Deck package"
} finally {
    Pop-Location
}

$portable = Join-Path $Workspace "outputs\PitLaunch-win-Portable.zip"
$plugin = Join-Path $pluginRoot "dist\com.cevzom.pitlaunch.streamDeckPlugin"
foreach ($asset in @($portable, $plugin)) {
    if (!(Test-Path -LiteralPath $asset)) { throw "Expected release asset is missing: $asset" }
    Copy-Item -LiteralPath $asset -Destination (Join-Path $ReleaseDir (Split-Path -Leaf $asset)) -Force
}

$hashFile = Join-Path $ReleaseDir "SHA256SUMS.txt"
if (Test-Path -LiteralPath $hashFile) { Remove-Item -LiteralPath $hashFile -Force }
$assets = @(Get-ChildItem -LiteralPath $ReleaseDir -File | Sort-Object Name)
$hashLines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
}
Set-Content -LiteralPath $hashFile -Value $hashLines -Encoding ASCII

$notesDir = Join-Path $Root "artifacts"
New-Item -ItemType Directory -Force -Path $notesDir | Out-Null
$notesFile = Join-Path $notesDir "release-notes-$version.md"
$currentAssets = @(
    Get-Item -LiteralPath (Join-Path $ReleaseDir "PitLaunch-win-Setup.exe"),
        (Join-Path $ReleaseDir "PitLaunch-win-Portable.zip"),
        (Join-Path $ReleaseDir "com.cevzom.pitlaunch.streamDeckPlugin"),
        (Join-Path $ReleaseDir "PitLaunch-$version-full.nupkg")
)
$deltaPath = Join-Path $ReleaseDir "PitLaunch-$version-delta.nupkg"
if (Test-Path -LiteralPath $deltaPath) { $currentAssets += Get-Item -LiteralPath $deltaPath }

$notes = @(
    "# PitLaunch $version",
    "",
    "The first public release of the whole-PC Desk <-> Rig switcher.",
    "",
    "- Guided first-run setup with displays, audio, apps, hardware readiness, game presets, Discord, hotkeys, and Stream Deck.",
    "- Manual switch confirmation; automatic game, hotkey, CLI, and Stream Deck paths remain prompt-free.",
    "- Preflight, rollback, restart-safe Undo, mandatory-update safety gate, support bundles, and emergency display recovery.",
    "",
    "> PitLaunch is not code-signed yet. Windows SmartScreen may warn. Verify the SHA256 below before choosing **More info > Run anyway**.",
    "",
    "## SHA256",
    "",
    "| Asset | Size | SHA256 |",
    "|---|---:|---|"
)
foreach ($asset in $currentAssets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = "{0:N1} MB" -f ($asset.Length / 1MB)
    $notes += "| ``$($asset.Name)`` | $size | ``$hash`` |"
}
$notes += @(
    "",
    "`SHA256SUMS.txt` contains hashes for every release-feed asset.",
    "",
    "[Installation and verification instructions](https://github.com/Cevzom/PitLaunch#download)"
)
Set-Content -LiteralPath $notesFile -Value $notes -Encoding UTF8

Write-Host ""
Write-Host "PitLaunch $version release assets are ready in $ReleaseDir" -ForegroundColor Green
Write-Host "Release notes: $notesFile" -ForegroundColor Green
Write-Host "Hashes: $hashFile" -ForegroundColor Green

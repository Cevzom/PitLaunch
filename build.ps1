$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Workspace = Split-Path -Parent (Split-Path -Parent $Root)
$Project = Join-Path $Root "PitLaunch.csproj"
$Output = Join-Path $Workspace "outputs\PitLaunch-Beta"
$Staging = Join-Path $Root "artifacts\publish"
# Read the version from the project so the zip name can never go stale against the build.
$ProjectVersion = ([xml](Get-Content $Project)).Project.PropertyGroup |
    Where-Object { $_.Version } | Select-Object -First 1 -ExpandProperty Version
if (!$ProjectVersion) { throw "Could not read <Version> from PitLaunch.csproj" }
$ShortVersion = ($ProjectVersion -split '-')[0]
$Zip = Join-Path $Workspace "outputs\PitLaunch-Beta-$ShortVersion.zip"
$LocalDotNet = Join-Path $Workspace ".tools\dotnet\dotnet.exe"

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
    if ($null -eq $DotNetCommand) {
        throw "The .NET 8 SDK is required. Install it or place it at $LocalDotNet"
    }
    $DotNet = $DotNetCommand.Source
}

Assert-ChildPath $Output $Workspace
Assert-ChildPath $Staging $Workspace
Assert-ChildPath $Zip $Workspace

foreach ($path in @($Output, $Staging)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
if (Test-Path -LiteralPath $Zip) {
    Remove-Item -LiteralPath $Zip -Force
}

New-Item -ItemType Directory -Force -Path $Output, $Staging | Out-Null

& $DotNet restore $Project -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw "Restore failed with exit code $LASTEXITCODE"
}

& $DotNet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $Staging
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE"
}

$PublishedExe = Join-Path $Staging "PitLaunch.exe"
if (!(Test-Path -LiteralPath $PublishedExe)) {
    throw "Publish completed without producing PitLaunch.exe"
}

Copy-Item -LiteralPath $PublishedExe -Destination (Join-Path $Output "PitLaunch.exe") -Force
Copy-Item -LiteralPath (Join-Path $Root "README.md") -Destination (Join-Path $Output "README.md") -Force
Copy-Item -LiteralPath (Join-Path $Root "START-HERE.txt") -Destination (Join-Path $Output "START-HERE.txt") -Force

$Exe = Get-Item -LiteralPath (Join-Path $Output "PitLaunch.exe")
$Hash = (Get-FileHash -LiteralPath $Exe.FullName -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $Output "SHA256.txt") -Encoding ASCII -Value "PitLaunch.exe  SHA256  $Hash"

Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $Zip -CompressionLevel Optimal

Remove-Item -LiteralPath $Staging -Recurse -Force
Write-Host "Built $($Exe.FullName) ($([Math]::Round($Exe.Length / 1MB, 1)) MB)"
Write-Host "SHA256 $Hash"
Write-Host "Packaged $Zip"

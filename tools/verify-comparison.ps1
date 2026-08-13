param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
)

$ErrorActionPreference = "Stop"

$dataPath = Join-Path $Root "docs\comparison.json"
$readmePath = Join-Path $Root "README.md"
$websitePath = Join-Path $Root "web\public\index.html"

$data = Get-Content -LiteralPath $dataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$readme = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
$website = Get-Content -LiteralPath $websitePath -Raw -Encoding UTF8

function Normalize-Copy([string]$value) {
    $decoded = [System.Net.WebUtility]::HtmlDecode($value)
    $decoded = [regex]::Replace($decoded, '<[^>]+>', ' ')
    $decoded = $decoded.Replace('**', '')
    return [regex]::Replace($decoded, '\s+', ' ').Trim()
}

$readme = Normalize-Copy $readme
$website = Normalize-Copy $website

$copy = @($data.columns)
foreach ($row in $data.rows) {
    $copy += $row.need
    $copy += $row.pitlaunch
    $copy += $row.displayMagician
    $copy += $row.simLauncher
}
$copy += $data.summary
$copy += $data.verification

$missing = @()
foreach ($text in $copy | Select-Object -Unique) {
    $normalized = Normalize-Copy $text
    if (-not $readme.Contains($normalized)) { $missing += "README: $text" }
    if (-not $website.Contains($normalized)) { $missing += "website: $text" }
}

if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Error "Comparison copy is out of sync: $_" }
    exit 1
}

Write-Host "README and website comparison wording match docs/comparison.json." -ForegroundColor Green

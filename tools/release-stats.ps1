# How many people are running PitLaunch, without tracking anybody.
#
# Every installed copy that updates downloads a delta package from the GitHub release, and
# nothing else does. So the delta download count of a release is a close read on how many
# live installs were out there when it shipped. Fresh installs show up as Setup.exe
# downloads, and portable users show up as zip downloads.
#
#   .\release-stats.ps1              -> summary + per release
#   .\release-stats.ps1 -Detailed    -> every asset
#
# Reads only public data. Set GITHUB_TOKEN to raise the API rate limit (60/hour without one).

param(
    [string]$Repo = "Cevzom/PitLaunch",
    [switch]$Detailed
)

$ErrorActionPreference = "Stop"

$headers = @{ "User-Agent" = "PitLaunch-release-stats"; "Accept" = "application/vnd.github+json" }
if ($env:GITHUB_TOKEN) { $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN" }

try {
    $releases = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases?per_page=100" -Headers $headers -TimeoutSec 30
} catch {
    Write-Host "Could not read releases for $Repo : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "If this is a rate limit, set GITHUB_TOKEN and try again." -ForegroundColor Yellow
    exit 1
}

if (-not $releases -or $releases.Count -eq 0) {
    Write-Host "No releases published yet for $Repo." -ForegroundColor Yellow
    Write-Host "Publish one with the contents of outputs\releases\ and numbers appear here." -ForegroundColor Yellow
    exit 0
}

function Kind([string]$name) {
    if ($name -match 'Setup\.exe$')       { return "install" }
    if ($name -match '-delta\.nupkg$')    { return "update" }
    if ($name -match '-full\.nupkg$')     { return "repair" }
    if ($name -match 'Portable.*\.zip$' -or $name -match '^PitLaunch-Beta.*\.zip$') { return "portable" }
    return "other"
}

$rows = foreach ($r in $releases) {
    $releaseVersion = [string]$r.tag_name -replace '^v', ''
    foreach ($a in $r.assets) {
        [pscustomobject]@{
            Release   = $r.tag_name
            Published = if ($r.published_at) { [datetime]$r.published_at } else { $null }
            Asset     = $a.name
            Kind      = Kind $a.name
            IsReleaseDelta = $a.name -ieq "PitLaunch-$releaseVersion-delta.nupkg"
            IsReleaseFull  = $a.name -ieq "PitLaunch-$releaseVersion-full.nupkg"
            Downloads = [int]$a.download_count
        }
    }
}

$installs  = ($rows | Where-Object Kind -eq "install"  | Measure-Object Downloads -Sum).Sum
$updates   = ($rows | Where-Object IsReleaseDelta      | Measure-Object Downloads -Sum).Sum
$repairs   = ($rows | Where-Object IsReleaseFull       | Measure-Object Downloads -Sum).Sum
$portable  = ($rows | Where-Object Kind -eq "portable" | Measure-Object Downloads -Sum).Sum
foreach ($v in 'installs','updates','repairs','portable') {
    if ($null -eq (Get-Variable $v -ValueOnly)) { Set-Variable $v 0 }
}

# The newest release that actually carries a delta is the freshest read on live installs.
$latestDelta = $rows | Where-Object IsReleaseDelta |
    Sort-Object Published -Descending | Select-Object -First 1

Write-Host ""
Write-Host "  PitLaunch  ->  $Repo" -ForegroundColor Cyan
Write-Host "  ---------------------------------------------" -ForegroundColor DarkGray
Write-Host ("  Installed via Setup.exe   {0,6}" -f $installs)
Write-Host ("  Update downloads          {0,6}   (a live install patching itself)" -f $updates)
Write-Host ("  Full-package downloads    {0,6}   (install that could not patch)" -f $repairs)
Write-Host ("  Portable zip downloads    {0,6}   (never auto-updates)" -f $portable)
Write-Host "  ---------------------------------------------" -ForegroundColor DarkGray
if ($latestDelta) {
    Write-Host ("  ACTIVE INSTALLS  ~{0}" -f $latestDelta.Downloads) -ForegroundColor Green
    Write-Host ("  from the {0} update, published {1:yyyy-MM-dd}" -f $latestDelta.Release, $latestDelta.Published) -ForegroundColor DarkGray
} else {
    Write-Host "  ACTIVE INSTALLS  not measurable yet" -ForegroundColor Yellow
    Write-Host "  Ship one more version: its delta count is the read." -ForegroundColor DarkGray
}
Write-Host ""

Write-Host "  Per release" -ForegroundColor Cyan
$releases | ForEach-Object {
    $tag = $_.tag_name
    $mine = $rows | Where-Object Release -eq $tag
    $tot = ($mine | Measure-Object Downloads -Sum).Sum
    if ($null -eq $tot) { $tot = 0 }
    $when = if ($_.published_at) { ([datetime]$_.published_at).ToString("yyyy-MM-dd") } else { "draft" }
    $d = ($mine | Where-Object IsReleaseDelta | Measure-Object Downloads -Sum).Sum
    $i = ($mine | Where-Object Kind -eq "install" | Measure-Object Downloads -Sum).Sum
    if ($null -eq $d) { $d = 0 }; if ($null -eq $i) { $i = 0 }
    Write-Host ("   {0,-22} {1}   installer {2,4}   live estimate {3,4}   all assets {4,5}" -f $tag, $when, $i, $d, $tot)
}
Write-Host ""

if ($Detailed) {
    Write-Host "  Every asset" -ForegroundColor Cyan
    $rows | Sort-Object -Property @{Expression='Published';Descending=$true}, @{Expression='Asset';Descending=$false} |
        Format-Table @{n='Release';e={$_.Release}}, @{n='Asset';e={$_.Asset}}, @{n='Kind';e={$_.Kind}}, @{n='Downloads';e={$_.Downloads}} -AutoSize
}

Write-Host "  Installer is the GitHub API download total for Setup.exe assets in that release." -ForegroundColor DarkGray
Write-Host "  Live estimate is that release's delta downloads; it becomes meaningful after an update ships." -ForegroundColor DarkGray
Write-Host "  Counts include anyone who grabbed a file - mirrors, bots, you." -ForegroundColor DarkGray
Write-Host ""

# Draws the plugin's icon set.
#
# These are placeholders in the sense that a designer could do better, but they are real assets:
# correct sizes, correct @2x pairs, PitLaunch's cyan, and a green variant for the active state.
# Re-run after changing any size or colour:  .\tools\generate-icons.ps1

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Imgs = Join-Path $Root "com.cevzom.pitlaunch.sdPlugin\imgs"

$Cyan = [System.Drawing.ColorTranslator]::FromHtml("#20B8F0")
$Green = [System.Drawing.ColorTranslator]::FromHtml("#3FB950")
$Amber = [System.Drawing.ColorTranslator]::FromHtml("#E7B86C")
$Canvas = [System.Drawing.ColorTranslator]::FromHtml("#12161A")

function New-Monitor {
    param(
        [int]$Size,
        [System.Drawing.Color]$Accent,
        [switch]$Transparent,
        [switch]$Emergency
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    if ($Transparent) { $g.Clear([System.Drawing.Color]::Transparent) } else { $g.Clear($Canvas) }

    $unit = $Size / 72.0
    $penWidth = [Math]::Max(1.0, 4.0 * $unit)
    $pen = New-Object System.Drawing.Pen($Accent, $penWidth)
    $brush = New-Object System.Drawing.SolidBrush($Accent)

    # Wide screen, offset up to leave room for the stand.
    $w = 44 * $unit
    $h = 28 * $unit
    $x = ($Size - $w) / 2
    $y = ($Size - $h) / 2 - (5 * $unit)
    $g.DrawRectangle($pen, $x, $y, $w, $h)

    if ($Emergency) {
        # A bar through the screen: reads as "put it back" rather than "switch to".
        $g.FillRectangle($brush, $x + (6 * $unit), $y + ($h / 2) - (2 * $unit), $w - (12 * $unit), 4 * $unit)
    } else {
        # A second, smaller screen beside it: the whole point is more than one monitor.
        $g.FillRectangle($brush, $x + (6 * $unit), $y + (6 * $unit), (14 * $unit), (10 * $unit))
    }

    # Stand.
    $g.FillRectangle($brush, ($Size / 2) - (2 * $unit), $y + $h, 4 * $unit, 6 * $unit)
    $g.FillRectangle($brush, ($Size / 2) - (11 * $unit), $y + $h + (6 * $unit), 22 * $unit, 3.5 * $unit)

    $pen.Dispose(); $brush.Dispose(); $g.Dispose()
    return $bitmap
}

function Save-Pair {
    param([string]$Path, [int]$Size, [System.Drawing.Color]$Accent, [switch]$Transparent, [switch]$Emergency)

    $dir = Split-Path -Parent $Path
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    foreach ($pair in @(@{ n = "$Path.png"; s = $Size }, @{ n = "$Path@2x.png"; s = $Size * 2 })) {
        $bmp = New-Monitor -Size $pair.s -Accent $Accent -Transparent:$Transparent -Emergency:$Emergency
        $bmp.Save($pair.n, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        "  {0,-52} {1}x{1}" -f (Split-Path -Leaf $pair.n), $pair.s
    }
}

"Writing icons to $Imgs"
Save-Pair -Path (Join-Path $Imgs "plugin\marketplace") -Size 288 -Accent $Cyan
Save-Pair -Path (Join-Path $Imgs "plugin\category-icon") -Size 28 -Accent $Cyan -Transparent
Save-Pair -Path (Join-Path $Imgs "actions\switch-setup\icon") -Size 20 -Accent $Cyan -Transparent
Save-Pair -Path (Join-Path $Imgs "actions\switch-setup\key") -Size 72 -Accent $Cyan
Save-Pair -Path (Join-Path $Imgs "actions\switch-setup\key-active") -Size 72 -Accent $Green
Save-Pair -Path (Join-Path $Imgs "actions\restore-displays\icon") -Size 20 -Accent $Amber -Transparent -Emergency
Save-Pair -Path (Join-Path $Imgs "actions\restore-displays\key") -Size 72 -Accent $Amber -Emergency
"Done."

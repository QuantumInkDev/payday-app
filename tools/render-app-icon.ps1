# Render every PayDay app-icon asset from a single Segoe Fluent Icons glyph.
#
# Usage:
#   ./tools/render-app-icon.ps1                                  # default F156 piggy bank, accent purple
#   ./tools/render-app-icon.ps1 -Glyph E9F3 -BackgroundHex '#0F766E'
#
# Reusable for the real Phase 7+ icon — swap the glyph or replace with a
# proper SVG-rendered design.

[CmdletBinding()]
param(
    [string]$Glyph = "F156",
    [string]$BackgroundHex = "#6C5CE7",
    [string]$ForegroundHex = "#FFFFFF",
    [string]$AssetsDir = (Join-Path $PSScriptRoot "..\PayDay\Assets"),
    [string]$FontFamily = "Segoe Fluent Icons",
    [double]$GlyphFillRatio = 0.62
)

Add-Type -AssemblyName System.Drawing

function ConvertFrom-Hex {
    param([string]$Hex)
    $Hex = $Hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb(
        [Convert]::ToInt32($Hex.Substring(0, 2), 16),
        [Convert]::ToInt32($Hex.Substring(2, 2), 16),
        [Convert]::ToInt32($Hex.Substring(4, 2), 16))
}

$bgColor = ConvertFrom-Hex $BackgroundHex
$fgColor = ConvertFrom-Hex $ForegroundHex
$glyphChar = [char][Convert]::ToInt32($Glyph, 16)

function Render-Png {
    param([int]$Width, [int]$Height, [bool]$Plated, [string]$Path)

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    if ($Plated) {
        $bgBrush = New-Object System.Drawing.SolidBrush($bgColor)
        $g.FillRectangle($bgBrush, 0, 0, $Width, $Height)
        $bgBrush.Dispose()
    }

    $shortest = [Math]::Min($Width, $Height)
    $glyphSize = $shortest * $GlyphFillRatio
    $font = New-Object System.Drawing.Font($FontFamily, [single]$glyphSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fgBrush = New-Object System.Drawing.SolidBrush($fgColor)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center

    $rect = New-Object System.Drawing.RectangleF(0, 0, $Width, $Height)
    $g.DrawString($glyphChar, $font, $fgBrush, $rect, $fmt)

    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)

    $font.Dispose(); $fgBrush.Dispose(); $g.Dispose(); $bmp.Dispose()
}

# Manifest-referenced assets.
$targets = @(
    @{ Name = 'Square44x44Logo.scale-200.png';                          W=88;   H=88;  Plated=$true  }
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png';     W=24;   H=24;  Plated=$false }
    @{ Name = 'Square44x44Logo.targetsize-48_altform-lightunplated.png';W=48;   H=48;  Plated=$false }
    @{ Name = 'Square150x150Logo.scale-200.png';                        W=300;  H=300; Plated=$true  }
    @{ Name = 'Wide310x150Logo.scale-200.png';                          W=620;  H=300; Plated=$true  }
    @{ Name = 'SplashScreen.scale-200.png';                             W=1240; H=600; Plated=$true  }
    @{ Name = 'LockScreenLogo.scale-200.png';                           W=48;   H=48;  Plated=$true  }
    @{ Name = 'StoreLogo.png';                                          W=50;   H=50;  Plated=$true  }
)

if (-not (Test-Path $AssetsDir)) {
    throw "Assets directory not found: $AssetsDir"
}

foreach ($t in $targets) {
    $out = Join-Path $AssetsDir $t.Name
    Render-Png -Width $t.W -Height $t.H -Plated:$t.Plated -Path $out
    Write-Host "Wrote $($t.W)x$($t.H)  $($t.Name)"
}

# AppIcon.ico — multi-resolution PNG-embedded ICO. Used by AppWindow.SetIcon
# for the title bar / taskbar icon when the app is running.
$icoSizes = 16, 32, 48, 64, 256
$tmpPngs = @()
foreach ($s in $icoSizes) {
    $tmp = Join-Path $AssetsDir "_ico-$s.png"
    Render-Png -Width $s -Height $s -Plated:$true -Path $tmp
    $tmpPngs += @{ Size = $s; Path = $tmp; Bytes = [System.IO.File]::ReadAllBytes($tmp) }
}

$icoPath = Join-Path $AssetsDir 'AppIcon.ico'
$fs = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    # ICONDIR
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$tmpPngs.Count)

    $dirEntryBytes = 16
    $dataOffset = 6 + ($dirEntryBytes * $tmpPngs.Count)

    foreach ($entry in $tmpPngs) {
        $dim = if ($entry.Size -ge 256) { [byte]0 } else { [byte]$entry.Size }
        $bw.Write([byte]$dim)
        $bw.Write([byte]$dim)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$entry.Bytes.Length)
        $bw.Write([uint32]$dataOffset)
        $dataOffset += $entry.Bytes.Length
    }

    foreach ($entry in $tmpPngs) {
        $bw.Write($entry.Bytes)
    }
}
finally {
    $bw.Close()
    $fs.Close()
}

foreach ($entry in $tmpPngs) { Remove-Item $entry.Path }
Write-Host "Wrote $($tmpPngs.Count)-size  AppIcon.ico"

Write-Host ""
Write-Host "Glyph U+$Glyph rendered with background $BackgroundHex, foreground $ForegroundHex." -ForegroundColor Cyan

# One-off: renders a 5x2 grid of candidate Segoe Fluent Icons glyphs for the
# PayDay app icon, each on the app's accent purple, labeled with code + name.
# Output: tools/icon-candidates.png

[CmdletBinding()]
param(
    [string]$BackgroundHex = "#6C5CE7",
    [string]$ForegroundHex = "#FFFFFF",
    [string]$FontFamily = "Segoe Fluent Icons",
    [string]$OutPath = (Join-Path $PSScriptRoot "icon-candidates.png")
)

Add-Type -AssemblyName System.Drawing

$candidates = @(
    @{ Code='e825'; Name='Bank' }
    @{ Code='e8c7'; Name='PaymentCard' }
    @{ Code='f540'; Name='Safe' }
    @{ Code='e8ef'; Name='Calculator' }
    @{ Code='ec59'; Name='CashDrawer' }
    @{ Code='e8ec'; Name='Tag' }
    @{ Code='e910'; Name='Accounts' }
    @{ Code='e787'; Name='Calendar' }
    @{ Code='e9d2'; Name='AreaChart' }
    @{ Code='efff'; Name='ScheduleReports' }
)

$cell = 200
$pad = 16
$labelH = 40
$cols = 5
$rows = [Math]::Ceiling($candidates.Count / $cols)
$totalW = $cols * ($cell + $pad) + $pad
$totalH = $rows * ($cell + $labelH + $pad) + $pad

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
$sheetBg = [System.Drawing.Color]::FromArgb(245, 245, 250)
$labelColor = [System.Drawing.Color]::FromArgb(40, 40, 50)

$bmp = New-Object System.Drawing.Bitmap($totalW, $totalH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear($sheetBg)

$glyphFont = New-Object System.Drawing.Font($FontFamily, [single]($cell * 0.62), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$labelFont = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fgBrush = New-Object System.Drawing.SolidBrush($fgColor)
$bgBrush = New-Object System.Drawing.SolidBrush($bgColor)
$labelBrush = New-Object System.Drawing.SolidBrush($labelColor)

$centerFmt = New-Object System.Drawing.StringFormat
$centerFmt.Alignment = [System.Drawing.StringAlignment]::Center
$centerFmt.LineAlignment = [System.Drawing.StringAlignment]::Center

for ($i = 0; $i -lt $candidates.Count; $i++) {
    $col = $i % $cols
    $row = [Math]::Floor($i / $cols)
    $x = $pad + $col * ($cell + $pad)
    $y = $pad + $row * ($cell + $labelH + $pad)

    # Plate
    $g.FillRectangle($bgBrush, $x, $y, $cell, $cell)

    # Glyph
    $glyphChar = [char][Convert]::ToInt32($candidates[$i].Code, 16)
    $rect = New-Object System.Drawing.RectangleF($x, $y, $cell, $cell)
    $g.DrawString($glyphChar, $glyphFont, $fgBrush, $rect, $centerFmt)

    # Label
    $label = "$($candidates[$i].Code)  $($candidates[$i].Name)"
    $labelRect = New-Object System.Drawing.RectangleF($x, ($y + $cell), $cell, $labelH)
    $g.DrawString($label, $labelFont, $labelBrush, $labelRect, $centerFmt)
}

$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$glyphFont.Dispose(); $labelFont.Dispose(); $fgBrush.Dispose(); $bgBrush.Dispose(); $labelBrush.Dispose(); $g.Dispose(); $bmp.Dispose()

Write-Host "Wrote $OutPath"

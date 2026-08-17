# =============================================================================
# tools/make-store-assets.ps1
# =============================================================================
# Generates the PNG logo set that Package.appxmanifest references — required
# for MSIX packaging / Microsoft Store submission. Same design language as
# tools/make-icon.ps1: the Segoe Fluent "ReadingMode" open-book glyph (U+E736)
# in white on a rounded accent-blue tile.
#
# Outputs (into src/EslEpubReader/Assets/):
#   Square44x44Logo.png    — taskbar/start-list icon
#   Square71x71Logo.png    — small start tile
#   Square150x150Logo.png  — medium start tile
#   Square310x310Logo.png  — large start tile
#   Wide310x150Logo.png    — wide start tile (tile centered)
#   StoreLogo.png          — 50x50, shown in the Store/installer UI
#   SplashScreen.png       — 620x300, packaged-app launch splash
#
#   powershell -File tools/make-store-assets.ps1
# =============================================================================

Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "..\src\EslEpubReader\Assets"
$assets = [System.IO.Path]::GetFullPath($assets)
New-Item -ItemType Directory -Force $assets | Out-Null

$tileColor  = [System.Drawing.Color]::FromArgb(255, 15, 108, 189)   # #0F6CBD accent
$glyphColor = [System.Drawing.Color]::White
$glyph      = [string][char]0xE736                                  # open book

# Draw one asset: a canvas of W x H with the rounded tile+glyph square
# centered at the given edge length (defaults to the full smaller dimension).
function New-Asset([string]$name, [int]$w, [int]$h, [int]$tileEdge = 0) {
    if ($tileEdge -le 0) { $tileEdge = [Math]::Min($w, $h) }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile (22% corner radius), centered on the canvas.
    $x0 = [int](($w - $tileEdge) / 2); $y0 = [int](($h - $tileEdge) / 2)
    $r = [Math]::Max(2, [int]($tileEdge * 0.22)); $d = 2 * $r; $e = $tileEdge - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x0,           $y0,           $d, $d, 180, 90)
    $path.AddArc($x0 + $e - $d, $y0,           $d, $d, 270, 90)
    $path.AddArc($x0 + $e - $d, $y0 + $e - $d, $d, $d,   0, 90)
    $path.AddArc($x0,           $y0 + $e - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    $tileBrush = New-Object System.Drawing.SolidBrush($tileColor)
    $g.FillPath($tileBrush, $path)

    # Glyph centered inside the tile.
    $font = New-Object System.Drawing.Font(
        "Segoe Fluent Icons", [float]($tileEdge * 0.62),
        [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment     = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $glyphBrush = New-Object System.Drawing.SolidBrush($glyphColor)
    $rect = New-Object System.Drawing.RectangleF($x0, $y0, $tileEdge, $tileEdge)
    $g.DrawString($glyph, $font, $glyphBrush, $rect, $fmt)

    $out = Join-Path $assets $name
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $glyphBrush.Dispose(); $tileBrush.Dispose(); $font.Dispose()
    $path.Dispose(); $g.Dispose(); $bmp.Dispose()
    Write-Host "Wrote $name (${w}x${h})"
}

New-Asset "Square44x44Logo.png"   44   44
New-Asset "Square71x71Logo.png"   71   71
New-Asset "Square150x150Logo.png" 150  150
New-Asset "Square310x310Logo.png" 310  310
New-Asset "Wide310x150Logo.png"   310  150  120   # tile floats on the wide canvas
New-Asset "StoreLogo.png"         50   50
New-Asset "SplashScreen.png"      620  300  180   # tile floats on the splash

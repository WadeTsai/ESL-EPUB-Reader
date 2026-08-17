# =============================================================================
# tools/make-icon.ps1
# =============================================================================
# Generates src/EslEpubReader/Assets/app.ico — the application icon shown in
# the Windows taskbar, Alt+Tab, Explorer, and pinned shortcuts.
#
# The icon reproduces the app's in-window identity mark: the "ReadingMode"
# open-book glyph (U+E736 from the Segoe Fluent Icons font, the same glyph
# the title bar shows) drawn in white on a rounded accent-blue tile —
# the standard Windows 11 app-icon look, and readable down to 16x16.
#
# The .ico container holds one PNG-compressed image per size (Windows
# supports PNG entries since Vista). Re-run this script whenever the design
# changes; the result is committed so normal builds never need it.
#
#   powershell -File tools/make-icon.ps1
# =============================================================================

Add-Type -AssemblyName System.Drawing

$outPath = Join-Path $PSScriptRoot "..\src\EslEpubReader\Assets\app.ico"
$outPath = [System.IO.Path]::GetFullPath($outPath)
New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null

# Standard icon sizes Windows actually samples (256 is the Explorer "extra
# large" source; 16 is the taskbar-overflow / title-bar size).
$sizes = 16, 20, 24, 32, 48, 64, 128, 256

# Accent blue matching the WinUI light-theme accent text color (#0F6CBD).
$tileColor  = [System.Drawing.Color]::FromArgb(255, 15, 108, 189)
$glyphColor = [System.Drawing.Color]::White
$glyph      = [string][char]0xE736   # Segoe Fluent Icons "ReadingMode"

$pngFrames = @()   # list of @{ Size = n; Bytes = byte[] }

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- rounded-square tile (Windows 11 icon silhouette) -------------------
    # Corner radius ~22% of the edge, the proportion Win11 system icons use.
    $r = [Math]::Max(2, [int]($s * 0.22)); $d = 2 * $r; $e = $s - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0,       0,       $d, $d, 180, 90)   # top-left corner
    $path.AddArc($e - $d, 0,       $d, $d, 270, 90)   # top-right
    $path.AddArc($e - $d, $e - $d, $d, $d,   0, 90)   # bottom-right
    $path.AddArc(0,       $e - $d, $d, $d,  90, 90)   # bottom-left
    $path.CloseFigure()
    $tileBrush = New-Object System.Drawing.SolidBrush($tileColor)
    $g.FillPath($tileBrush, $path)

    # --- the open-book glyph, centered ---------------------------------------
    # 62% of the edge leaves comfortable padding inside the tile.
    $font = New-Object System.Drawing.Font(
        "Segoe Fluent Icons", [float]($s * 0.62),
        [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment     = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $glyphBrush = New-Object System.Drawing.SolidBrush($glyphColor)
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $g.DrawString($glyph, $font, $glyphBrush, $rect, $fmt)

    # --- capture as PNG bytes ------------------------------------------------
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngFrames += , @{ Size = $s; Bytes = $ms.ToArray() }

    $ms.Dispose(); $glyphBrush.Dispose(); $tileBrush.Dispose()
    $font.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
}

# --- assemble the .ico container ---------------------------------------------
# Layout: ICONDIR header (6 bytes) + one ICONDIRENTRY (16 bytes) per image
# + the raw PNG payloads. All integers little-endian.
$fs = [System.IO.File]::Create($outPath)
$w = New-Object System.IO.BinaryWriter($fs)

$w.Write([uint16]0)                    # reserved, must be 0
$w.Write([uint16]1)                    # type 1 = icon
$w.Write([uint16]$pngFrames.Count)     # image count

$dataOffset = 6 + 16 * $pngFrames.Count
foreach ($f in $pngFrames) {
    # width/height bytes: 0 means 256 (the byte can't hold 256).
    $edge = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$edge)              # width
    $w.Write([byte]$edge)              # height
    $w.Write([byte]0)                  # palette size (none — true color)
    $w.Write([byte]0)                  # reserved
    $w.Write([uint16]1)                # color planes
    $w.Write([uint16]32)               # bits per pixel
    $w.Write([uint32]$f.Bytes.Length)  # payload size
    $w.Write([uint32]$dataOffset)      # payload offset
    $dataOffset += $f.Bytes.Length
}
foreach ($f in $pngFrames) { $w.Write($f.Bytes) }

$w.Dispose(); $fs.Dispose()
Write-Host "Wrote $outPath ($([math]::Round((Get-Item $outPath).Length / 1KB, 1)) KB, $($pngFrames.Count) sizes)"

# Generates the JustPlay app/installer icon — the brand mark in the DEFAULT (Aurora)
# theme: a cyan→pink rounded chip with a white play triangle, the same mark
# ThemedWindowIcon renders live at runtime. Produces a multi-resolution PNG-compressed
# .ico (16…256) committed at src\JustPlay.App\Assets\justplay.ico.
#
# Re-run only if the brand mark changes. Uses GDI+ (System.Drawing), Windows-only.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root "src\JustPlay.App\Assets\justplay.ico"
$sizes = 256, 128, 64, 48, 32, 24, 16

# Aurora accents (App.axaml: AccentA cyan #6ce0ff → AccentB pink #ff5fae).
$cyan = [System.Drawing.Color]::FromArgb(0x6C, 0xE0, 0xFF)
$pink = [System.Drawing.Color]::FromArgb(0xFF, 0x5F, 0xAE)

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-rect chip (full bleed). Corner radius ≈ 0.22·size mirrors the in-app chip.
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $d = [float]($s * 0.22 * 2)   # arc diameter
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $cyan, $pink, 45.0)
    $g.FillPath($brush, $path)

    # White play triangle (matches the M0,0 L0,10 L9,5 glyph): vertical left edge,
    # apex mid-right, optically nudged right of centre.
    $cx = $s / 2.0; $cy = $s / 2.0
    $t = $s * 0.34
    $half = $t * 0.5
    $leftX  = [float]($cx - $t * 0.34)
    $apexX  = [float]($cx + $t * 0.46)
    $pts = @(
        (New-Object System.Drawing.PointF($leftX, [float]($cy - $half))),
        (New-Object System.Drawing.PointF($leftX, [float]($cy + $half))),
        (New-Object System.Drawing.PointF($apexX, [float]$cy))
    )
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillPolygon($white, $pts)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)

    $brush.Dispose(); $white.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
    return , $ms.ToArray()
}

# Render each size to a PNG blob.
$blobs = @{}
foreach ($s in $sizes) { $blobs[$s] = New-IconPng $s }

# Assemble the .ico container (ICONDIR + entries + PNG data, same iteration order).
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type: 1 = icon
$bw.Write([uint16]$sizes.Count)   # image count

$offset = 6 + (16 * $sizes.Count) # data starts after the directory
foreach ($s in $sizes) {
    $data = $blobs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }  # 0 means 256 in the ICO format
    $bw.Write([byte]$dim)         # width
    $bw.Write([byte]$dim)         # height
    $bw.Write([byte]0)            # palette count
    $bw.Write([byte]0)            # reserved
    $bw.Write([uint16]1)          # colour planes
    $bw.Write([uint16]32)         # bits per pixel
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($blobs[$s]) }
$bw.Flush()

[System.IO.File]::WriteAllBytes($out, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()

$kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host ("Wrote {0} ({1} KB, sizes: {2})" -f $out, $kb, ($sizes -join ', ')) -ForegroundColor Green

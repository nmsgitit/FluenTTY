# Generates FluenTTY\Assets\FluenTTY.ico and FluenTTY.png: the same rounded accent tile as FluentPlay's
# icon (same size, radius and blue gradient) with a terminal prompt on it — a right angle bracket and
# an underscore cursor line (">_").
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "..\FluenTTY\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.04))
    $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $radius = [Math]::Max(2, [int]($size * 0.22))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 0, 78, 160)), 60
    $g.FillPath($brush, $path)

    # Stroke weight scales with the tile but never drops below ~1.6 px so 16 px stays legible.
    $stroke = [Math]::Max(1.6, $size * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'

    # Right angle bracket ">"
    $pts = @(
        (New-Object System.Drawing.PointF ($size * 0.27), ($size * 0.31)),
        (New-Object System.Drawing.PointF ($size * 0.47), ($size * 0.50)),
        (New-Object System.Drawing.PointF ($size * 0.27), ($size * 0.69))
    )
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    # Cursor line "_"
    $g.DrawLine($pen, [single]($size * 0.53), [single]($size * 0.69), [single]($size * 0.74), [single]($size * 0.69))

    $pen.Dispose()
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = Draw-Tile $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $s; Bytes = $ms.ToArray() }
    if ($s -eq 256) { $bmp.Save((Join-Path $assets "FluenTTY.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO container with PNG-compressed entries
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$p.Bytes.Length); $w.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $w.Write($p.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "FluenTTY.ico"), $out.ToArray())
Write-Host "Wrote $(Join-Path $assets 'FluenTTY.ico') ($($out.Length) bytes)"

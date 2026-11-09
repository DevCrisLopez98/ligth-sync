Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$icoPath = Join-Path $root 'src\LightSync\Assets\LightSync.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $icoPath) | Out-Null

function Add-RoundedRect([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
}

function New-MarkPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.06))
    $tile = $size - (2 * $pad)
    $tilePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $tilePath $pad $pad $tile $tile ($tile * 14 / 48)
    $graphics.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 23, 34))), $tilePath)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 34, 42, 57)), ([Math]::Max(1, $size / 64))
    $graphics.DrawPath($pen, $tilePath)

    $mark = $tile * 0.5
    $origin = ($size - $mark) / 2
    $markPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $markPath $origin $origin $mark $mark ($mark * 7 / 24)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF $origin, $origin),
        (New-Object System.Drawing.PointF ($origin + $mark), ($origin + $mark)),
        ([System.Drawing.Color]::FromArgb(255, 255, 77, 109)),
        ([System.Drawing.Color]::FromArgb(255, 162, 59, 255)))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 5
    $blend.Colors = @(
        [System.Drawing.Color]::FromArgb(255, 255, 77, 109),
        [System.Drawing.Color]::FromArgb(255, 255, 176, 32),
        [System.Drawing.Color]::FromArgb(255, 52, 211, 153),
        [System.Drawing.Color]::FromArgb(255, 56, 189, 248),
        [System.Drawing.Color]::FromArgb(255, 162, 59, 255)
    )
    $blend.Positions = @(0, 0.28, 0.52, 0.74, 1)
    $brush.InterpolationColors = $blend
    $graphics.FillPath($brush, $markPath)

    $brush.Dispose()
    $pen.Dispose()
    $markPath.Dispose()
    $tilePath.Dispose()
    $graphics.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,$bytes
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $pngs += ,(New-MarkPng $size)
}

$offset = 6 + (16 * $pngs.Count)
$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $ico
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$pngs.Count)
for ($i = 0; $i -lt $pngs.Count; $i++) {
    $dimension = $sizes[$i]
    $writer.Write([byte]($(if ($dimension -ge 256) { 0 } else { $dimension })))
    $writer.Write([byte]($(if ($dimension -ge 256) { 0 } else { $dimension })))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$pngs[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) {
    $writer.Write($png)
}
$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())
$writer.Dispose()
Write-Output $icoPath

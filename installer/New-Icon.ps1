Add-Type -AssemblyName System.Drawing

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Test-Path (Join-Path $root 'logo.jpg'))) {
    $root = Split-Path $PSScriptRoot -Parent
}
$logoPath = Join-Path $root 'logo.jpg'
$icoPath = Join-Path $root 'src\LightSync\Assets\LightSync.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $icoPath) | Out-Null

$source = [System.Drawing.Image]::FromFile($logoPath)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 8, 12, 24))
    $side = [Math]::Min($source.Width, [int]($source.Height * 0.62))
    $sx = [int](($source.Width - $side) / 2)
    $sy = [int]($source.Height * 0.06)
    $dest = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $src = New-Object System.Drawing.Rectangle $sx, $sy, $side, $side
    $graphics.DrawImage($source, $dest, $src, [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $pngs += ,$stream.ToArray()
    $stream.Dispose()
}
$source.Dispose()

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

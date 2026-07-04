param(
    [string]$SourcePng = "$PSScriptRoot\..\src\Wispclip\Assets\wispclip.png",
    [string]$OutIco = "$PSScriptRoot\..\src\Wispclip\Assets\wispclip.ico"
)

Add-Type -AssemblyName System.Drawing

# GDI+'s bicubic filter aliases thin strokes away when asked to shrink an image
# by a large ratio in one jump (e.g. 1024 -> 16px is 64x). Halving repeatedly
# keeps every single step within bicubic's safe range so nothing thin gets lost.
function Resize-HighQuality([System.Drawing.Image]$Source, [int]$TargetSize) {
    $current = New-Object System.Drawing.Bitmap $Source
    while (($current.Width / 2) -ge $TargetSize -and ($current.Width / 2) -ge 1) {
        $half = [int]($current.Width / 2)
        $next = New-Object System.Drawing.Bitmap $half, $half, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($next)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($current, 0, 0, $half, $half)
        $g.Dispose()
        $current.Dispose()
        $current = $next
    }
    if ($current.Width -ne $TargetSize) {
        $final = New-Object System.Drawing.Bitmap $TargetSize, $TargetSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($final)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($current, 0, 0, $TargetSize, $TargetSize)
        $g.Dispose()
        $current.Dispose()
        $current = $final
    }
    return $current
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$source = [System.Drawing.Image]::FromFile($SourcePng)

$pngBlobs = @()
foreach ($size in $sizes) {
    $canvas = Resize-HighQuality -Source $source -TargetSize $size

    $ms = New-Object System.IO.MemoryStream
    $canvas.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBlobs += , @{ Size = $size; Bytes = $ms.ToArray() }
    $canvas.Dispose()
}
$source.Dispose()

$fs = New-Object System.IO.FileStream $OutIco, ([System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $fs

# ICONDIR
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type = icon
$bw.Write([UInt16]$pngBlobs.Count)

$headerSize = 6
$entrySize = 16
$offset = $headerSize + ($entrySize * $pngBlobs.Count)

foreach ($blob in $pngBlobs) {
    $dim = if ($blob.Size -ge 256) { 0 } else { $blob.Size }
    $bw.Write([Byte]$dim)             # width (0 = 256)
    $bw.Write([Byte]$dim)             # height (0 = 256)
    $bw.Write([Byte]0)                # color count
    $bw.Write([Byte]0)                # reserved
    $bw.Write([UInt16]1)              # color planes
    $bw.Write([UInt16]32)             # bits per pixel
    $bw.Write([UInt32]$blob.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $blob.Bytes.Length
}

foreach ($blob in $pngBlobs) {
    $bw.Write($blob.Bytes)
}

$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output "Wrote $OutIco ($((Get-Item $OutIco).Length) bytes, sizes: $($sizes -join ', '))"

# Regenerates Assets/wispclip-small.png (128x128) from the master Assets/wispclip.png.
#
# The master includes the full "wispclip" wordmark lockup, which is great for the big
# app icon / README banner but becomes illegible noise at the tiny sizes this asset is
# used at (header brand mark ~38px, library empty state ~40px). This script finds the
# icon mark (the swirl + dot) by locating the horizontal gap that separates it from the
# wordmark band below, crops to just the mark with a bit of padding, then downsamples in
# successive 2x halving steps -- GDI+'s bicubic filter aliases thin strokes away when
# asked to shrink by a huge ratio in one jump, so keeping every step close to 2x keeps
# the logo's thin ring stroke intact.
Add-Type -AssemblyName System.Drawing

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

$srcPath = "$PSScriptRoot\..\src\Wispclip\Assets\wispclip.png"
$outPath = "$PSScriptRoot\..\src\Wispclip\Assets\wispclip-small.png"

$src = [System.Drawing.Image]::FromFile($srcPath)
$bmp = New-Object System.Drawing.Bitmap $src
$w = $bmp.Width
$h = $bmp.Height

$rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
$bmpData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bytes = New-Object byte[] ($bmpData.Stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($bmpData.Scan0, $bytes, 0, $bytes.Length)
$bmp.UnlockBits($bmpData)
$stride = $bmpData.Stride

# Find content bands (rows with any non-black pixel), separated by all-black gaps.
$bands = New-Object System.Collections.ArrayList
$inBand = $false
$start = 0
for ($y = 0; $y -lt $h; $y++) {
    $rowHas = $false
    $rowOffset = $y * $stride
    for ($x = 0; $x -lt $w; $x += 2) {
        $idx = $rowOffset + ($x * 4)
        if ($bytes[$idx] -gt 30 -or $bytes[$idx+1] -gt 30 -or $bytes[$idx+2] -gt 30) { $rowHas = $true; break }
    }
    if ($rowHas -and -not $inBand) { $inBand = $true; $start = $y }
    if ((-not $rowHas) -and $inBand) {
        $inBand = $false
        $prevY = $y - 1
        [void]$bands.Add(@($start, $prevY))
    }
}
if ($inBand) {
    $lastY = $h - 1
    [void]$bands.Add(@($start, $lastY))
}

Write-Output "Content bands:"
foreach ($band in $bands) { Write-Output ("  {0} - {1}" -f $band[0], $band[1]) }

if ($bands.Count -eq 0) {
    Write-Error "No content found in $srcPath"
    exit 1
}

$markBand = $bands[0]
$topY = $markBand[0]
$botY = $markBand[1]

$minX = $w
$maxX = 0
for ($y = $topY; $y -le $botY; $y++) {
    $rowOffset = $y * $stride
    for ($x = 0; $x -lt $w; $x++) {
        $idx = $rowOffset + ($x * 4)
        if ($bytes[$idx] -gt 30 -or $bytes[$idx+1] -gt 30 -or $bytes[$idx+2] -gt 30) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
        }
    }
}
Write-Output ("Mark bbox: x=[{0},{1}] y=[{2},{3}]" -f $minX, $maxX, $topY, $botY)

$nextBandTop = $h
if ($bands.Count -gt 1) { $nextBandTop = $bands[1][0] }

$contentW = $maxX - $minX
$contentH = $botY - $topY
$cx = ($minX + $maxX) / 2.0
$cy = ($topY + $botY) / 2.0

$maxDim = $contentW
if ($contentH -gt $maxDim) { $maxDim = $contentH }
$desiredPadded = [int]($maxDim * 1.28)

$marginBeforeNextBand = 20 * ($w / 1024.0)
$limit = $nextBandTop - $marginBeforeNextBand
$padded = $desiredPadded
if ($padded -gt $limit) { $padded = [int]$limit }

$cropTop = [int]($cy - $padded / 2.0)
if ($cropTop -lt 0) { $cropTop = 0 }
$cropBottom = $cropTop + $padded
if ($cropBottom -gt $limit) { $cropBottom = [int]$limit; $cropTop = $cropBottom - $padded }

$cropLeft = [int]($cx - $padded / 2.0)
$cropRight = $cropLeft + $padded
if ($cropLeft -lt 0) { $cropLeft = 0; $cropRight = $padded }
if ($cropRight -gt $w) { $cropRight = $w; $cropLeft = $w - $padded }

Write-Output ("Crop box: x=[{0},{1}] y=[{2},{3}] size={4}" -f $cropLeft, $cropRight, $cropTop, $cropBottom, $padded)

$cropped = New-Object System.Drawing.Bitmap $padded, $padded, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($cropped)
$g.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $padded, $padded), (New-Object System.Drawing.Rectangle $cropLeft, $cropTop, $padded, $padded), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$bmp.Dispose()
$src.Dispose()

$small = Resize-HighQuality -Source $cropped -TargetSize 128
$cropped.Dispose()
$small.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose()
Write-Output "Wrote $outPath"

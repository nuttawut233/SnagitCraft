# Sets the app logo from a picture (PNG/JPG), e.g. an illustration made in an image generator.
#   powershell -STA -File installer\set-logo.ps1 -Source C:\path\to\griffin.png -Background "#2F6BE0"
# Produces Assets\logo.png (512 px, rounded corners like the other Craft icons), Assets\logo-256.png
# (installer wizard) and Assets\app.ico (16–256 px: exe, tray, window icons). Rebuild the app afterwards.
param(
    [Parameter(Mandatory)][string]$Source,
    [double]$CornerRadius = 0.22,   # fraction of the size; the Craft icons use ~22 %
    [switch]$NoRoundCorners,         # keep the source corners (if the image already has them)
    [string]$Background,             # e.g. "#2F6BE0": put a transparent picture on a solid panel like the Craft icons
    [double]$Fill = 0.9              # with -Background: how much of the panel the picture covers
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$src = New-Object Windows.Media.Imaging.BitmapImage
$src.BeginInit(); $src.CacheOption = 'OnLoad'; $src.UriSource = New-Object Uri((Resolve-Path $Source).Path); $src.EndInit()

# Renders the logo at $size px as straight (non-premultiplied) BGRA.
function Render([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, 'HighQuality')
    $dc = $visual.RenderOpen()
    $rect = New-Object Windows.Rect 0, 0, $size, $size
    if (-not $NoRoundCorners) {
        $r = $size * $CornerRadius
        $dc.PushClip((New-Object Windows.Media.RectangleGeometry $rect, $r, $r))
    }
    # centre-crop to a square
    $w = $src.PixelWidth; $h = $src.PixelHeight; $side = [Math]::Min($w, $h)
    $crop = New-Object Windows.Media.Imaging.CroppedBitmap $src, (New-Object Windows.Int32Rect ([int](($w - $side) / 2)), ([int](($h - $side) / 2)), $side, $side)
    if ($Background) {
        $dc.DrawRectangle((New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($Background))), $null, $rect)
        $inset = $size * (1 - $Fill) / 2
        $dc.DrawImage($crop, (New-Object Windows.Rect $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)))
    } else {
        $dc.DrawImage($crop, $rect)
    }
    if (-not $NoRoundCorners) { $dc.Pop() }
    $dc.Close()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    return New-Object Windows.Media.Imaging.FormatConvertedBitmap $rtb, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
}

function PngBytes($bmp) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream; $enc.Save($ms)
    return ,$ms.ToArray()
}

# Classic 32-bit DIB icon entry (header + bottom-up BGRA + empty AND mask). Sizes under 256 must use this:
# System.Drawing.Icon (tray icon) and older readers only understand PNG for the 256 px entry.
function DibBytes($bmp) {
    $s = $bmp.PixelWidth; $stride = $s * 4
    $px = New-Object byte[] ($stride * $s); $bmp.CopyPixels($px, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($px.Length + $maskStride * $s)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px, $y * $stride, $stride) }
    $w.Write((New-Object byte[] ($maskStride * $s)))
    $w.Flush()
    return ,$ms.ToArray()
}

[IO.File]::WriteAllBytes((Join-Path $root 'Assets\logo.png'), (PngBytes (Render 512)))
[IO.File]::WriteAllBytes((Join-Path $root 'Assets\logo-256.png'), (PngBytes (Render 256)))

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) { $bmp = Render $s; $images[$s] = $(if ($s -ge 256) { PngBytes $bmp } else { DibBytes $bmp }) }
$ico = Join-Path $root 'Assets\app.ico'
$fs = [IO.File]::Create($ico); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $b = [byte]($(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write($b); $bw.Write($b); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$s].Length); $bw.Write([uint32]$offset); $offset += $images[$s].Length
}
foreach ($s in $sizes) { $bw.Write($images[$s]) }
$bw.Close()
"logo set from $Source -> Assets\logo.png, Assets\logo-256.png, Assets\app.ico"

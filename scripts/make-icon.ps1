# Generates src\Arr411.Desktop\Assets\app.ico (16 to 256 px) from the Arr411 logo.
# Frames below 256 px are DIB/BMP, the 256 px frame is PNG: System.Drawing.Icon (Form.Icon, NotifyIcon) cannot read smaller PNG frames.
# Usage: powershell -File scripts\make-icon.ps1 [-Source <png>] [-Output <ico>]
param(
  [string]$Source = (Join-Path (Split-Path $PSScriptRoot) '..\arr411-webapp\public\icons\icon-512.png'),
  [string]$Output = (Join-Path (Split-Path $PSScriptRoot) 'src\Arr411.Desktop\Assets\app.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "Logo not found: $Source (clone arr411-webapp next to this repository, or pass -Source)" }
$Source = (Resolve-Path $Source).Path

function Resize-Image([System.Drawing.Image]$img, [int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $attr = New-Object System.Drawing.Imaging.ImageAttributes
  $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
  $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
  $g.Dispose(); $attr.Dispose()
  return $bmp
}

function Get-Png([System.Drawing.Image]$img) {
  $ms = New-Object System.IO.MemoryStream
  $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  return , $ms.ToArray()
}

# 32-bit DIB icon image: BITMAPINFOHEADER (height doubled), bottom-up BGRA pixels, then the 1-bit AND mask (1 = transparent).
function Get-Dib([System.Drawing.Bitmap]$bmp) {
  $w = $bmp.Width; $h = $bmp.Height
  $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $data.Stride
  $pix = New-Object byte[] ($stride * $h)
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pix, 0, $pix.Length)
  $bmp.UnlockBits($data)
  $maskRow = [int]([math]::Floor(($w + 31) / 32) * 4)
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter $ms
  $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32](2 * $h)); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]0); $bw.Write([uint32]($w * $h * 4 + $maskRow * $h))
  $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
  for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pix, $y * $stride, $w * 4) }
  for ($y = $h - 1; $y -ge 0; $y--) {
    $row = New-Object byte[] $maskRow
    for ($x = 0; $x -lt $w; $x++) {
      if ($pix[$y * $stride + $x * 4 + 3] -eq 0) { $row[$x -shr 3] = [byte]($row[$x -shr 3] -bor (0x80 -shr ($x -band 7))) }
    }
    $bw.Write($row)
  }
  $bw.Flush()
  return , $ms.ToArray()
}

# Halve step by step (512, 256, 128, 64, 32, 16) so small sizes are filtered instead of aliased.
$src = [System.Drawing.Image]::FromFile($Source)
$steps = @{ 512 = $src }
$prev = $src
foreach ($s in 256, 128, 64, 32, 16) { $prev = Resize-Image $prev $s; $steps[$s] = $prev }

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
  $from = [int]($steps.Keys | Where-Object { $_ -ge $s } | Measure-Object -Minimum).Minimum
  $img = if ($from -eq $s) { $steps[$s] } else { Resize-Image $steps[$from] $s }
  $data = if ($s -ge 256) { Get-Png $img } else { Get-Dib $img }
  $frames += [pscustomobject]@{ Size = $s; Data = $data }
  if ($from -ne $s) { $img.Dispose() }
}

$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
  $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f.Data) }
$w.Flush()

New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[System.IO.File]::WriteAllBytes($Output, $ms.ToArray())
foreach ($i in $steps.Values) { $i.Dispose() }
"Icon: $Output ($($ms.Length) bytes, $($frames.Count) frames: $($sizes -join ', '))"

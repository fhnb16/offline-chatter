# Рисует иконку приложения (src/OfflineChatter/Assets/app.ico): скруглённый квадрат с градиентом и облачком чата.
# ICO с PNG-кадрами поддерживается начиная с Vista, т.е. на всех целевых ОС.
# Запуск: pwsh tools/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\OfflineChatter\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function New-Frame([int]$s) {
    $bmp = [Drawing.Bitmap]::new($s, $s, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $m = [single]([Math]::Max(1, $s * 0.04))
    $bg = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ([single]($s * 0.22))
    $brush = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.PointF]::new(0, 0), [Drawing.PointF]::new($s, $s),
        [Drawing.Color]::FromArgb(255, 0x1E, 0x90, 0xFF), [Drawing.Color]::FromArgb(255, 0x6A, 0x3D, 0xE8))
    $g.FillPath($brush, $bg)
    # облачко чата
    $bx = [single]($s * 0.22); $by = [single]($s * 0.24); $bw = [single]($s * 0.56); $bh = [single]($s * 0.40)
    $bubble = New-RoundRect $bx $by $bw $bh ([single]($s * 0.12))
    $white = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    $g.FillPath($white, $bubble)
    $tail = [Drawing.PointF[]]@(
        [Drawing.PointF]::new($bx + $bw * 0.22, $by + $bh - 1),
        [Drawing.PointF]::new($bx + $bw * 0.20, $by + $bh + $s * 0.14),
        [Drawing.PointF]::new($bx + $bw * 0.45, $by + $bh - 1))
    $g.FillPolygon($white, $tail)
    # три точки «печатает»
    if ($s -ge 24) {
        $dot = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 0x3F, 0x6C, 0xF0))
        $r = [single]($s * 0.045)
        foreach ($i in 0..2) {
            $cx = $bx + $bw * (0.28 + 0.22 * $i); $cy = $by + $bh / 2
            $g.FillEllipse($dot, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
        }
    }
    $g.Dispose(); return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $ms = [IO.MemoryStream]::new(); (New-Frame $s).Save($ms, [Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray()
}
$fs = [IO.File]::Create($out); $w = [IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Dispose()
Write-Host "Icon written: $out"

# Рисует assets\BatteryBar.ico: синяя плитка в стиле Windows 11 с белой батарейкой и зелёным зарядом.
# Запуск: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets\BatteryBar.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Плитка с градиентом
    $m = [Math]::Max(0.5, $s * 0.04)
    $tile = New-RoundedPath $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 76, 160, 255)), ([System.Drawing.Color]::FromArgb(255, 0, 90, 200))
    $g.FillPath($grad, $tile)

    # Корпус батарейки
    $stroke = [Math]::Max(1.25, $s * 0.065)
    $bx = $s * 0.17; $by = $s * 0.32; $bw = $s * 0.58; $bh = $s * 0.36
    $body = New-RoundedPath $bx $by $bw $bh ([Math]::Max(1, $s * 0.07))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $g.DrawPath($pen, $body)

    # Клемма
    $tw = [Math]::Max(1.5, $s * 0.07)
    $g.FillRectangle([System.Drawing.Brushes]::White, [single]($bx + $bw + $stroke * 0.6), [single]($s * 0.43), [single]$tw, [single]($s * 0.14))

    # Заряд (~70%)
    $pad = $stroke * 1.3
    $fw = ($bw - 2 * $pad) * 0.7
    $fill = New-RoundedPath ($bx + $pad) ($by + $pad) $fw ($bh - 2 * $pad) ([Math]::Max(0.5, $s * 0.03))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 108, 230, 110))
    $g.FillPath($green, $fill)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

# ICO: заголовок, каталог, затем PNG-картинки подряд
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()

# Превью для проверки
$preview = Join-Path $root 'assets\icon-preview.png'
[System.IO.File]::WriteAllBytes($preview, $images[-1])
Write-Output "written $out"

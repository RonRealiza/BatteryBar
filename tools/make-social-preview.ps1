# Рисует docs/social-preview.png (1280×640) — картинку-превью для ссылок на репозиторий.
# Загружается вручную: GitHub → Settings → General → Social preview.
# Запуск: powershell -ExecutionPolicy Bypass -File tools\make-social-preview.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$shots = Join-Path $root 'docs\screenshots'
$out = Join-Path $root 'docs\social-preview.png'
$W = 1280; $H = 640

$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'

# Фон: тёмный градиент как у панели задач Windows 11 в тёмной теме.
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $W, $H), ([System.Drawing.Color]::FromArgb(255, 18, 24, 34)), ([System.Drawing.Color]::FromArgb(255, 30, 44, 64))
$g.FillRectangle($bg, 0, 0, $W, $H)

function RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

# Иконка и название
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\icon-preview.png'))
$g.DrawImage($icon, 96, 92, 168, 168)
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 190, 200, 214))
$g.DrawString('BatteryBar', (New-Object System.Drawing.Font('Segoe UI Semibold', 72, [System.Drawing.GraphicsUnit]::Pixel)), $white, 296, 98)
$g.DrawString('Заряд беспроводных устройств', (New-Object System.Drawing.Font('Segoe UI', 34, [System.Drawing.GraphicsUnit]::Pixel)), $soft, 300, 190)
$g.DrawString('на панели задач Windows 11', (New-Object System.Drawing.Font('Segoe UI', 34, [System.Drawing.GraphicsUnit]::Pixel)), $soft, 300, 232)

# Ряд из двух «карточек»: блок на панели задач (погода + BatteryBar) и иконки в трее.
# Скриншоты кладём на подложку цвета панели и обрезаем, чтобы не было видно обоев и рамок.
$taskbar = [System.Drawing.Image]::FromFile((Join-Path $shots 'taskbar.png'))
$tray = [System.Drawing.Image]::FromFile((Join-Path $shots 'tray.png'))
$taskSrc = New-Object System.Drawing.Rectangle 0, 3, 480, 44      # погода и блок, без обоев справа
$traySrc = New-Object System.Drawing.Rectangle 0, 18, $tray.Width, ($tray.Height - 30)

$ts = 1.4; $rs = 0.62
$tw = [int]($taskSrc.Width * $ts); $th = [int]($taskSrc.Height * $ts)
$rw = [int]($traySrc.Width * $rs); $rh = [int]($traySrc.Height * $rs)
$gap = 36; $pad = 16
$rowW = $tw + $rw + 4 * $pad + $gap
$x0 = [int](($W - $rowW) / 2); $y0 = 352
$cardH = [Math]::Max($th, $rh) + 2 * $pad
$cardColor = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 42, 56))
$cardPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(55, 255, 255, 255)), 1

$card1 = RoundedRect $x0 $y0 ($tw + 2 * $pad) $cardH 18
$g.FillPath($cardColor, $card1); $g.DrawPath($cardPen, $card1)
$g.DrawImage($taskbar, (New-Object System.Drawing.Rectangle ($x0 + $pad), ([int]($y0 + ($cardH - $th) / 2)), $tw, $th), $taskSrc, [System.Drawing.GraphicsUnit]::Pixel)

$x1 = $x0 + $tw + 2 * $pad + $gap
$card2 = RoundedRect $x1 $y0 ($rw + 2 * $pad) $cardH 18
$g.FillPath($cardColor, $card2); $g.DrawPath($cardPen, $card2)
$g.DrawImage($tray, (New-Object System.Drawing.Rectangle ($x1 + $pad), ([int]($y0 + ($cardH - $rh) / 2)), $rw, $rh), $traySrc, [System.Drawing.GraphicsUnit]::Pixel)

$cap = New-Object System.Drawing.Font('Segoe UI', 20, [System.Drawing.GraphicsUnit]::Pixel)
$capBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 150, 162, 180))
$c1 = 'на панели задач'; $c2 = 'или в трее'
$g.DrawString($c1, $cap, $capBrush, [single]($x0 + ($tw + 2 * $pad - $g.MeasureString($c1, $cap).Width) / 2), [single]($y0 + $cardH + 10))
$g.DrawString($c2, $cap, $capBrush, [single]($x1 + ($rw + 2 * $pad - $g.MeasureString($c2, $cap).Width) / 2), [single]($y0 + $cardH + 10))

# Марки внизу
$brands = 'Logitech · Razer · SteelSeries · HyperX · Corsair · DualSense · Xbox · AirPods · Bluetooth'
$bf = New-Object System.Drawing.Font('Segoe UI', 24, [System.Drawing.GraphicsUnit]::Pixel)
$bs = $g.MeasureString($brands, $bf)
$g.DrawString($brands, $bf, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 140, 152, 170))), [single](($W - $bs.Width) / 2), 578)

$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$icon.Dispose(); $taskbar.Dispose(); $tray.Dispose()
"saved $out"

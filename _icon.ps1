# Generates app.ico (multi-size, 16..256) and preview PNGs.
# Design: dark rounded tile, 4+4 keys, one lit amber bar, a thin light rim.
# The rim keeps the silhouette readable on the dark Windows 11 taskbar (#202020).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function RRect([single]$x,[single]$y,[single]$w,[single]$h,[single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -lt 0.5) { $p.AddRectangle((New-Object System.Drawing.RectangleF($x,$y,$w,$h))); return $p }
    $d = [single]($r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function C([int]$r,[int]$g,[int]$b,[int]$a = 255) {
    return [System.Drawing.Color]::FromArgb($a,$r,$g,$b)
}

# full design, drawn in a 256x256 coordinate space
function Draw-Full([System.Drawing.Graphics]$g, [bool]$glow) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.InterpolationMode = 'HighQualityBicubic'

    # --- background tile: one flat colour, exactly as on the small sizes,
    #     so the window, the tray, the taskbar and Explorer show the same icon
    $tile = RRect 8 8 240 240 52
    $g.FillPath((New-Object System.Drawing.SolidBrush((C 71 82 214))), $tile)

    # --- rim: the only thing that works on a dark taskbar ---------------
    $rimPen = New-Object System.Drawing.Pen((C 165 173 255 120), 2.0)
    $g.DrawPath($rimPen, $tile)
    $rimPen.Dispose()

    $g.SetClip($tile)
    $tile.Dispose()

    # no glow and no gradient on purpose: any extra light at the big sizes makes
    # the taskbar icon look different from the small one in the title bar

    # --- keyboard: two rows of four keys --------------------------------
    $keyFill = C 124 134 236
    $keyEdge = C 155 164 245

    for ($row = 0; $row -lt 2; $row++) {
        $y = 64 + $row * 44
        for ($i = 0; $i -lt 4; $i++) {
            $x = 33 + $i * 50
            $p = RRect $x $y 40 34 8
            $g.FillPath((New-Object System.Drawing.SolidBrush($keyFill)), $p)
            $pen = New-Object System.Drawing.Pen($keyEdge, 1.0)
            $g.DrawPath($pen, $p)
            $pen.Dispose(); $p.Dispose()
        }
    }

    # --- the lit bar ----------------------------------------------------
    $sp = RRect 58 154 140 40 10
    $sbr = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF(58,154,140,40)), (C 255 255 255), (C 214 220 255), 90.0)
    $g.FillPath($sbr, $sp); $sbr.Dispose()
    $spPen = New-Object System.Drawing.Pen((C 255 255 255 235), 2.0)
    $g.DrawPath($spPen, $sp)
    $spPen.Dispose(); $sp.Dispose()

    # a soft light line on top of the bar: reads as "this one is lit"
    $top = RRect 60 156 136 12 8
    $tbr = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF(60,156,136,12)), (C 255 255 255 150), (C 255 255 255 0), 90.0)
    $g.FillPath($tbr, $top)
    $tbr.Dispose(); $top.Dispose()

    $g.ResetClip()
}

# small sizes: drawn pixel by pixel, no gradient, no glow, flat colours
function Draw-Simple([System.Drawing.Graphics]$g, [int]$S) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    $tileFill = C 71 82 214
    $rim = C 160 168 255
    $keyFill = C 124 134 236
    $accent = C 255 255 255

    if ($S -le 18) {
        # 16 px
        $tile = RRect 0 0 16 16 4
        $g.FillPath((New-Object System.Drawing.SolidBrush($tileFill)), $tile)
        $g.DrawPath((New-Object System.Drawing.Pen($rim, 1.0)), $tile)
        $tile.Dispose()
        $kb = New-Object System.Drawing.SolidBrush($keyFill)
        foreach ($x in @(2, 6, 11)) { $g.FillRectangle($kb, $x, 4, 3, 3) }
        $kb.Dispose()
        $ab = New-Object System.Drawing.SolidBrush($accent)
        $bar = RRect 3 10 10 4 1
        $g.FillPath($ab, $bar); $bar.Dispose(); $ab.Dispose()
    }
    elseif ($S -le 22) {
        # 20 px
        $tile = RRect 0 0 20 20 5
        $g.FillPath((New-Object System.Drawing.SolidBrush($tileFill)), $tile)
        $g.DrawPath((New-Object System.Drawing.Pen($rim, 1.0)), $tile)
        $tile.Dispose()
        $kb = New-Object System.Drawing.SolidBrush($keyFill)
        foreach ($x in @(2, 7, 13)) { $g.FillRectangle($kb, $x, 5, 4, 4) }
        $kb.Dispose()
        $ab = New-Object System.Drawing.SolidBrush($accent)
        $bar = RRect 3 12 14 5 2
        $g.FillPath($ab, $bar); $bar.Dispose(); $ab.Dispose()
    }
    elseif ($S -le 28) {
        # 24 px
        $tile = RRect 0 0 24 24 6
        $g.FillPath((New-Object System.Drawing.SolidBrush($tileFill)), $tile)
        $g.DrawPath((New-Object System.Drawing.Pen($rim, 1.0)), $tile)
        $tile.Dispose()
        $kb = New-Object System.Drawing.SolidBrush($keyFill)
        foreach ($x in @(3, 9, 16)) { $g.FillRectangle($kb, $x, 6, 5, 5) }
        $kb.Dispose()
        $ab = New-Object System.Drawing.SolidBrush($accent)
        $bar = RRect 4 14 16 6 2
        $g.FillPath($ab, $bar); $bar.Dispose(); $ab.Dispose()
    }
    else {
        # 32 px and the 40 px frame
        $k = $S / 32.0
        $tile = RRect (1 * $k) (1 * $k) (30 * $k) (30 * $k) (8 * $k)
        $g.FillPath((New-Object System.Drawing.SolidBrush($tileFill)), $tile)
        $g.DrawPath((New-Object System.Drawing.Pen($rim, [single](1.0 * $k))), $tile)
        $tile.Dispose()
        $kb = New-Object System.Drawing.SolidBrush($keyFill)
        foreach ($x in @(4, 13, 22)) {
            $g.FillRectangle($kb, [single]($x * $k), [single](8 * $k), [single](6 * $k), [single](6 * $k))
        }
        $kb.Dispose()
        $ab = New-Object System.Drawing.SolidBrush($accent)
        $bar = RRect (5 * $k) (19 * $k) (22 * $k) (8 * $k) (3 * $k)
        $g.FillPath($ab, $bar); $bar.Dispose(); $ab.Dispose()
    }
}

function Render([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    # small sizes are drawn pixel by pixel (a scaled picture turns to mush below
    # 32 px); from 48 px the same design is drawn at full detail. Both use the
    # same flat colours, so the window, tray, taskbar and Explorer show one icon
    if ($S -le 40) {
        Draw-Simple $g $S
    } else {
        $k = $S / 256.0
        $g.ScaleTransform($k, $k)
        Draw-Full $g $false
    }
    $g.Dispose()
    return $bmp
}

# 32bpp DIB (BGRA rows bottom-up + empty AND mask) - the most compatible frame format
function To-Dib([System.Drawing.Bitmap]$bmp) {
    $S = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)          # biSize
    $bw.Write([int32]$S)           # biWidth
    $bw.Write([int32]($S * 2))     # biHeight (XOR + AND)
    $bw.Write([uint16]1)           # biPlanes
    $bw.Write([uint16]32)          # biBitCount
    $bw.Write([uint32]0)           # biCompression BI_RGB
    $bw.Write([uint32]($S * $S * 4))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $S, $S)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = $S * 4
    $all = New-Object byte[] ($row * $S)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $all, 0, $all.Length)
    $bmp.UnlockBits($data)

    for ($y = $S - 1; $y -ge 0; $y--) { $bw.Write($all, $y * $row, $row) }

    $maskRow = [int]([math]::Floor(($S + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskRow * $S)
    $bw.Write($mask, 0, $mask.Length)
    $bw.Flush()
    return $ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = Render $s
    # every frame must be an uncompressed DIB: csc.exe and the .NET Icon class
    # do not accept PNG-compressed frames inside an .ico
    $bytes = To-Dib $bmp

    $frames += , @{ Size = $s; Data = $bytes }
    if ($s -eq 256 -or $s -eq 32 -or $s -eq 16) {
        $out = Join-Path $env:TEMP 'lightfixer-icons'
        if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }
        $bmp.Save((Join-Path $out "icon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
}

# --- write the ICO container --------------------------------------------
function Write-Ico($list, [string]$path) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$list.Count)
    $offset = 6 + 16 * $list.Count
    for ($i = 0; $i -lt $list.Count; $i++) {
        $s = [int]$list[$i].Size
        $data = [byte[]]$list[$i].Data
        $blen = [uint32]$data.Length
        $boff = [uint32]$offset
        $dim = [byte]0
        if ($s -lt 256) { $dim = [byte]$s }
        $bw.Write([byte]$dim)
        $bw.Write([byte]$dim)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write($blen)
        $bw.Write($boff)
        $offset += $data.Length
    }
    for ($i = 0; $i -lt $list.Count; $i++) {
        $d = [byte[]]$list[$i].Data
        $bw.Write($d, 0, $d.Length)
    }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $n = (Get-Item $path).Length
    Write-Host ("OK: {0}  {1:N0} bytes, frames: {2}" -f $path, $n, $list.Count)
}

# full set - for the exe itself (Explorer, Start menu, taskbar, large icons)
Write-Ico $frames (Join-Path $PSScriptRoot 'app.ico')

# compact set (16..64) - embedded as a managed resource and used at runtime
# for the tray, the window and Alt+Tab; keeps the exe small
$small = @()
for ($i = 0; $i -lt $frames.Count; $i++) {
    if ([int]$frames[$i].Size -le 64) { $small += , $frames[$i] }
}
Write-Ico $small (Join-Path $PSScriptRoot 'app-small.ico')

# Generates app.ico (16-256 px, PNG-compressed entries): a pair of glasses over a floating screen.
param([string]$Out = (Join-Path $PSScriptRoot "app.ico"))
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $k = $s / 256.0

    # rounded background
    $r = 56 * $k; $rect = New-Object System.Drawing.RectangleF (8*$k), (8*$k), (240*$k), (240*$k)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90); $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
    $path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90); $path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 28, 34, 52)), ([System.Drawing.Color]::FromArgb(255, 12, 14, 22)), 90
    $g.FillPath($bg, $path)

    # floating screen
    $scr = New-Object System.Drawing.RectangleF (52*$k), (40*$k), (152*$k), (88*$k)
    $sb = New-Object System.Drawing.Drawing2D.LinearGradientBrush $scr, ([System.Drawing.Color]::FromArgb(255, 90, 170, 255)), ([System.Drawing.Color]::FromArgb(255, 40, 110, 230)), 45
    $g.FillRectangle($sb, $scr)

    # glasses
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 235, 240, 250)), ([Math]::Max(1.5, 14*$k))
    $pen.LineJoin = 'Round'
    $lens = [System.Drawing.Color]::FromArgb(200, 20, 24, 36)
    $lb = New-Object System.Drawing.SolidBrush $lens
    $g.FillEllipse($lb, 36*$k, 140*$k, 82*$k, 64*$k); $g.FillEllipse($lb, 138*$k, 140*$k, 82*$k, 64*$k)
    $g.DrawEllipse($pen, 36*$k, 140*$k, 82*$k, 64*$k); $g.DrawEllipse($pen, 138*$k, 140*$k, 82*$k, 64*$k)
    $g.DrawArc($pen, 112*$k, 150*$k, 32*$k, 24*$k, 200, 140)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) { $b = Draw-Icon $s; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); ,$ms.ToArray() }

$icoStream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $icoStream
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[System.IO.File]::WriteAllBytes($Out, $icoStream.ToArray())
"Wrote $Out"

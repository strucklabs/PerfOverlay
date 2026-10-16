# Erzeugt Assets\PerfOverlay.ico (16–256 px, PNG-komprimiert) – einmalig bzw. nach Designänderungen ausführen:
#   powershell -ExecutionPolicy Bypass -File Assets\make-icon.ps1
# Motiv: dunkle Kachel mit Frametime-Kurve (grün) und einem Ausschlag (gelb) – passend zum Overlay.
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # abgerundete Kachel
    $r = [Math]::Max(2, $s * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 16, 20, 24))), $path)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 55, 65, 81)), ([Math]::Max(1, $s / 32))), $path)

    # Frametime-Kurve: flach, ein Ausschlag, wieder flach
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 74, 222, 128)), ([Math]::Max(1.5, $s / 10))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $p = { param($x, $y) New-Object System.Drawing.PointF ($x * $s), ($y * $s) }
    $pts = @((& $p 0.16 0.66), (& $p 0.40 0.66), (& $p 0.50 0.24), (& $p 0.60 0.66), (& $p 0.84 0.66))
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    # Spitze gelb markieren
    $d = [Math]::Max(2, $s * 0.16)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 251, 191, 36))), 0.50 * $s - $d / 2, 0.24 * $s - $d / 2, $d, $d)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # 256 px als PNG (Standard seit Windows Vista)
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # kleinere Größen als klassisches 32-Bit-DIB: BITMAPINFOHEADER (Höhe doppelt) + BGRA von unten + AND-Maske
        $w2 = New-Object System.IO.BinaryWriter $ms
        $w2.Write([UInt32]40); $w2.Write([Int32]$s); $w2.Write([Int32]($s * 2))
        $w2.Write([UInt16]1); $w2.Write([UInt16]32); $w2.Write([UInt32]0)
        $w2.Write([UInt32]($s * $s * 4)); $w2.Write([Int32]0); $w2.Write([Int32]0); $w2.Write([UInt32]0); $w2.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w2.Write([byte]$c.B); $w2.Write([byte]$c.G); $w2.Write([byte]$c.R); $w2.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $w2.Write((New-Object byte[] ($maskRow * $s)))   # Transparenz kommt aus dem Alphakanal
        $w2.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO-Container: Header, Verzeichnis, dann die PNG-Daten
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'PerfOverlay.ico'), $out.ToArray())
"PerfOverlay.ico geschrieben ($($out.Length) Bytes)"

# Draws BuilderEngine.ico (a faded-orange hammer on the black app background, in the style of EasyEngine's icon) at 16-256 px,
# plus BuilderEngine.png (256 px) for the windows. Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\Make-Icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src\BuilderEngine\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir 'BuilderEngine.ico'
$sizes = 256, 64, 48, 32, 24, 16

function Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.5, 2 * $r)
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure()
    return $p
}

function Draw-Bitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    # rounded black tile (BuilderEngine's background)
    $m = [Math]::Max(0, $s * 0.02); $w = $s - 2 * $m
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 17, 17, 17))), (Rounded $m $m $w $w ([Math]::Max(2, $s * 0.22))))

    # the hammer, drawn upright then turned 45 degrees so the head points to the top right
    $orange = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 217, 119, 87))
    $g.TranslateTransform($s / 2, $s / 2); $g.RotateTransform(45); $g.TranslateTransform(-$s / 2, -$s / 2)
    $hw = $s * 0.13                                   # handle width
    $g.FillPath($orange, (Rounded ($s / 2 - $hw / 2) ($s * 0.34) $hw ($s * 0.49) ($hw * 0.45)))
    $headW = $s * 0.50; $headH = $s * 0.20            # head
    $g.FillPath($orange, (Rounded ($s / 2 - $headW * 0.42) ($s * 0.21) $headW $headH ($headH * 0.22)))
    if ($s -ge 32) {
        # a dark line where the head meets the handle, so it reads as two parts
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 17, 17, 17))
        $g.FillRectangle($dark, [single]($s / 2 - $hw / 2), [single]($s * 0.21 + $headH), [single]$hw, [single]([Math]::Max(1, $s * 0.025)))
    }
    $g.Dispose()
    return $bmp
}

function Png-Bytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# ICO = header + one directory entry per size + the PNGs
$pngs = $sizes | ForEach-Object { $b = Draw-Bitmap $_; $bytes = Png-Bytes $b; $b.Dispose(); ,$bytes }
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Close()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'BuilderEngine.png'), $pngs[0])
"wrote $out"

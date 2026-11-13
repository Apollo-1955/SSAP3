#Requires -Version 5.1
[CmdletBinding()]
param()

# Regenerates the SSAP3 brand mark (assets/ssap3-mark.png, 512 px) and the
# multi-resolution application icon (assets/SSAP3.ico). The editable vector
# source is assets/SSAP3.svg; keep the two in sync.
#
# Motif: a listener (centre dot) with a pair of generic earbuds, and two
# arcs for the virtual soundstage anchored in front of the listener. The
# artwork is original and does not reproduce any Apple asset or trademark.

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Ssap3Bitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $s = $Size / 256.0

    # Background: deep indigo rounded square with a gentle diagonal gradient.
    $background = New-RoundedRectanglePath -X (8 * $s) -Y (8 * $s) -Width (240 * $s) -Height (240 * $s) -Radius (56 * $s)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF((8 * $s), (8 * $s))),
        (New-Object System.Drawing.PointF((248 * $s), (248 * $s))),
        [System.Drawing.Color]::FromArgb(255, 36, 46, 86),
        [System.Drawing.Color]::FromArgb(255, 12, 17, 34))
    $graphics.FillPath($gradient, $background)

    $accent = [System.Drawing.Color]::FromArgb(255, 92, 214, 246)
    $white = [System.Drawing.Color]::FromArgb(255, 246, 249, 255)

    # Soundstage arcs, centred on the listener at (128,150).
    $arcWidth = [Math]::Max(1.2, 11 * $s)
    $outer = New-Object System.Drawing.Pen $accent, $arcWidth
    $outer.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $outer.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawArc($outer, (128 - 92) * $s, (150 - 92) * $s, 184 * $s, 184 * $s, 222, 96)
    if ($Size -ge 24) {
        $innerColor = [System.Drawing.Color]::FromArgb(185, 92, 214, 246)
        $inner = New-Object System.Drawing.Pen $innerColor, $arcWidth
        $inner.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $inner.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawArc($inner, (128 - 60) * $s, (150 - 60) * $s, 120 * $s, 120 * $s, 228, 84)
        $inner.Dispose()
    }

    # Generic earbuds: rounded heads with short stems, either side of the listener.
    $budBrush = New-Object System.Drawing.SolidBrush $white
    $stem = New-Object System.Drawing.Pen $white, ([Math]::Max(1.6, 17 * $s))
    $stem.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stem.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($bud in @(@(70, 168, 78, 214), @(186, 168, 178, 214))) {
        $graphics.DrawLine($stem, $bud[0] * $s, $bud[1] * $s, $bud[2] * $s, $bud[3] * $s)
        $graphics.FillEllipse($budBrush, ($bud[0] - 23) * $s, ($bud[1] - 25) * $s, 46 * $s, 46 * $s)
    }

    # Listener / anchor point.
    $dotBrush = New-Object System.Drawing.SolidBrush $accent
    $dotRadius = [Math]::Max(1.4, 12 * $s)
    $graphics.FillEllipse($dotBrush, 128 * $s - $dotRadius, 150 * $s - $dotRadius, $dotRadius * 2, $dotRadius * 2)

    $dotBrush.Dispose()
    $stem.Dispose()
    $budBrush.Dispose()
    $outer.Dispose()
    $gradient.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return $bytes
}

$preview = New-Ssap3Bitmap -Size 512
$preview.Save((Join-Path $PSScriptRoot 'ssap3-mark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-Ssap3Bitmap -Size $size
    try {
        [pscustomobject]@{ Size = $size; Bytes = Convert-BitmapToPngBytes -Bitmap $bitmap }
    }
    finally {
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $PSScriptRoot 'SSAP3.ico'
$iconStream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
$writer = New-Object System.IO.BinaryWriter $iconStream
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

Write-Output "Generated: $iconPath"
Write-Output "Generated: $(Join-Path $PSScriptRoot 'ssap3-mark.png')"

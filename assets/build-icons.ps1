#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc(
        $X + $Width - $diameter,
        $Y,
        $diameter,
        $diameter,
        270,
        90)
    $path.AddArc(
        $X + $Width - $diameter,
        $Y + $Height - $diameter,
        $diameter,
        $diameter,
        0,
        90)
    $path.AddArc(
        $X,
        $Y + $Height - $diameter,
        $diameter,
        $diameter,
        90,
        90)
    $path.CloseFigure()
    return $path
}

function New-SoundStageBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode =
        [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode =
        [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 256.0
    $background = New-RoundedRectanglePath `
        -X (8 * $scale) `
        -Y (8 * $scale) `
        -Width (240 * $scale) `
        -Height (240 * $scale) `
        -Radius (54 * $scale)
    $backgroundBrush = New-Object System.Drawing.SolidBrush (
        [System.Drawing.Color]::FromArgb(255, 23, 27, 39))
    $graphics.FillPath($backgroundBrush, $background)

    $strokeWidth = [Math]::Max(1.15, 11 * $scale)
    $stroke = New-Object System.Drawing.Pen (
        [System.Drawing.Color]::FromArgb(255, 248, 250, 255)),
        $strokeWidth
    $stroke.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stroke.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stroke.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $left = New-Object System.Drawing.Drawing2D.GraphicsPath
    $left.StartFigure()
    $left.AddBezier(
        100 * $scale, 88 * $scale,
        97 * $scale, 100 * $scale,
        87 * $scale, 106 * $scale,
        72 * $scale, 104 * $scale)
    $left.AddBezier(
        72 * $scale, 104 * $scale,
        54 * $scale, 102 * $scale,
        43 * $scale, 90 * $scale,
        43 * $scale, 72 * $scale)
    $left.AddBezier(
        43 * $scale, 72 * $scale,
        43 * $scale, 50 * $scale,
        58 * $scale, 35 * $scale,
        79 * $scale, 35 * $scale)
    $left.AddBezier(
        79 * $scale, 35 * $scale,
        100 * $scale, 35 * $scale,
        115 * $scale, 49 * $scale,
        115 * $scale, 69 * $scale)
    $left.AddBezier(
        115 * $scale, 69 * $scale,
        115 * $scale, 78 * $scale,
        112 * $scale, 85 * $scale,
        106 * $scale, 91 * $scale)
    $left.AddLine(
        106 * $scale,
        91 * $scale,
        103 * $scale,
        126 * $scale)
    $graphics.DrawPath($stroke, $left)

    $right = New-Object System.Drawing.Drawing2D.GraphicsPath
    $right.StartFigure()
    $right.AddBezier(
        156 * $scale, 88 * $scale,
        159 * $scale, 100 * $scale,
        169 * $scale, 106 * $scale,
        184 * $scale, 104 * $scale)
    $right.AddBezier(
        184 * $scale, 104 * $scale,
        202 * $scale, 102 * $scale,
        213 * $scale, 90 * $scale,
        213 * $scale, 72 * $scale)
    $right.AddBezier(
        213 * $scale, 72 * $scale,
        213 * $scale, 50 * $scale,
        198 * $scale, 35 * $scale,
        177 * $scale, 35 * $scale)
    $right.AddBezier(
        177 * $scale, 35 * $scale,
        156 * $scale, 35 * $scale,
        141 * $scale, 49 * $scale,
        141 * $scale, 69 * $scale)
    $right.AddBezier(
        141 * $scale, 69 * $scale,
        141 * $scale, 78 * $scale,
        144 * $scale, 85 * $scale,
        150 * $scale, 91 * $scale)
    $right.AddLine(
        150 * $scale,
        91 * $scale,
        153 * $scale,
        126 * $scale)
    $graphics.DrawPath($stroke, $right)

    $case = New-Object System.Drawing.Drawing2D.GraphicsPath
    $case.StartFigure()
    $case.AddLine(42 * $scale, 126 * $scale, 214 * $scale, 126 * $scale)
    $graphics.DrawPath($stroke, $case)
    $caseBody = New-Object System.Drawing.Drawing2D.GraphicsPath
    $caseBody.StartFigure()
    $caseBody.AddLine(42 * $scale, 126 * $scale, 42 * $scale, 179 * $scale)
    $caseBody.AddBezier(
        42 * $scale, 179 * $scale,
        42 * $scale, 204 * $scale,
        62 * $scale, 224 * $scale,
        87 * $scale, 224 * $scale)
    $caseBody.AddLine(87 * $scale, 224 * $scale, 169 * $scale, 224 * $scale)
    $caseBody.AddBezier(
        169 * $scale, 224 * $scale,
        194 * $scale, 224 * $scale,
        214 * $scale, 204 * $scale,
        214 * $scale, 179 * $scale)
    $caseBody.AddLine(214 * $scale, 179 * $scale, 214 * $scale, 126 * $scale)
    $graphics.DrawPath($stroke, $caseBody)

    $dotBrush = New-Object System.Drawing.SolidBrush (
        [System.Drawing.Color]::FromArgb(255, 248, 250, 255))
    foreach ($dot in @(
        @(70, 70, 5),
        @(186, 70, 5),
        @(128, 151, 4))) {
        $radius = [Math]::Max(0.8, $dot[2] * $scale)
        $graphics.FillEllipse(
            $dotBrush,
            $dot[0] * $scale - $radius,
            $dot[1] * $scale - $radius,
            $radius * 2,
            $radius * 2)
    }

    $dotBrush.Dispose()
    $caseBody.Dispose()
    $case.Dispose()
    $right.Dispose()
    $left.Dispose()
    $stroke.Dispose()
    $backgroundBrush.Dispose()
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

$preview = New-SoundStageBitmap -Size 512
$preview.Save(
    (Join-Path $PSScriptRoot 'soundstage-mark.png'),
    [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-SoundStageBitmap -Size $size
    try {
        [pscustomobject]@{
            Size = $size
            Bytes = Convert-BitmapToPngBytes -Bitmap $bitmap
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $PSScriptRoot 'SoundStage.ico'
$iconStream = [System.IO.File]::Open(
    $iconPath,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
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
Write-Output "Generated: $(Join-Path $PSScriptRoot 'soundstage-mark.png')"

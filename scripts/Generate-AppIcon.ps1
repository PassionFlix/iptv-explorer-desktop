[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [single]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [Math]::Max(1.0, $Radius * 2.0)
    $arc = [System.Drawing.RectangleF]::new($Rectangle.X, $Rectangle.Y, $diameter, $diameter)

    $path.AddArc($arc, 180, 90)
    $arc.X = $Rectangle.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $Rectangle.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $Rectangle.Left
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPngBytes {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = [single]($Size / 256.0)
    $outer = [System.Drawing.RectangleF]::new(8 * $scale, 8 * $scale, 240 * $scale, 240 * $scale)
    $inner = [System.Drawing.RectangleF]::new(40 * $scale, 40 * $scale, 176 * $scale, 176 * $scale)

    $outerPath = New-RoundedRectanglePath -Rectangle $outer -Radius (58 * $scale)
    $innerPath = New-RoundedRectanglePath -Rectangle $inner -Radius (40 * $scale)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 8, 14, 25))
    $innerBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 10, 20, 36))
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $outer,
        [System.Drawing.Color]::FromArgb(255, 124, 92, 255),
        [System.Drawing.Color]::FromArgb(255, 48, 210, 195),
        45.0
    )
    $borderPen = [System.Drawing.Pen]::new($gradient, [single][Math]::Max(1.0, 20 * $scale))
    $borderPen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset

    $graphics.FillPath($background, $outerPath)
    $graphics.DrawPath($borderPen, $outerPath)
    $graphics.FillPath($innerBrush, $innerPath)

    $fontSize = [single][Math]::Max(5.0, 82 * $scale)
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 248, 250, 252))
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = [System.Drawing.RectangleF]::new(38 * $scale, 47 * $scale, 170 * $scale, 128 * $scale)
    $graphics.DrawString('IE', $font, $textBrush, $textRect, $format)

    $playBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 48, 210, 195))
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(177 * $scale, 167 * $scale),
        [System.Drawing.PointF]::new(177 * $scale, 204 * $scale),
        [System.Drawing.PointF]::new(211 * $scale, 185.5 * $scale)
    )
    $graphics.FillPolygon($playBrush, $points)

    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $playBrush.Dispose()
        $format.Dispose()
        $textBrush.Dispose()
        $font.Dispose()
        $borderPen.Dispose()
        $gradient.Dispose()
        $innerBrush.Dispose()
        $background.Dispose()
        $innerPath.Dispose()
        $outerPath.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$directory = [System.IO.Path]::GetDirectoryName($fullOutputPath)
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Bytes = New-IconPngBytes -Size $size
    }
}

$stream = [System.IO.File]::Create($fullOutputPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
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
    $stream.Dispose()
}

if (-not (Test-Path -LiteralPath $fullOutputPath) -or (Get-Item -LiteralPath $fullOutputPath).Length -lt 1024) {
    throw "La génération de l'icône Windows a échoué."
}

Write-Host "Icône Windows générée : $fullOutputPath"

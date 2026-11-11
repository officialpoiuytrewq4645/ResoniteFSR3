# Generates icon.png (256x256): a riff on Resonite's petal-burst mark with an "FSR 3.1" label.
param([string]$OutputPath = "$PSScriptRoot\..\icon.png", [int]$Size = 256)

Add-Type -AssemblyName System.Drawing

function New-PetalPath([float]$centerX, [float]$centerY, [float]$angleDegrees, [float]$innerDistance, [float]$length, [float]$radius) {
    # Teardrop pointing outward along +X in local space: thin tip near the burst centre, round end outward
    $tipX = $innerDistance
    $endCenterX = $innerDistance + $length - $radius
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBezier($tipX, 0, $tipX + ($endCenterX - $tipX) * 0.35, -$radius * 0.25, $endCenterX - $radius * 0.9, -$radius, $endCenterX, -$radius)
    $path.AddArc($endCenterX - $radius, -$radius, $radius * 2, $radius * 2, 270, 180)
    $path.AddBezier($endCenterX, $radius, $endCenterX - $radius * 0.9, $radius, $tipX + ($endCenterX - $tipX) * 0.35, $radius * 0.25, $tipX, 0)
    $path.CloseFigure()
    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate($centerX, $centerY)
    $matrix.Rotate($angleDegrees)
    $path.Transform($matrix)
    return $path
}

function Get-PetalEnd([float]$centerX, [float]$centerY, [float]$angleDegrees, [float]$distance) {
    $radians = $angleDegrees * [Math]::PI / 180
    return New-Object System.Drawing.PointF ($centerX + [Math]::Cos($radians) * $distance), ($centerY + [Math]::Sin($radians) * $distance)
}

$bitmap = New-Object System.Drawing.Bitmap $Size, $Size
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.TextRenderingHint = 'AntiAliasGridFit'
$graphics.InterpolationMode = 'HighQualityBicubic'

# Dark background with a soft violet glow behind the burst
$graphics.Clear([System.Drawing.Color]::FromArgb(255, 18, 16, 26))
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(-20, -40, $Size + 40, $Size + 20)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
$glow.CenterColor = [System.Drawing.Color]::FromArgb(255, 48, 34, 78)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 18, 16, 26))
$graphics.FillPath($glow, $glowPath)

# Petal burst: angle (deg, clockwise from +X), inner distance, length, end radius, inner colour, outer colour
$burstCenterX = $Size * 0.5
$burstCenterY = $Size * 0.40
$scale = $Size / 256.0
$petals = @(
    @{ Angle = -48;  Inner = 16; Length = 92; Radius = 30; From = '#3D7BFF'; To = '#A24BFF' },  # large, up-right: blue -> violet
    @{ Angle = -112; Inner = 16; Length = 58; Radius = 17; From = '#3FA9FF'; To = '#7FE3FF' },  # up: blue -> light cyan
    @{ Angle = -168; Inner = 16; Length = 52; Radius = 15; From = '#22D6C4'; To = '#4CFF9A' },  # left: teal -> green
    @{ Angle = 134;  Inner = 16; Length = 92; Radius = 30; From = '#7CFF4A'; To = '#FFE53B' },  # large, down-left: green -> yellow
    @{ Angle = 88;   Inner = 18; Length = 56; Radius = 16; From = '#FF3B3B'; To = '#FF9A3B' },  # down: red -> orange
    @{ Angle = 16;   Inner = 18; Length = 54; Radius = 15; From = '#FF3FA4'; To = '#C24BFF' }   # right: pink -> violet
)
foreach ($petal in $petals) {
    $path = New-PetalPath $burstCenterX $burstCenterY $petal.Angle ($petal.Inner * $scale) ($petal.Length * $scale) ($petal.Radius * $scale)
    $startPoint = Get-PetalEnd $burstCenterX $burstCenterY $petal.Angle ($petal.Inner * $scale)
    $endPoint = Get-PetalEnd $burstCenterX $burstCenterY $petal.Angle (($petal.Inner + $petal.Length) * $scale)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $startPoint, $endPoint, ([System.Drawing.ColorTranslator]::FromHtml($petal.From)), ([System.Drawing.ColorTranslator]::FromHtml($petal.To))
    $graphics.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()
}

# Label
$format = New-Object System.Drawing.StringFormat
$format.Alignment = 'Center'
$format.LineAlignment = 'Center'
$labelFont = New-Object System.Drawing.Font "Segoe UI", ([float](34 * $scale)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$graphics.DrawString("FSR 3.1", $labelFont, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($Size * 0.73), $Size, ($Size * 0.16)), $format)
$tagFont = New-Object System.Drawing.Font "Segoe UI", ([float](13 * $scale)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$tagBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 190, 170, 255))
$graphics.DrawString("ALPHA", $tagFont, $tagBrush, (New-Object System.Drawing.RectangleF 0, ($Size * 0.88), $Size, ($Size * 0.09)), $format)

$graphics.Dispose()
$bitmap.Save((Resolve-Path (Split-Path $OutputPath -Parent)).Path + "\" + (Split-Path $OutputPath -Leaf), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "Wrote $OutputPath"

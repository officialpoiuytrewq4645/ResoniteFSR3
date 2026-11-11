# Ring-style icon (the original FSR ring layout) recoloured with Resonite's petal palette.
param([string]$OutputPath = "$PSScriptRoot\..\icon-ring.png", [int]$Size = 256)

Add-Type -AssemblyName System.Drawing

# Resonite petal palette, in order around the ring (wraps back to the first colour)
$palette = @('#3D7BFF', '#A24BFF', '#FF3FA4', '#FF3B3B', '#FF9A3B', '#FFE53B', '#7CFF4A', '#22D6C4', '#3FA9FF') |
    ForEach-Object { [System.Drawing.ColorTranslator]::FromHtml($_) }

function Get-PaletteColor([double]$position) {
    # position 0..1 around the ring
    $scaled = ($position % 1.0) * $palette.Count
    $index = [Math]::Floor($scaled)
    $fraction = $scaled - $index
    $from = $palette[[int]$index % $palette.Count]
    $to = $palette[([int]$index + 1) % $palette.Count]
    return [System.Drawing.Color]::FromArgb(255,
        [int]($from.R + ($to.R - $from.R) * $fraction),
        [int]($from.G + ($to.G - $from.G) * $fraction),
        [int]($from.B + ($to.B - $from.B) * $fraction))
}

$scale = $Size / 256.0
$bitmap = New-Object System.Drawing.Bitmap $Size, $Size
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.TextRenderingHint = 'AntiAliasGridFit'

# Background: dark with a soft violet glow
$graphics.Clear([System.Drawing.Color]::FromArgb(255, 18, 16, 26))
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(-30 * $scale, -30 * $scale, $Size + 60 * $scale, $Size + 60 * $scale)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
$glow.CenterColor = [System.Drawing.Color]::FromArgb(255, 44, 30, 72)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 18, 16, 26))
$graphics.FillPath($glow, $glowPath)

# Rainbow ring: many thin arc segments approximating a conic gradient
$ringRect = New-Object System.Drawing.RectangleF (36 * $scale), (36 * $scale), (184 * $scale), (184 * $scale)
$ringWidth = 16 * $scale
$segments = 360
for ($segment = 0; $segment -lt $segments; $segment++) {
    $pen = New-Object System.Drawing.Pen (Get-PaletteColor ($segment / $segments)), $ringWidth
    $graphics.DrawArc($pen, $ringRect, [float](-90 + $segment * 360.0 / $segments), [float](360.0 / $segments + 0.6))
    $pen.Dispose()
}

# Labels
$format = New-Object System.Drawing.StringFormat
$format.Alignment = 'Center'
$format.LineAlignment = 'Center'
$fsrFont = New-Object System.Drawing.Font "Segoe UI", ([float](58 * $scale)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$graphics.DrawString("FSR", $fsrFont, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, (78 * $scale), $Size, (70 * $scale)), $format)

$versionRect = New-Object System.Drawing.RectangleF 0, (138 * $scale), $Size, (46 * $scale)
$versionBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF (98 * $scale), 0), (New-Object System.Drawing.PointF (158 * $scale), 0), ([System.Drawing.ColorTranslator]::FromHtml('#3FA9FF')), ([System.Drawing.ColorTranslator]::FromHtml('#FF3FA4'))
$versionFont = New-Object System.Drawing.Font "Segoe UI", ([float](34 * $scale)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$graphics.DrawString("3.1", $versionFont, $versionBrush, $versionRect, $format)

$tagFont = New-Object System.Drawing.Font "Segoe UI", ([float](13 * $scale)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$tagBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 205, 190, 255))
$graphics.DrawString("ALPHA", $tagFont, $tagBrush, (New-Object System.Drawing.RectangleF 0, (226 * $scale), $Size, (26 * $scale)), $format)

$graphics.Dispose()
$fullOutputPath = (Resolve-Path (Split-Path $OutputPath -Parent)).Path + "\" + (Split-Path $OutputPath -Leaf)
$bitmap.Save($fullOutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "Wrote $fullOutputPath"

# Draws The Dojo's icon (道, "the way", in falling-code green on the app's dark surface) into src/TheDojo/Assets.
$assets = Join-Path $PSScriptRoot "..\src\TheDojo\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null
Add-Type -AssemblyName System.Drawing

$green = [System.Drawing.Color]::FromArgb(255, 62, 224, 143)
$dark = [System.Drawing.Color]::FromArgb(255, 12, 17, 23)
$images = @()

foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $d = [Math]::Max(4, [int]($size * 0.44))
    $last = $size - $d - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($last, 0, $d, $d, 270, 90)
    $path.AddArc($last, $last, $d, $d, 0, 90)
    $path.AddArc(0, $last, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $dark), $path)
    $g.DrawPath((New-Object System.Drawing.Pen $green, ([Math]::Max(1.0, $size / 32.0))), $path)

    $font = New-Object System.Drawing.Font "Microsoft YaHei UI", ([float]($size * 0.56)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $box = New-Object System.Drawing.RectangleF 0, ([float]($size * 0.03)), $size, $size
    $g.DrawString([string][char]0x9053, $font, (New-Object System.Drawing.SolidBrush $green), $box, $format)

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @($size, $stream.ToArray())
    if ($size -eq 256) {
        $bmp.Save((Join-Path $assets "TheDojo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $g.Dispose()
    $bmp.Dispose()
}

# ICO container with PNG frames.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0)
$w.Write([uint16]1)
$w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $size = $image[0]
    $dim = 0
    if ($size -lt 256) { $dim = $size }
    $w.Write([byte]$dim)
    $w.Write([byte]$dim)
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]$image[1].Length)
    $w.Write([uint32]$offset)
    $offset += $image[1].Length
}

foreach ($image in $images) {
    $w.Write([byte[]]$image[1])
}

[System.IO.File]::WriteAllBytes((Join-Path $assets "TheDojo.ico"), $out.ToArray())
Write-Host "Wrote $assets\TheDojo.ico"

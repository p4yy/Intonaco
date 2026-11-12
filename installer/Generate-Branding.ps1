# Produces clean installer art directly from the real Intonaco .ico.
# No external assets are downloaded and the original mark is unchanged.
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot "branding"))

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$iconPath = Join-Path $PSScriptRoot "../src/Intonaco/Assets/intonaco.ico"
$icon = [System.Drawing.Icon]::new($iconPath, [System.Drawing.Size]::new(256, 256))
$source = $icon.ToBitmap()

function Build-Panel([int]$width, [int]$height, [string]$filename, [bool]$welcome) {
    $image = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(23, 36, 42))
    $foreground = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(240, 247, 245))
    $secondary = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(173, 199, 196))
    $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(131, 223, 204))
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.FillRectangle($background, 0, 0, $width, $height)

        if ($welcome) {
            $size = [int]($width * 0.70)
            $left = [int](($width - $size) / 2)
            $graphics.DrawImage($source, $left, [int]($height * 0.19), $size, $size)
            $fontTitle = [System.Drawing.Font]::new("Segoe UI", 28,
                [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $fontSubtitle = [System.Drawing.Font]::new("Segoe UI", 17,
                [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
            try {
                $format = [System.Drawing.StringFormat]::new()
                $format.Alignment = [System.Drawing.StringAlignment]::Center
                $graphics.DrawString("Intonaco Wallpaper", $fontTitle, $foreground,
                    [System.Drawing.RectangleF]::new(0, [int]($height * 0.65), $width, 58), $format)
                $graphics.DrawString("Video wallpapers for Windows", $fontSubtitle, $secondary,
                    [System.Drawing.RectangleF]::new(0, [int]($height * 0.73), $width, 38), $format)
                $graphics.FillRectangle($accent, [int]($width * 0.44),
                    [int]($height * 0.83), [int]($width * 0.12), 4)
                $format.Dispose()
            }
            finally {
                $fontTitle.Dispose()
                $fontSubtitle.Dispose()
            }
        }
        else {
            $size = [int]($width * 0.78)
            $left = [int](($width - $size) / 2)
            $graphics.DrawImage($source, $left, $left, $size, $size)
        }

        $path = Join-Path $OutputDirectory $filename
        $image.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
        Write-Host "Created installer artwork: $path"
    }
    finally {
        $graphics.Dispose()
        $image.Dispose()
        $background.Dispose()
        $foreground.Dispose()
        $secondary.Dispose()
        $accent.Dispose()
    }
}

try {
    Build-Panel 480 918 "Welcome.bmp" $true
    Build-Panel 256 256 "Header.bmp" $false
}
finally {
    $source.Dispose()
    $icon.Dispose()
}

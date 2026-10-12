$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $projectRoot 'Duplicates\Assets'
$source = [Drawing.Bitmap]::FromFile((Join-Path $projectRoot 'branding\Duplicates.png'))
$iconSizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256)
$scales = @(100, 125, 150, 200, 250, 300, 400)

function Export-Png([string]$Name, [int]$Width, [int]$Height, [double]$Fill = 1) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $side = [int][Math]::Round([Math]::Min($Width, $Height) * $Fill)
        $destination = [Drawing.Rectangle]::new(
            [int](($Width - $side) / 2), [int](($Height - $side) / 2), $side, $side)
        $graphics.DrawImage($source, $destination, 0, 0, $source.Width, $source.Height,
            [Drawing.GraphicsUnit]::Pixel, $attributes)
        $bitmap.Save((Join-Path $assetDirectory $Name), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $attributes.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    foreach ($size in $iconSizes) {
        $name = "Square44x44Logo.targetsize-$size.png"
        Export-Png $name $size $size
        foreach ($variant in @('unplated', 'lightunplated')) {
            Copy-Item -LiteralPath (Join-Path $assetDirectory $name) -Destination (
                Join-Path $assetDirectory "Square44x44Logo.targetsize-${size}_altform-$variant.png")
        }
    }

    foreach ($scale in $scales) {
        $factor = $scale / 100.0
        foreach ($baseSize in @(44, 150)) {
            $size = [int][Math]::Ceiling($baseSize * $factor)
            $fill = if ($baseSize -eq 150) { 0.88 } else { 1 }
            Export-Png "Square${baseSize}x${baseSize}Logo.scale-$scale.png" $size $size $fill
        }
        Export-Png "Wide310x150Logo.scale-$scale.png" ([int][Math]::Ceiling(310 * $factor)) ([int][Math]::Ceiling(150 * $factor)) 0.88
        Export-Png "SplashScreen.scale-$scale.png" ([int][Math]::Ceiling(620 * $factor)) ([int][Math]::Ceiling(300 * $factor)) 0.65
        $storeSize = [int][Math]::Ceiling(50 * $factor)
        Export-Png "StoreLogo.scale-$scale.png" $storeSize $storeSize
    }
    Export-Png 'StoreLogo.png' 50 50

    $frames = @($iconSizes | ForEach-Object {
        , [IO.File]::ReadAllBytes((Join-Path $assetDirectory "Square44x44Logo.targetsize-$_.png"))
    })
    $stream = [IO.File]::Create((Join-Path $assetDirectory 'AppIcon.ico'))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$iconSizes.Count)
        $offset = 6 + 16 * $iconSizes.Count
        for ($index = 0; $index -lt $iconSizes.Count; $index++) {
            $dimension = if ($iconSizes[$index] -eq 256) { 0 } else { $iconSizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}
finally {
    $source.Dispose()
}

Write-Output "Generated PNG assets at seven display scales and an ICO with $($iconSizes.Count) sizes."

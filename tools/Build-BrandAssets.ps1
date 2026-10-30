param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $Root 'src\CleanRift\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$diagnostics = Join-Path $Root 'artifacts'
New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null

function New-RoundedPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $scale = 4
    $canvas = $Size * $scale
    $bitmap = New-Object System.Drawing.Bitmap($canvas, $canvas)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $pad = $canvas * 0.04
        $side = $canvas - ($pad * 2)
        $radius = $side * 0.22
        $path = New-RoundedPath -X $pad -Y $pad -W $side -H $side -R $radius
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF($pad, $pad)),
            (New-Object System.Drawing.PointF(($pad + $side), ($pad + $side))),
            [System.Drawing.Color]::FromArgb(255, 11, 94, 107),
            [System.Drawing.Color]::FromArgb(255, 22, 163, 180))
        try {
            $g.FillPath($brush, $path)
        }
        finally {
            $brush.Dispose()
            $path.Dispose()
        }

        $white = [System.Drawing.Color]::FromArgb(245, 255, 255, 255)
        $pen = New-Object System.Drawing.Pen($white, ($canvas * 0.055))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        try {
            $box = New-Object System.Drawing.Drawing2D.GraphicsPath
            $box.AddLine($canvas * 0.27, $canvas * 0.57, $canvas * 0.27, $canvas * 0.82)
            $box.AddLine($canvas * 0.27, $canvas * 0.82, $canvas * 0.73, $canvas * 0.82)
            $box.AddLine($canvas * 0.73, $canvas * 0.82, $canvas * 0.73, $canvas * 0.57)
            $g.DrawPath($pen, $box)
            $box.Dispose()

            $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
            $arrow.AddLine($canvas * 0.50, $canvas * 0.61, $canvas * 0.50, $canvas * 0.28)
            $arrow.AddLine($canvas * 0.50, $canvas * 0.28, $canvas * 0.38, $canvas * 0.40)
            $arrow.AddLine($canvas * 0.50, $canvas * 0.28, $canvas * 0.62, $canvas * 0.40)
            $g.DrawPath($pen, $arrow)
            $arrow.Dispose()
        }
        finally {
            $pen.Dispose()
        }
    }
    finally {
        $g.Dispose()
    }

    if ($scale -eq 1) {
        return $bitmap
    }

    $result = New-Object System.Drawing.Bitmap($Size, $Size)
    $rg = [System.Drawing.Graphics]::FromImage($result)
    try {
        $rg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $rg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $rg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $rg.DrawImage($bitmap, 0, 0, $Size, $Size)
    }
    finally {
        $rg.Dispose()
        $bitmap.Dispose()
    }

    return $result
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBytes = @{}
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap -Size $size
    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBytes[$size] = $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

# Preview image for documentation.
$preview = New-IconBitmap -Size 256
$preview.Save((Join-Path $diagnostics 'cleanrift-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

$iconPath = Join-Path $assets 'app.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    foreach ($size in $sizes) {
        $data = $pngBytes[$size]
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$data.Length)
        $writer.Write([uint32]$offset)
        $offset += $data.Length
    }

    foreach ($size in $sizes) {
        $writer.Write($pngBytes[$size])
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host ("icon={0} bytes={1}" -f $iconPath, (Get-Item $iconPath).Length)
Write-Host ("preview={0}" -f (Join-Path $diagnostics 'cleanrift-256.png'))

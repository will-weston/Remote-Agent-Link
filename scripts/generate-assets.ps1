#requires -Version 7.2
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\branding')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function New-RoundedRectangle([single] $X, [single] $Y, [single] $Width, [single] $Height, [single] $Radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc(($X + $Width - $diameter), $Y, $diameter, $diameter, 270, 90)
    $path.AddArc(($X + $Width - $diameter), ($Y + $Height - $diameter), $diameter, $diameter, 0, 90)
    $path.AddArc($X, ($Y + $Height - $diameter), $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Get-LogoPng([int] $Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $surface = New-RoundedRectangle 4 4 92 92 22
    $back = New-RoundedRectangle 19 23 45 34 6
    $front = New-RoundedRectangle 36 43 45 34 6
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.RectangleF]::new(4, 4, 92, 92),
        [Drawing.Color]::FromArgb(10, 117, 236),
        [Drawing.Color]::FromArgb(94, 63, 215), [single]65)
    $glass = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(35, 255, 255, 255))
    $frontFill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(44, 98, 224))
    $white = [Drawing.Pen]::new([Drawing.Color]::White, [single]4.5)
    $link = [Drawing.Pen]::new([Drawing.Color]::White, [single]5.5)
    $memory = [IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.ScaleTransform([single]($Size / 100.0), [single]($Size / 100.0))
        $graphics.FillPath($gradient, $surface)
        $graphics.FillPath($glass, $back)
        $graphics.DrawPath($white, $back)
        $graphics.FillPath($frontFill, $front)
        $graphics.DrawPath($white, $front)
        $link.StartCap = $link.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($link, [single]43, [single]58, [single]58, [single]43)
        $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $link.Dispose()
        $white.Dispose()
        $frontFill.Dispose()
        $glass.Dispose()
        $gradient.Dispose()
        $front.Dispose()
        $back.Dispose()
        $surface.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Write-GeneratedAsset([string] $Name, [byte[]] $Bytes) {
    $path = Join-Path $OutputDirectory $Name
    if (Test-Path -LiteralPath $path) {
        $current = [IO.File]::ReadAllBytes($path)
        if ([Convert]::ToBase64String($current) -ceq [Convert]::ToBase64String($Bytes)) { return }
    }
    [IO.File]::WriteAllBytes($path, $Bytes)
}

foreach ($size in @(44, 50, 150, 256)) {
    Write-GeneratedAsset "Logo$size.png" (Get-LogoPng $size)
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$images = @($sizes | ForEach-Object { ,(Get-LogoPng $_) })
$icon = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
    $writer.Flush()
    Write-GeneratedAsset 'AgentLink.ico' $icon.ToArray()
}
finally { $writer.Dispose(); $icon.Dispose() }

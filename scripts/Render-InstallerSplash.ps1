#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Rendering the installer splash requires Windows and its Segoe UI fonts.' }
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepositoryRoot 'Assets/installer-splash.png'
}
$outputFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($outputFull) -ne '.png') { throw 'The output must be a PNG file.' }
$logoPath = (Resolve-Path -LiteralPath (Join-Path $RepositoryRoot 'Assets/logo-banner.png')).Path
if ($outputFull.Equals($logoPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output must not replace the source logo.'
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull)) | Out-Null
$disposables = [Collections.Generic.List[IDisposable]]::new()

function New-SplashBrush([string]$Hex) {
    $value = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($Hex))
    $disposables.Add($value)
    return $value
}

function New-SplashPen([string]$Hex, [single]$Width = 1) {
    $value = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($Hex), $Width)
    $disposables.Add($value)
    return $value
}

function New-SplashFont([string]$Family, [single]$Size) {
    $value = [Drawing.Font]::new($Family, $Size, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $disposables.Add($value)
    if ($value.Name -ne $Family) { throw "Required font is unavailable: $Family" }
    return $value
}

try {
    $bitmap = [Drawing.Bitmap]::new(600, 300, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $disposables.Add($bitmap)
    $bitmap.SetResolution(96, 96)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $disposables.Add($graphics)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#071828'))

    # Range rings use the application's dark-blue palette.
    $radarPen = New-SplashPen '#123246'
    $graphics.DrawEllipse($radarPen, [single]463, [single]-23, [single]238, [single]238)
    $graphics.DrawEllipse($radarPen, [single]503, [single]17, [single]158, [single]158)
    $graphics.DrawLine($radarPen, [single]490, [single]31, [single]600, [single]141)
    $graphics.DrawLine($radarPen, [single]488, [single]160, [single]600, [single]48)
    $graphics.FillEllipse((New-SplashBrush '#24536a'), [single]545, [single]84, [single]4, [single]4)

    # Preserve the logo's colours and aspect ratio without cropping.
    $logo = [Drawing.Image]::FromFile($logoPath)
    $disposables.Add($logo)
    $logoRect = [Drawing.RectangleF]::new(22, 10, 204, [single](204 * $logo.Height / $logo.Width))
    $graphics.DrawImage($logo, $logoRect)

    $format = [Drawing.StringFormat]::GenericTypographic.Clone()
    $disposables.Add($format)
    $graphics.DrawString('Sweden FIR Launchpad', (New-SplashFont 'Segoe UI Semibold' 31), (New-SplashBrush '#dfebeb'),
        [Drawing.PointF]::new(32, 120), $format)
    $graphics.DrawString("ATC tools for Sweden’s controllers", (New-SplashFont 'Segoe UI' 14), (New-SplashBrush '#a9bdcb'),
        [Drawing.PointF]::new(34, 164), $format)

    $graphics.FillRectangle((New-SplashBrush '#0d2235'), 0, 220, 600, 80)
    $graphics.DrawLine((New-SplashPen '#1a3448'), 0, 220, 600, 220)
    $graphics.DrawString('Installing Launchpad…', (New-SplashFont 'Segoe UI Semibold' 18), (New-SplashBrush '#7bd9f0'),
        [Drawing.PointF]::new(32, 241), $format)
    $graphics.DrawString('Please wait.', (New-SplashFont 'Segoe UI' 13), (New-SplashBrush '#a9bdcb'),
        [Drawing.PointF]::new(484, 245), $format)

    # Velopack draws its live progress overlay in the bottom 12 pixels.
    $graphics.FillRectangle((New-SplashBrush '#0d2235'), 0, 288, 600, 12)
    $bitmap.Save($outputFull, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    for ($index = $disposables.Count - 1; $index -ge 0; $index--) { $disposables[$index].Dispose() }
}

[pscustomobject]@{
    OutputPath = $outputFull
    Width = 600
    Height = 300
    Dpi = 96
    BottomProgressReservePixels = 12
    Sha256 = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash
}

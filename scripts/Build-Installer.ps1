#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string] $CertificateThumbprint,
    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows packaging and certificate-store signing require Windows.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repository 'VatscaUpdateChecker.csproj'
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Use a three-part semantic version, optionally with a prerelease suffix.' }

$signingDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VatscaUpdateChecker\Signing'
if (-not $CertificateThumbprint) {
    $thumbprintFile = Join-Path $signingDirectory 'development.thumbprint'
    if (-not (Test-Path -LiteralPath $thumbprintFile)) { throw 'Run scripts/New-DevelopmentCertificate.ps1 explicitly first, or supply -CertificateThumbprint.' }
    $CertificateThumbprint = (Get-Content -LiteralPath $thumbprintFile -Raw).Trim()
}
if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'The certificate thumbprint must be 40 hexadecimal characters.' }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'The selected signing certificate must have an available private key and be unexpired.' }
if (@($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) { throw 'The selected certificate must permit code signing.' }

if (-not $OutputRoot) { $OutputRoot = Join-Path $repository "artifacts\installer\$Version" }
elseif (-not [IO.Path]::IsPathFullyQualified($OutputRoot)) { $OutputRoot = Join-Path $repository $OutputRoot }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if (-not $OutputRoot.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be a child of this repository''s artifacts directory.' }
if (Test-Path -LiteralPath $OutputRoot) { throw 'Output directory already exists. Choose a fresh -OutputRoot; this script never deletes or mixes previous packages.' }
$publishDirectory = Join-Path $OutputRoot 'publish'
$releaseDirectory = Join-Path $OutputRoot 'releases'
[IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
[IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null

Push-Location $repository
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the pinned Velopack tool failed.' }
    $publishArguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        "-p:Version=$Version", "-p:InformationalVersion=$Version", '-o', $publishDirectory, '--nologo')
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publishing Launchpad failed.' }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $publishDirectory 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.txt') -Destination (Join-Path $publishDirectory 'PACKAGE-README.txt')
    $packArguments = @('tool', 'run', 'vpk', '--', '--skip-updates', '--yes', 'pack',
        '--packId', 'SwedenFirLaunchpad', '--packVersion', $Version,
        '--packTitle', 'Sweden FIR Launchpad', '--packAuthors', 'Sweden FIR Launchpad contributors',
        '--packDir', $publishDirectory, '--mainExe', 'VatscaUpdateChecker.exe',
        '--outputDir', $releaseDirectory, '--channel', 'win-x64', '--runtime', 'win-x64',
        '--framework', 'webview2', '--icon', (Join-Path $repository 'Assets\app.ico'),
        '--splashImage', (Join-Path $repository 'Assets\installer-splash.png'), '--splashProgressColor', '#42BED9',
        '--shortcuts', 'StartMenuRoot', '--delta', 'None',
        '--signParams', "/sha1 $CertificateThumbprint /s My /fd SHA256",
        '--signExclude', '(?i)(?:^|[\\/])(?:Microsoft\..*\.dll|WebView2Loader\.dll)$')
    # vpk performs its normal noninteractive Velopack startup-hook verification.
    # It builds Setup/portable/update assets; it never runs Setup or installs the app.
    & dotnet @packArguments
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
    Export-Certificate -Cert $certificate -FilePath (Join-Path $releaseDirectory 'SwedenFirLaunchpad-Signer.cer') -Type CERT -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.txt') -Destination (Join-Path $releaseDirectory 'PACKAGE-README.txt')
    & (Join-Path $PSScriptRoot 'Test-InstallerPackage.ps1') -ReleaseDirectory $releaseDirectory -CertificateThumbprint $CertificateThumbprint -ExpectedVersion $Version
    [pscustomobject]@{ Version = $Version; PublishDirectory = $publishDirectory; ReleaseDirectory = $releaseDirectory; CertificateThumbprint = $CertificateThumbprint }
}
finally { Pop-Location }

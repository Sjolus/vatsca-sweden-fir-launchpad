#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReleaseDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Fa-f0-9]{40}$')] [string] $CertificateThumbprint,
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Package signature checks require Windows.' }
$ReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$inspection = Join-Path (Split-Path -Parent $ReleaseDirectory) ('inspection-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($inspection) | Out-Null
if (-not ('LaunchpadPackaging.Authenticode' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace LaunchpadPackaging {
  public static class Authenticode {
    [StructLayout(LayoutKind.Sequential)] struct FileInfo {
      public uint Size; public IntPtr Path, File, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)] struct TrustData {
      public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice;
      public IntPtr File; public uint Action; public IntPtr State, Url;
      public uint Flags, UiContext; public IntPtr Settings;
    }
    [DllImport("wintrust.dll", ExactSpelling=true)]
    static extern int WinVerifyTrust(IntPtr window, ref Guid policy, ref TrustData data);
    public static int Verify(string path, bool hashOnly) {
      var file = new FileInfo { Size=(uint)Marshal.SizeOf<FileInfo>(), Path=Marshal.StringToCoTaskMemUni(path) };
      var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
      Marshal.StructureToPtr(file, pointer, false);
      var data = new TrustData { Size=(uint)Marshal.SizeOf<TrustData>(), Ui=2, Choice=1, File=pointer, Action=1, Flags=(uint)(0x1010 | (hashOnly ? 0x200 : 0)) };
      var policy = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
      try { return WinVerifyTrust(new IntPtr(-1), ref policy, ref data); }
      finally { data.Action=2; WinVerifyTrust(new IntPtr(-1), ref policy, ref data); Marshal.FreeHGlobal(pointer); Marshal.FreeCoTaskMem(file.Path); }
    }
  }
}
'@
}

$signatureChecks = [Collections.Generic.List[object]]::new()
$iconExecutables = [Collections.Generic.List[string]]::new()
function Assert-Signature([string] $Path, [string] $Label) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) { throw "Unexpected or missing signing certificate: $Label" }
    $hashStatus = [LaunchpadPackaging.Authenticode]::Verify($Path, $true)
    if ($hashStatus -ne 0) { throw "Authenticode hash verification failed for $Label (status $hashStatus)." }
    $trust = [LaunchpadPackaging.Authenticode]::Verify($Path, $false)
    # CERT_E_UNTRUSTEDROOT is expected for a self-signed development certificate;
    # errors such as TRUST_E_BAD_DIGEST remain failures. Never add a trusted root.
    if ($trust -ne 0 -and $trust -ne -2146762487) { throw "Authenticode integrity verification failed for $Label (status $trust)." }
    $signatureChecks.Add([pscustomobject]@{ File=$Label; Sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash; Signer=$signature.SignerCertificate.Subject; AuthenticodeHash='Verified'; TrustStatus=if ($trust -eq 0) { 'Trusted' } else { 'Self-signed; untrusted root' } })
}

$setupFiles = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Filter '*Setup.exe' -File)
$portableFiles = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Filter '*Portable.zip' -File)
$packages = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Filter '*-full.nupkg' -File)
if ($setupFiles.Count -ne 1 -or $portableFiles.Count -ne 1 -or $packages.Count -ne 1) { throw 'Expected one Setup, one portable ZIP and one full update package.' }
Assert-Signature $setupFiles[0].FullName $setupFiles[0].Name
$iconExecutables.Add($setupFiles[0].FullName)

$entryCounter = 0
foreach ($bundle in @($portableFiles[0], $packages[0])) {
    $archive = [IO.Compression.ZipFile]::OpenRead($bundle.FullName)
    try {
        $executables = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) })
        if (-not ($executables | Where-Object { $_.Name -eq 'VatscaUpdateChecker.exe' })) { throw "Main executable missing from $($bundle.Name)." }
        foreach ($entry in $executables) {
            $destination = Join-Path $inspection ((++$entryCounter).ToString('D3') + '-' + $entry.Name)
            $source = $entry.Open()
            $output = [IO.File]::Create($destination)
            try { $source.CopyTo($output) } finally { $output.Dispose(); $source.Dispose() }
            Assert-Signature $destination ($bundle.Name + ':' + $entry.FullName)
            $iconExecutables.Add($destination)
        }
        if ($bundle.Extension -eq '.nupkg') {
            $manifestEntry = $archive.Entries | Where-Object { $_.Name.EndsWith('.nuspec') } | Select-Object -First 1
            if (-not $manifestEntry) { throw 'Update package has no manifest.' }
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($manifest.package.metadata.id -ne 'SwedenFirLaunchpad') { throw 'The installer package ID would collide with the application data folder.' }
            $metadata = $manifest.package.metadata
            if ($metadata.title -ne 'Sweden FIR Launchpad' -or $metadata.mainExe -ne 'VatscaUpdateChecker.exe') { throw 'Unexpected package title or entry point.' }
            if ($metadata.channel -ne 'win-x64' -or $metadata.rid -ne 'win-x64' -or $metadata.machineArchitecture -ne 'x64') { throw 'Unexpected package release channel or architecture.' }
            if ($metadata.runtimeDependencies -ne 'webview2') { throw 'Expected WebView2 as the only bootstrap prerequisite for the self-contained app.' }
            if ($metadata.shortcutLocations -ne 'StartMenuRoot' -or $metadata.shortcutAumid -ne 'velopack.SwedenFirLaunchpad') { throw 'Unexpected shortcut locations or taskbar application identity.' }
            $packageVersion = [string]$metadata.version
            if ($ExpectedVersion -and $packageVersion -ne $ExpectedVersion) { throw 'The package version differs from the requested build version.' }
            $mainFile = Get-ChildItem -LiteralPath $inspection -Filter '*-VatscaUpdateChecker.exe' -File | Select-Object -First 1
            $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($mainFile.FullName).ProductVersion
            if ($productVersion.Split('+')[0] -ne $packageVersion) { throw 'The application executable and package versions differ.' }
            foreach ($requiredFile in @('LICENSE.txt', 'PACKAGE-README.txt')) {
                if (-not ($archive.Entries | Where-Object { $_.Name -eq $requiredFile })) { throw "Distribution notice missing: $requiredFile" }
            }
        }
    }
    finally { $archive.Dispose() }
}

$iconChecks = & (Join-Path $PSScriptRoot 'Test-InstallerIcons.ps1') -ExecutablePaths $iconExecutables.ToArray() -FullPackagePath $packages[0].FullName
$iconChecks | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReleaseDirectory 'icon-verification.json') -Encoding utf8

$splashCheck = & (Join-Path $PSScriptRoot 'Test-InstallerSplash.ps1') -FullPackagePath $packages[0].FullName -SetupPath $setupFiles[0].FullName
$splashCheck | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReleaseDirectory 'splash-verification.json') -Encoding utf8

$feedPath = Join-Path $ReleaseDirectory 'releases.win-x64.json'
if (-not (Test-Path -LiteralPath $feedPath)) { throw 'The win-x64 update feed is missing.' }
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
if (-not $feed.Assets) { throw 'The update feed has no assets.' }
foreach ($asset in $feed.Assets) {
    if ($asset.PackageId -ne 'SwedenFirLaunchpad' -or $asset.Version -ne $packageVersion) { throw 'The update feed identity/version differs from its package.' }
    if ([IO.Path]::GetFileName($asset.FileName) -ne $asset.FileName) { throw 'The update feed contains a path rather than a filename.' }
    $assetPath = Join-Path $ReleaseDirectory $asset.FileName
    if (-not (Test-Path -LiteralPath $assetPath)) { throw 'A package referenced by the update feed is missing.' }
    if ((Get-Item -LiteralPath $assetPath).Length -ne $asset.Size) { throw 'The update feed package size does not match.' }
    if ((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash -ne $asset.SHA256) { throw 'The update feed package hash does not match.' }
}

# Prove digest checks reject tampering even though the certificate is untrusted.
$fixture = Get-ChildItem -LiteralPath $inspection -Filter '*.exe' -File | Sort-Object Length | Select-Object -First 1
$tampered = Join-Path $inspection 'signature-tamper-fixture.bin'
[IO.File]::Copy($fixture.FullName, $tampered)
if ([LaunchpadPackaging.Authenticode]::Verify($tampered, $true) -ne 0) { throw 'The original tamper fixture did not verify.' }
$bytes = [IO.File]::ReadAllBytes($tampered)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
$sectionTable = $peOffset + 24 + [BitConverter]::ToUInt16($bytes, $peOffset + 20)
$contentOffset = [BitConverter]::ToUInt32($bytes, $sectionTable + 20)
if ($contentOffset -le 0 -or $contentOffset -ge $bytes.Length) { throw 'Could not locate signed PE data for the tamper check.' }
$bytes[$contentOffset] = $bytes[$contentOffset] -bxor 1
[IO.File]::WriteAllBytes($tampered, $bytes)
$tamperStatus = [LaunchpadPackaging.Authenticode]::Verify($tampered, $true)
if ($tamperStatus -eq 0) { throw 'Hash-only verification accepted modified signed data.' }
$tamperTrustStatus = [LaunchpadPackaging.Authenticode]::Verify($tampered, $false)
if ($tamperTrustStatus -ne -2146869232) { throw "The modified signed fixture did not produce TRUST_E_BAD_DIGEST (status $tamperTrustStatus)." }
$signatureChecks | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReleaseDirectory 'signature-verification.json') -Encoding utf8
Write-Host "Verified $($signatureChecks.Count) executable signatures and icons, setup.ico, installer splash, embedded package, tamper rejection, package metadata and $(@($feed.Assets).Count) update-feed package hashes. No executable was launched."

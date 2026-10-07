#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FullPackagePath,
    [Parameter(Mandatory)] [string] $SetupPath,
    [string] $SourceSplash = (Join-Path $PSScriptRoot '..\Assets\installer-splash.png')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Splash image verification requires Windows.' }
Add-Type -AssemblyName System.Drawing
if (-not ('LaunchpadPackaging.SplashBundleVerification' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Security.Cryptography;

namespace LaunchpadPackaging {
    public static class SplashBundleVerification {
        // Velopack 1.2.161: two little-endian Int64 values precede this marker.
        // https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/vpk/Velopack.Packaging.Windows/SetupBundle.cs
        static readonly byte[] Marker = Convert.FromHexString("94f0b17b6893e02937eb34ef53aae7d42b54f5707ef5d6f57854983e5e94ed7d");

        static long FindMarker(FileStream stream) {
            var buffer = new byte[65536 + Marker.Length - 1];
            int retained = 0;
            long start = 0;
            while (true) {
                int read = stream.Read(buffer, retained, buffer.Length - retained);
                if (read == 0) throw new InvalidDataException("Setup has no Velopack bundle marker.");
                int available = retained + read;
                for (int i = 0; i <= available - Marker.Length; i++)
                    if (buffer[i] == Marker[0] && buffer.AsSpan(i, Marker.Length).SequenceEqual(Marker))
                        return start + i;
                retained = Math.Min(available, Marker.Length - 1);
                Buffer.BlockCopy(buffer, available - retained, buffer, 0, retained);
                start += available - retained;
            }
        }

        public static string Verify(string setupPath, string packagePath) {
            using var setup = File.OpenRead(setupPath);
            using var package = File.OpenRead(packagePath);
            long markerPosition = FindMarker(setup);
            if (markerPosition < 16) throw new InvalidDataException("Setup bundle header is truncated.");
            setup.Position = markerPosition - 16;
            using var reader = new BinaryReader(setup, System.Text.Encoding.UTF8, true);
            long offset = reader.ReadInt64();
            long length = reader.ReadInt64();
            if (length <= 0 || length != package.Length || offset < markerPosition + Marker.Length ||
                offset > setup.Length || length > setup.Length - offset)
                throw new InvalidDataException("Setup bundle offset or length is invalid.");
            setup.Position = offset;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long remaining = length;
            while (remaining > 0) {
                int read = setup.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new InvalidDataException("Setup bundle is truncated.");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            byte[] embeddedHash = hash.GetHashAndReset();
            byte[] packageHash = SHA256.HashData(package);
            if (!CryptographicOperations.FixedTimeEquals(embeddedHash, packageHash))
                throw new InvalidDataException("Setup embeds a different full package.");
            return Convert.ToHexString(packageHash);
        }
    }
}
'@
}

function Read-BoundedBytes([IO.Stream] $Stream, [long] $Length, [long] $Maximum) {
    if ($Length -le 0 -or $Length -gt $Maximum) { throw 'Splash verification input exceeds its size limit.' }
    $bytes = [byte[]]::new([int]$Length)
    $Stream.ReadExactly($bytes, 0, $bytes.Length)
    if ($Stream.ReadByte() -ne -1) { throw 'Splash verification input exceeds its declared length.' }
    return ,$bytes
}

$sourceStream = [IO.File]::OpenRead([IO.Path]::GetFullPath($SourceSplash))
try { $sourceBytes = Read-BoundedBytes $sourceStream $sourceStream.Length 4194304 }
finally { $sourceStream.Dispose() }
$sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes))
$imageStream = [IO.MemoryStream]::new($sourceBytes, $false)
$image = $null
try {
    $image = [Drawing.Bitmap]::new($imageStream)
    if ($image.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid -or $image.Width -ne 600 -or $image.Height -ne 300) {
        throw 'Installer splash must be a 600 x 300 PNG.'
    }
    $stripColor = $image.GetPixel(0, 288).ToArgb()
    for ($y = 0; $y -lt 300; $y++) {
        for ($x = 0; $x -lt 600; $x++) {
            $pixel = $image.GetPixel($x, $y)
            if ($pixel.A -ne 255) { throw 'Installer splash must be fully opaque.' }
            if ($y -ge 288 -and $pixel.ToArgb() -ne $stripColor) { throw 'Reserve a uniform bottom 12-pixel strip for installer progress.' }
        }
    }
}
finally { if ($image) { $image.Dispose() }; $imageStream.Dispose() }

$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($FullPackagePath))
try {
    # Velopack searches for any name containing splashimage; reject ambiguous or nested matches.
    $splashEntries = @($archive.Entries | Where-Object { $_.FullName.Contains('splashimage', [StringComparison]::OrdinalIgnoreCase) })
    if ($splashEntries.Count -ne 1 -or $splashEntries[0].FullName -cne 'splashimage.png') { throw 'Full package must contain exactly one root splashimage.png.' }
    $stream = $splashEntries[0].Open()
    try { $packagedBytes = Read-BoundedBytes $stream $splashEntries[0].Length 4194304 }
    finally { $stream.Dispose() }
    $packagedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($packagedBytes))
    if ($packagedBytes.Length -ne $sourceBytes.Length -or $packagedHash -cne $sourceHash) { throw 'Packaged splash differs from the source PNG.' }

    $manifests = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
    if ($manifests.Count -ne 1) { throw 'Full package must contain exactly one manifest.' }
    $stream = $manifests[0].Open()
    $xmlReader = $null
    try {
        $options = [Xml.XmlReaderSettings]::new()
        $options.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $options.XmlResolver = $null
        $options.MaxCharactersInDocument = 262144
        $xmlReader = [Xml.XmlReader]::Create($stream, $options)
        $manifest = [Xml.XmlDocument]::new()
        $manifest.XmlResolver = $null
        $manifest.Load($xmlReader)
        if ([string]$manifest.package.metadata.splashProgressColor -cne '#42BED9') { throw 'Full package must use the configured #42BED9 progress color.' }
    }
    finally { if ($xmlReader) { $xmlReader.Dispose() }; $stream.Dispose() }
}
finally { $archive.Dispose() }

$packageHash = [LaunchpadPackaging.SplashBundleVerification]::Verify([IO.Path]::GetFullPath($SetupPath), [IO.Path]::GetFullPath($FullPackagePath))
[pscustomobject]@{
    Splash = 'splashimage.png'
    Dimensions = '600x300'
    Opaque = $true
    ProgressStripHeight = 12
    ProgressColor = '#42BED9'
    SplashSha256 = $sourceHash
    EmbeddedPackageSha256 = $packageHash
    SetupPackage = 'Matches full update package'
}

#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Development signing requires Windows.' }

$signingDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VatscaUpdateChecker\Signing'
$thumbprintFile = Join-Path $signingDirectory 'development.thumbprint'
$publicCertificateFile = Join-Path $signingDirectory 'SwedenFirLaunchpad-Development.cer'
$subject = 'CN=Sweden FIR Launchpad Development'
$certificate = $null

if (Test-Path -LiteralPath $thumbprintFile) {
    $savedThumbprint = (Get-Content -LiteralPath $thumbprintFile -Raw).Trim()
    if ($savedThumbprint -match '^[A-Fa-f0-9]{40}$') {
        $candidate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$savedThumbprint" -ErrorAction SilentlyContinue
        if ($candidate -and $candidate.Subject -eq $subject -and $candidate.HasPrivateKey -and
            $candidate.NotAfter -gt (Get-Date).AddDays(30) -and
            @($candidate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0) {
            $certificate = $candidate
        }
    }
}

if (-not $certificate) {
    # This creates a private key only in the current user's Personal store.
    # It never imports a root certificate, changes trust, or exports a private key.
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -FriendlyName 'Sweden FIR Launchpad Development (self-signed)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 `
        -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
}

[IO.Directory]::CreateDirectory($signingDirectory) | Out-Null
Set-Content -LiteralPath $thumbprintFile -Value $certificate.Thumbprint -Encoding ascii -NoNewline
Export-Certificate -Cert $certificate -FilePath $publicCertificateFile -Type CERT -Force | Out-Null

[pscustomobject]@{
    Thumbprint = $certificate.Thumbprint
    PublicCertificatePath = $publicCertificateFile
    Subject = $certificate.Subject
    Expires = $certificate.NotAfter
}

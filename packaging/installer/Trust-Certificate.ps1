param(
    [Parameter(Mandatory = $true)][string]$CertificatePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ExpectedThumbprint
)
$ErrorActionPreference = 'Stop'
try {
    $cert = New-Object -TypeName Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $CertificatePath
    if ($cert.HasPrivateKey -or $cert.Thumbprint -ne $ExpectedThumbprint -or $cert.NotAfter -lt (Get-Date)) {
        throw 'The public certificate does not match this installer.'
    }
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    exit 0
}
catch { exit 1 }

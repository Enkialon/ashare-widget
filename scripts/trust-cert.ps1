# Import the sideload signing cert into LocalMachine\TrustedPeople (needs Admin).
# Usage:
#   pwsh scripts/trust-cert.ps1
#   # or elevate automatically:
#   pwsh scripts/trust-cert.ps1 -Elevate

param([switch]$Elevate)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$cerPath = Join-Path $repoRoot ".certs\ashare-widget.cer"

if (-not (Test-Path $cerPath)) {
    Write-Host "Cert not found. Run: pwsh scripts/sideload.ps1 -PackOnly"
    exit 1
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    if ($Elevate) {
        Start-Process pwsh -Verb RunAs -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $PSCommandPath
        ) -Wait
        exit $LASTEXITCODE
    }
    Write-Host "Need Administrator. Re-run:"
    Write-Host "  pwsh scripts/trust-cert.ps1 -Elevate"
    exit 1
}

Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
Write-Host "Trusted: $cerPath -> LocalMachine\TrustedPeople"
Write-Host "Next: pwsh scripts/sideload.ps1 -SkipBuild"

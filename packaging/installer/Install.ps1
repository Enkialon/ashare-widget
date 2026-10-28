# Compatible with the Windows PowerShell 5.1 included in Windows 11.
param([switch]$ValidateOnly, [switch]$CertificateOnly)

$ErrorActionPreference = 'Stop'

function Read-InstallBundle([string]$Directory) {
    $release = Get-Content -LiteralPath (Join-Path $Directory 'release.json') -Raw | ConvertFrom-Json
    foreach ($file in $release.files.PSObject.Properties) {
        if ($file.Name -ne [IO.Path]::GetFileName($file.Name)) { throw 'Invalid payload filename.' }
        $path = Join-Path $Directory $file.Name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing installer payload: $($file.Name). Download the installer again." }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Value) {
            throw "File verification failed: $($file.Name). Download the installer again."
        }
    }
    $certificate = New-Object -TypeName Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (
        Join-Path $Directory 'AShareWidget.cer')
    if ($certificate.HasPrivateKey -or $certificate.Thumbprint -ne $release.certificateThumbprint -or
        $certificate.Subject -ne $release.publisher) { throw 'Signing certificate does not match this release.' }
    if ($certificate.NotAfter -lt (Get-Date)) { throw 'The developer signing certificate has expired. Ask for a new installer.' }
    return $release
}

function Test-WidgetRuntime($Release) {
    $packages = @(Get-AppxPackage -Name $Release.runtimeName -ErrorAction SilentlyContinue)
    return @($packages | Where-Object {
        $_.Architecture -eq 'X64' -and [version]$_.Version -ge [version]$Release.runtimeMinVersion
    }).Count -gt 0
}

function Trust-WidgetCertificate([string]$Directory, $Release) {
    $trustedPath = 'Cert:\LocalMachine\TrustedPeople\' + $Release.certificateThumbprint
    if (Test-Path -LiteralPath $trustedPath) { return }
    Write-Host 'First installation: Windows will ask permission to trust the developer certificate.'
    # Elevate only certificate import. MSIX registration must remain in the
    # original user's process, even when UAC asks for a different admin account.
    $helper = Join-Path $Directory 'Trust-Certificate.ps1'
    $certificate = Join-Path $Directory 'AShareWidget.cer'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -CertificatePath "{1}" -ExpectedThumbprint {2}' -f
        $helper, $certificate, $Release.certificateThumbprint
    $process = Start-Process -FilePath "$PSHOME\powershell.exe" -Verb RunAs -WindowStyle Hidden `
        -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $trustedPath)) {
        throw 'Certificate trust was not completed. Allow the administrator prompt and try again.'
    }
}

function Install-WidgetRuntime([string]$Directory, $Release) {
    if (Test-WidgetRuntime $Release) { return }
    Write-Host 'Installing Microsoft Windows App Runtime 1.6...'
    $runtime = Join-Path $Directory 'WindowsAppRuntimeInstall-x64.exe'
    $signature = Get-AuthenticodeSignature -FilePath $runtime
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
        throw 'Microsoft runtime signature verification failed.'
    }
    $process = Start-Process -FilePath $runtime -ArgumentList '--quiet' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or !(Test-WidgetRuntime $Release)) {
        throw ('Windows App Runtime installation failed (exit code {0}).' -f $process.ExitCode)
    }
}

function Install-WidgetPackage([string]$Directory, $Release) {
    $existing = Get-AppxPackage -Name $Release.name -ErrorAction SilentlyContinue
    if ($existing -and [version]$existing.Version -ge [version]$Release.version) {
        Write-Host "Version $($existing.Version) is already installed. No downgrade or reinstall is needed."
        return
    }
    Write-Host "Installing AShareWidget $($Release.version)..."
    Add-AppxPackage -Path (Join-Path $Directory 'AShareWidget.msix') -ForceApplicationShutdown
    $installed = Get-AppxPackage -Name $Release.name -ErrorAction Stop
    if (!$installed -or [version]$installed.Version -lt [version]$Release.version -or $installed.Status -ne 'Ok') {
        throw 'Windows did not complete application registration.'
    }
}

function Invoke-WidgetInstall([string]$Directory, [switch]$CheckOnly, [switch]$TrustOnly) {
    if ([Environment]::OSVersion.Version.Build -lt 22000) { throw 'This widget requires Windows 11.' }
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'X64') {
        throw 'This installer currently supports Intel/AMD x64 Windows only.'
    }
    $release = Read-InstallBundle $Directory
    if ($CheckOnly) {
        Write-Host "Verified installer payload: AShareWidget $($release.version). No system changes made."
        return
    }
    Trust-WidgetCertificate $Directory $release
    if ($TrustOnly) { return }
    Install-WidgetRuntime $Directory $release
    Install-WidgetPackage $Directory $release
    Write-Host 'Completed. Press Win + W, then add the stock quote widget using the + button.'
    Write-Host 'Existing pinned widgets and your stock configuration are preserved during upgrades.'
}

# Dot sourcing defines functions only, allowing regression checks without deployment.
if ($MyInvocation.InvocationName -eq '.') { return }
$installLog = Join-Path $env:TEMP 'AShareWidget-install.log'
$transcriptStarted = $false
try {
    Start-Transcript -LiteralPath $installLog -Force | Out-Null
    $transcriptStarted = $true
    Invoke-WidgetInstall -Directory $PSScriptRoot -CheckOnly:$ValidateOnly -TrustOnly:$CertificateOnly
    exit 0
}
catch {
    Write-Host ('Installation failed: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host "Installation log: $installLog"
    exit 1
}
finally {
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}

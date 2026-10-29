# Build / sign / sideload AShareWidget MSIX for Win+W testing.
#
# Usage:
#   pwsh scripts/sideload.ps1
#   pwsh scripts/sideload.ps1 -PackOnly
#   pwsh scripts/sideload.ps1 -SkipBuild
#   pwsh scripts/sideload.ps1 -Uninstall
#
# Prerequisites:
#   - .NET 8 SDK
#   - Windows SDK (makeappx.exe + signtool.exe)
#   - Developer Mode OR trusted self-signed cert
#   - Windows App Runtime 1.6 installed

param(
    [switch]$SkipBuild,
    [switch]$PackOnly,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$identityName = "AShareWidget"
$publisher = "CN=AShareWidget Dev"
$friendlyName = "AShareWidget Dev Cert"
$certDir = Join-Path $repoRoot ".certs"
$pfxPath = Join-Path $certDir "ashare-widget.pfx"
$cerPath = Join-Path $certDir "ashare-widget.cer"
$pfxPasswordPlain = "AShareWidget-Dev"
$staging = Join-Path $repoRoot "sideload-staging"
$msixPath = Join-Path $repoRoot "AShareWidget.msix"
$widgetPublish = Join-Path $repoRoot "publish\win-x64"
$manifestSrc = Join-Path $repoRoot "packaging\AppxManifest.xml"
$assetsSrc = Join-Path $repoRoot "packaging\Assets"

function Find-SdkTool([string]$Name) {
    $hit = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\$Name" `
        -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $hit) {
        throw "$Name not found under Windows Kits\10\bin. Install the Windows SDK."
    }
    return $hit.FullName
}

function Get-InstalledPackage {
    Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue
}

function Uninstall-Package {
    $pkg = Get-InstalledPackage
    if (-not $pkg) {
        Write-Host "No installed package ($identityName)."
        return
    }
    Write-Host "Removing $($pkg.PackageFullName) ..."
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host "Uninstalled."
}

if ($Uninstall) {
    Uninstall-Package
    exit 0
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

$exe = Join-Path $widgetPublish "AShareWidget.Provider.exe"
if (-not (Test-Path $exe)) {
    throw "Provider publish missing: $exe (run without -SkipBuild)"
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding $false
$manifestText = [System.IO.File]::ReadAllText(
    [System.IO.Path]::GetFullPath($manifestSrc), $utf8)
[System.IO.File]::WriteAllText((Join-Path $staging "AppxManifest.xml"), $manifestText, $utf8)

$assetsDest = Join-Path $staging "Assets"
New-Item -ItemType Directory -Force -Path $assetsDest | Out-Null
Copy-Item (Join-Path $assetsSrc "*") $assetsDest -Force

$widgetDest = Join-Path $staging "WidgetProvider"
New-Item -ItemType Directory -Force -Path $widgetDest | Out-Null
Copy-Item (Join-Path $widgetPublish "*") $widgetDest -Recurse -Force
Get-ChildItem $widgetDest -Filter "*.pdb" -Recurse | Remove-Item -Force
Get-ChildItem $widgetDest -Filter "*.log" -Recurse | Remove-Item -Force

New-Item -ItemType Directory -Force -Path (Join-Path $staging "Public") | Out-Null

New-Item -ItemType Directory -Force -Path $certDir | Out-Null
$securePwd = ConvertTo-SecureString $pfxPasswordPlain -Force -AsPlainText

if (-not (Test-Path $pfxPath)) {
    Write-Host "Creating self-signed cert ($publisher) ..."
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -KeyUsage DigitalSignature `
        -Subject $publisher `
        -FriendlyName $friendlyName `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
            "2.5.29.19={text}"
        )
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
}

Write-Host "Ensuring cert is trusted for sideload ..."
try {
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
} catch {
    Write-Host "WARN: could not import to LocalMachine\TrustedPeople (need Admin?). Trying CurrentUser ..."
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
}

$makeappx = Find-SdkTool "makeappx.exe"
$signtool = Find-SdkTool "signtool.exe"

if (Test-Path $msixPath) {
    Remove-Item $msixPath -Force
}

Write-Host "Packing $msixPath ..."
& $makeappx pack /d $staging /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed"
}

Write-Host "Signing ..."
& $signtool sign /fd SHA256 /a /f $pfxPath /p $pfxPasswordPlain $msixPath
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed"
}

Write-Host "Signed MSIX: $msixPath"
if ($PackOnly) {
    exit 0
}

$existing = Get-InstalledPackage
if ($existing) {
    Write-Host "Removing previous install ..."
    try {
        Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction Stop
    }
    catch {
        Write-Host "Remove-AppxPackage skipped: $($_.Exception.Message)"
    }
}

Write-Host "Installing ..."
Add-AppxPackage -Path $msixPath
Write-Host ""
Write-Host "Installed. Next:"
Write-Host "  1. Win + W  open Widgets Board"
Write-Host "  2. Find '股票行情' and pin it"
Write-Host "  3. Edit %USERPROFILE%\AppData\Local\AShareWidget\stocks.json (codes: tencent:sh600519 / sina:gds_AU9999)"
Write-Host "Uninstall: pwsh scripts/sideload.ps1 -Uninstall"

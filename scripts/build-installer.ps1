# Build one Inno Setup EXE from an already signed MSIX. Does not install anything.
param(
    [string]$PackagePath = '',
    [string]$RuntimeInstallerPath = '',
    [string]$CompilerPath = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$PackagePath) { $PackagePath = Join-Path $repoRoot 'AShareWidget.msix' }
if (!$RuntimeInstallerPath) { $RuntimeInstallerPath = Join-Path $repoRoot 'publish/runtime-cache/WindowsAppRuntimeInstall-x64.exe' }
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$RuntimeInstallerPath = (Resolve-Path -LiteralPath $RuntimeInstallerPath).Path
$certificatePath = Join-Path $repoRoot '.certs/ashare-widget.cer'
$certificate = New-Object -TypeName Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $certificatePath
$signature = Get-AuthenticodeSignature -FilePath $PackagePath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'Use a valid MSIX signed with the certificate in .certs/ashare-widget.cer.'
}
$runtimeSignature = Get-AuthenticodeSignature -FilePath $RuntimeInstallerPath
if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
    throw 'The runtime installer must have a valid Microsoft signature.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $reader = New-Object IO.StreamReader($package.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $package.Dispose() }
$identity = $manifest.Package.Identity
$dependency = $manifest.Package.Dependencies.PackageDependency | Where-Object { $_.Name -like 'Microsoft.WindowsAppRuntime.*' }
$output = Join-Path $repoRoot 'dist'
# Each build gets its own workspace; do not recursively remove previous releases.
$payload = Join-Path $repoRoot ('publish/installer-payload-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $output, $payload | Out-Null
Copy-Item -LiteralPath $PackagePath -Destination (Join-Path $payload 'AShareWidget.msix')
Copy-Item -LiteralPath $certificatePath -Destination (Join-Path $payload 'AShareWidget.cer')
Copy-Item -LiteralPath $RuntimeInstallerPath -Destination (Join-Path $payload 'WindowsAppRuntimeInstall-x64.exe')
foreach ($name in @('Install.ps1', 'Trust-Certificate.ps1')) {
    $scriptText = [IO.File]::ReadAllText((Join-Path $repoRoot "packaging/installer/$name"))
    # PowerShell 5.1 interprets UTF-8 reliably with a BOM.
    [IO.File]::WriteAllText((Join-Path $payload $name), $scriptText, (New-Object Text.UTF8Encoding($true)))
}
$hashes = [ordered]@{}
Get-ChildItem -LiteralPath $payload -File | ForEach-Object { $hashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$release = [ordered]@{
    name = [string]$identity.Name; version = [string]$identity.Version; publisher = [string]$identity.Publisher
    certificateThumbprint = $certificate.Thumbprint
    runtimeName = [string]$dependency.Name; runtimeMinVersion = [string]$dependency.MinVersion
    files = $hashes
}
$release | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'release.json') -Encoding UTF8
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $payload 'Install.ps1') -ValidateOnly
if ($LASTEXITCODE -ne 0) { throw 'Installer payload validation failed.' }
if (!$CompilerPath) { $CompilerPath = (Get-Command iscc.exe -ErrorAction Stop).Source }
& $CompilerPath /Qp "/DPayloadDir=$payload" "/DAppVersion=$($identity.Version)" "/DCertificateThumbprint=$($certificate.Thumbprint)" "/DOutputDir=$output" (Join-Path $repoRoot 'packaging/installer/AShareWidget.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$installer = Join-Path $output "AShareWidget-$($identity.Version)-win-x64-Setup.exe"
Write-Host "Installer: $installer"
Get-FileHash -LiteralPath $installer -Algorithm SHA256

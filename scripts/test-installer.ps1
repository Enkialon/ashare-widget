# Isolated regression checks: no certificate imports, runtime execution or MSIX deployment.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'packaging/installer/Install.ps1')
function Assert($condition, [string]$message) {
    if (!$condition) { throw "FAIL: $message" }
    Write-Host "PASS: $message"
}
$fixture = Join-Path $repo ('publish/installer-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
Copy-Item -LiteralPath (Join-Path $repo '.certs/ashare-widget.cer') -Destination (Join-Path $fixture 'AShareWidget.cer')
'test payload; not a deployable MSIX' | Set-Content -LiteralPath (Join-Path $fixture 'AShareWidget.msix')
$cert = New-Object -TypeName Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (Join-Path $fixture 'AShareWidget.cer')
$release = [ordered]@{
    name = 'AShareWidget'; version = '0.2.12.0'; publisher = $cert.Subject
    certificateThumbprint = $cert.Thumbprint; runtimeName = 'Microsoft.WindowsAppRuntime.1.6'
    runtimeMinVersion = '6000.401.2352.0'
    files = @{ 'AShareWidget.msix' = (Get-FileHash (Join-Path $fixture 'AShareWidget.msix')).Hash }
}
$release | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $fixture 'release.json') -Encoding UTF8
$metadata = Read-InstallBundle $fixture
Assert ($metadata.version -eq '0.2.12.0') 'bundle metadata and public certificate are accepted'
'tampered' | Set-Content (Join-Path $fixture 'AShareWidget.msix')
$rejected = $false
try { Read-InstallBundle $fixture | Out-Null } catch { $rejected = $true }
Assert $rejected 'damaged payload is rejected before any trust or install operation'

$script:version = '0.2.11.0'
$script:deployments = 0
$script:trust = $false
$script:runtimeReady = $false
$script:processes = @()
$script:processExitCode = 0
function Get-AppxPackage {
    param($Name)
    if ($Name -eq 'AShareWidget' -and $script:version) {
        return [pscustomobject]@{ Version = $script:version; Status = 'Ok' }
    }
}
function Add-AppxPackage {
    param($Path, [switch]$ForceApplicationShutdown)
    Assert $ForceApplicationShutdown 'upgrade registers in the calling user without uninstalling'
    $script:deployments++
    $script:version = '0.2.12.0'
}
function Remove-AppxPackage { throw 'Uninstall must never be used during upgrades.' }
Install-WidgetPackage $fixture $metadata
Assert ($script:deployments -eq 1) 'older version upgrades exactly once'
Install-WidgetPackage $fixture $metadata
Assert ($script:deployments -eq 1) 'same version does not reinstall'
$script:version = '0.2.13.0'
Install-WidgetPackage $fixture $metadata
Assert ($script:deployments -eq 1) 'newer version is not downgraded'

function Test-Path {
    param($LiteralPath, $PathType)
    if ($LiteralPath -like 'Cert:*') { return $script:trust }
    return Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath
}
function Start-Process {
    param($FilePath, $ArgumentList, $Verb, $WindowStyle, [switch]$Wait, [switch]$PassThru)
    $script:processes += [pscustomobject]@{ File = $FilePath; Args = $ArgumentList; Verb = $Verb; Style = $WindowStyle }
    if ($script:processExitCode -eq 0) {
        if ($Verb -eq 'RunAs') { $script:trust = $true } else { $script:runtimeReady = $true }
    }
    return [pscustomobject]@{ ExitCode = $script:processExitCode }
}
Trust-WidgetCertificate $fixture $metadata
Assert ($script:processes.Count -eq 1 -and $script:processes[0].Verb -eq 'RunAs' -and
    $script:processes[0].Style -eq 'Hidden' -and $script:processes[0].Args -like '*Trust-Certificate.ps1*') 'only certificate helper is elevated'
Trust-WidgetCertificate $fixture $metadata
Assert ($script:processes.Count -eq 1) 'trusted certificate skips subsequent UAC prompts'
$script:trust = $false
$script:processExitCode = 1
$rejected = $false
try { Trust-WidgetCertificate $fixture $metadata } catch { $rejected = $true }
Assert $rejected 'cancelled or failed certificate trust stops installation'

function Test-WidgetRuntime { param($Release) return $script:runtimeReady }
function Get-AuthenticodeSignature {
    param($FilePath)
    return [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation, C=US' } }
}
$script:processExitCode = 0
$script:processes = @()
Install-WidgetRuntime $fixture $metadata
Assert ($script:processes.Count -eq 1 -and !$script:processes[0].Verb -and
    $script:processes[0].Args -eq '--quiet') 'missing runtime installs silently in original user context'
Install-WidgetRuntime $fixture $metadata
Assert ($script:processes.Count -eq 1) 'existing runtime is not reinstalled'

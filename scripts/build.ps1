# Build self-contained Widget Provider for MSIX packaging.
# Usage:
#   pwsh scripts/build.ps1
#   pwsh scripts/build.ps1 -OutputDir path\to\out

param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$proj = Join-Path $repoRoot "src\AShareWidget.Provider\AShareWidget.Provider.csproj"
if (-not (Test-Path $proj)) {
    throw "Project not found: $proj"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "publish\win-x64"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $localDotnet) {
        $dotnetCmd = $localDotnet
    } else {
        throw "dotnet SDK not found. Install .NET 8 SDK."
    }
} else {
    $dotnetCmd = $dotnet.Source
}

Write-Host "dotnet: $dotnetCmd"
Write-Host "Project: $proj"
Write-Host "Output:  $OutputDir"

Remove-Item $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

& $dotnetCmd publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $OutputDir "AShareWidget.Provider.exe"
if (-not (Test-Path $exe)) {
    throw "Publish succeeded but exe missing: $exe"
}

Write-Host "OK: $exe"
Get-ChildItem $OutputDir | Measure-Object -Property Length -Sum |
    ForEach-Object { Write-Host ("Publish size: {0:N1} MB" -f ($_.Sum / 1MB)) }

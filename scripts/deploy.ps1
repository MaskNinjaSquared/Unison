param(
    [string]$IP = "127.0.0.1",
    [string]$Platform = "ARM"
)

Write-Host "Deploying Unison with configuration 'Release' and platform '$Platform' to IP '$IP'..." -ForegroundColor Cyan

$ErrorActionPreference = "Stop"

$kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$winAppDeployCmd = Get-ChildItem $kitRoot -Recurse -Filter WinAppDeployCmd.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $winAppDeployCmd) {
    throw "WinAppDeployCmd.exe not found."
}

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$packageRoot = Join-Path $root "src\Unison.Uwp\AppPackages"
if (-not (Test-Path $packageRoot)) {
    throw "Package folder not found: $packageRoot (build/sign first)."
}

# Prefer bundle when present (AppxBundle=Always in Unison.Uwp.csproj).
$appx = Get-ChildItem $packageRoot -Recurse -Include "*.appxbundle","*.msixbundle","*.appx","*.msix" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "_${Platform}_" -and $_.FullName -match "_Test" } |
    Sort-Object @{ Expression = {
        if ($_.Extension -match 'bundle') { 0 } else { 1 }
    } }, FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $appx) {
    throw "APPX/MSIX package not found under $packageRoot for platform '$Platform'."
}

Write-Host "Package: $appx"
Write-Host "Listing connected devices..."
& $winAppDeployCmd devices

Write-Host "Installing AppX package..."
& $winAppDeployCmd install -file $appx -ip $IP

$code = $LASTEXITCODE

if ($code -eq 0) {
    Write-Host "Deploy succeeded." -ForegroundColor Cyan
}
else {
    Write-Host "Deploy failed with exit code $code." -ForegroundColor Red
}

exit $code

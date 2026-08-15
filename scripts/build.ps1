param(
    [string]$Configuration = "Release",
    [string]$Platform = "ARM"
)

Write-Host "Building Unison with configuration '$Configuration' and platform '$Platform'..." -ForegroundColor Cyan

$ErrorActionPreference = "Stop"

$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vsWhere -latest -all -prerelease -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1

if (-not $msbuild) {
    throw "MSBuild not found."
}

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$solution = Join-Path $root "src\Unison.sln"
if (-not (Test-Path $solution)) {
    throw "Solution not found: $solution"
}

$packageRoot = Join-Path $root "src\Unison.Uwp\AppPackages"

New-Item -ItemType Directory -Path "logs" -Force | Out-Null

& $msbuild $solution /t:Clean /p:Configuration=$Configuration /p:Platform=$Platform /v:q /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $msbuild $solution /t:Restore /p:Configuration=$Configuration /p:Platform=$Platform /v:q /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $msbuild $solution /t:Build /p:Configuration=$Configuration /p:Platform=$Platform /v:m /nologo "/fl" "/flp:logfile=logs\build.log;verbosity=normal"
$code = $LASTEXITCODE

if ($code -eq 0) {
    Get-ChildItem $packageRoot -Recurse -Include "*.appx","*.appxbundle","*.msix","*.msixbundle" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
}

exit $code

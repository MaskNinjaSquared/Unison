@echo off
REM Full build and sign script for Unison.UWPApp
REM This script builds and signs the package with the development certificate
REM Certificate: CN=UniDev, Password: unison

setlocal

set CONFIG=%1
set PLATFORM=%2

if "%CONFIG%"=="" set CONFIG=Debug
if "%PLATFORM%"=="" set PLATFORM=ARM

set PFX_FILE=%~dp0Unison.UWPApp\Unison.UWPApp_TemporaryKey.pfx
set PFX_PASSWORD=unison
set CERT_THUMBPRINT=81DE7C2F354D114196E8D2238E4064E4C5B3E237

echo ============================================
echo Building and Signing Unison.UWPApp
echo Configuration: %CONFIG%
echo Platform: %PLATFORM%
echo Certificate: CN=UniDev
echo Started: %DATE% %TIME%
echo ============================================

call "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\Common7\Tools\VsDevCmd.bat"

cd /d "%~dp0"

echo.
echo Step 1: Clean...
msbuild Unison.UWPApp.sln /t:Clean /p:Configuration=%CONFIG% /p:Platform=%PLATFORM% /v:q /nologo

echo.
echo Step 2: Restore packages...
msbuild Unison.UWPApp.sln /t:Restore /p:Configuration=%CONFIG% /p:Platform=%PLATFORM% /v:q /nologo

echo.
echo Step 3: Build and Package with Signing...
msbuild Unison.UWPApp.sln /t:Build /p:Configuration=%CONFIG% /p:Platform=%PLATFORM% /p:AppxPackageSigningEnabled=true /p:PackageCertificateKeyFile="%PFX_FILE%" /p:PackageCertificatePassword=%PFX_PASSWORD% /p:PackageCertificateThumbprint=%CERT_THUMBPRINT% /v:m /nologo /fl /flp:logfile=logs\build.log;verbosity=normal

set BUILD_RESULT=%ERRORLEVEL%

echo.
echo Finished: %DATE% %TIME%

if %BUILD_RESULT%==0 (
    echo ============================================
    echo BUILD AND SIGN SUCCEEDED
    echo ============================================
    echo.
    echo Signed AppX location:
    dir /b /s Unison.UWPApp\AppPackages\*.appx 2>nul
    echo.
    echo Certificate subject: CN=UniDev
    echo Install the certificate on target device to sideload the app.
) else (
    echo ============================================
    echo BUILD FAILED - Error code: %BUILD_RESULT%
    echo Check logs\build.log for details
    echo ============================================
)

endlocal
exit /b %BUILD_RESULT%

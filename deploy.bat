@echo off
REM Deploy script for Unison.UWPApp to Windows 10 Mobile
REM Usage: deploy.bat [IP_ADDRESS] [Platform]
REM Defaults: 127.0.0.1 ARM

setlocal

set IP=%1
set PLATFORM=%2

if "%IP%"=="" set IP=127.0.0.1
if "%PLATFORM%"=="" set PLATFORM=ARM

set WINAPPDEPLOYCMD="C:\Program Files (x86)\Windows Kits\10\bin\10.0.15254.0\x86\WinAppDeployCmd.exe"
set APPX_PATH=Unison.UWPApp\AppPackages\Unison.UWPApp_1.0.0.0_%PLATFORM%_Debug_Test\Unison.UWPApp_1.0.0.0_%PLATFORM%_Debug.appx

echo ============================================
echo Deploying Unison.UWPApp
echo Target IP: %IP%
echo Platform: %PLATFORM%
echo ============================================

if not exist %WINAPPDEPLOYCMD% (
    echo ERROR: WinAppDeployCmd.exe not found at expected path
    echo Expected: %WINAPPDEPLOYCMD%
    exit /b 1
)

echo.
echo Listing connected devices...
%WINAPPDEPLOYCMD% devices

echo.
echo Installing AppX package...
%WINAPPDEPLOYCMD% install -file "%~dp0%APPX_PATH%" -ip %IP%

if %ERRORLEVEL%==0 (
    echo.
    echo ============================================
    echo DEPLOY SUCCEEDED
    echo ============================================
) else (
    echo.
    echo ============================================
    echo DEPLOY FAILED - Error code: %ERRORLEVEL%
    echo ============================================
)

endlocal

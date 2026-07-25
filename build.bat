@echo off
REM Full build and package script for Unison.UWPApp
REM This script runs to completion before returning

setlocal

set CONFIG=%1
set PLATFORM=%2

if "%CONFIG%"=="" set CONFIG=Debug
if "%PLATFORM%"=="" set PLATFORM=ARM

echo ============================================
echo Building Unison.UWPApp (Full Package)
echo Configuration: %CONFIG%
echo Platform: %PLATFORM%
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
echo Step 3: Build and Package...
msbuild Unison.UWPApp.sln /t:Build /p:Configuration=%CONFIG% /p:Platform=%PLATFORM% /v:m /nologo /fl /flp:logfile=logs\build.log;verbosity=normal

set BUILD_RESULT=%ERRORLEVEL%

echo.
echo Finished: %DATE% %TIME%

if %BUILD_RESULT%==0 (
    echo ============================================
    echo BUILD SUCCEEDED
    echo ============================================
    echo.
    echo AppX location:
    dir /b /s Unison.UWPApp\AppPackages\*.appx 2>nul
) else (
    echo ============================================
    echo BUILD FAILED - Error code: %BUILD_RESULT%
    echo Check logs\build.log for details
    echo ============================================
)

endlocal
exit /b %BUILD_RESULT%

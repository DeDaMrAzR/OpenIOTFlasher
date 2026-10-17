@echo off
:: Build script for OpenIOT flasher
:: Found MSBuild path: C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe

set MSBUILD_PATH="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

if not exist %MSBUILD_PATH% (
    echo [ERROR] MSBuild.exe not found at %MSBUILD_PATH%
    echo Please ensure Visual Studio 2022 is installed.
    exit /b 1
)

:: Set version from argument or default to 1.0.0
set FLASHERVERSION=1.0.0
if not "%~1"=="" set FLASHERVERSION=%~1

echo [INFO] Building OpenIOT flasher version %FLASHERVERSION%...
echo [INFO] Target: Release (Any CPU)

%MSBUILD_PATH% BK7231Flasher.sln /p:Configuration=Release /p:Platform="Any CPU" /p:FLASHERVERSION=%FLASHERVERSION% /t:Restore;Build

if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] Build completed successfully!
    echo [INFO] Executable path: BK7231Flasher\bin\Release\OpenIOT_flasher.exe
) else (
    echo.
    echo [ERROR] Build failed with error code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

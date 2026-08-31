@echo off
setlocal
cd /d "%~dp0"

REM  Starts the app through the Microsoft-signed "dotnet" host.
REM
REM  WHY: this app is a hobby project and is not code-signed. On a machine with
REM  Windows Smart App Control enabled, an unsigned .exe is refused outright and
REM  cannot be launched at all. Running the managed .dll inside the signed
REM  dotnet host avoids that entirely - Windows is validating Microsoft's host,
REM  not our unsigned binary.
REM
REM  Requires the .NET 10 Desktop Runtime (x64).

where dotnet >nul 2>&1
if errorlevel 1 goto :nodotnet

REM Confirm a Windows Desktop runtime is actually installed (the ASP.NET or
REM console-only runtime is not enough for a WinForms app).
dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.WindowsDesktop.App 10." >nul
if errorlevel 1 goto :nodesktop

start "AI Asset Provenance Helper" dotnet "%~dp0AssetProvenanceHelper.dll" %*
exit /b 0

:nodotnet
echo.
echo   The .NET 10 Desktop Runtime is required, but "dotnet" was not found.
echo.
echo   Install the .NET Desktop Runtime 10 (x64) from:
echo     https://dotnet.microsoft.com/download/dotnet/10.0
echo.
echo   Then run this file again.
echo.
pause
exit /b 1

:nodesktop
echo.
echo   .NET was found, but the .NET 10 *Desktop* Runtime is missing.
echo   This app needs the Desktop Runtime (it includes Windows Forms).
echo.
echo   Install ".NET Desktop Runtime 10.x (x64)" from:
echo     https://dotnet.microsoft.com/download/dotnet/10.0
echo.
echo   Installed runtimes:
dotnet --list-runtimes
echo.
pause
exit /b 1

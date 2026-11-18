@echo off
setlocal

set "bridgeAddinDll=%~1"
if not defined bridgeAddinDll for %%I in ("%~dp0..\..\CKBusbarBridge\bin\Debug\CKBusbarBridge.dll") do set "bridgeAddinDll=%%~fI"

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0Register-Addin.ps1" -AssemblyPath "%bridgeAddinDll%"
set "bridgeRegistrationResult=%errorlevel%"
if /I not "%~2"=="--no-pause" pause
exit /b %bridgeRegistrationResult%

@echo off
setlocal

for %%I in ("%~dp0..\..\CKBusbarBridge\bin\Debug\CKBusbarBridge.dll") do set "addinDll=%%~fI"
set "regAsm=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
set "addinStartup=HKCU\Software\SolidWorks\AddInsStartup\{46af9620-9777-42ed-9df4-25a2ac4f860d}"

if not exist "%addinDll%" goto missingDll
if not exist "%regAsm%" goto missingRegAsm
"%SystemRoot%\System32\tasklist.exe" /FI "IMAGENAME eq SLDWORKS.exe" /NH | "%SystemRoot%\System32\find.exe" /I "SLDWORKS.exe" >nul
if not errorlevel 1 goto solidWorksRunning

"%regAsm%" "%addinDll%" /codebase /nologo
if errorlevel 1 goto registrationFailed

rem The COM registration callback sets startup off; turn it on for VS F5.
"%SystemRoot%\System32\reg.exe" add "%addinStartup%" /ve /t REG_DWORD /d 1 /f /reg:64 >nul
if errorlevel 1 goto startupFailed

echo Registered Debug plugin: "%addinDll%"
echo Press F5 in Visual Studio to start SolidWorks debugging.
pause
exit /b 0

:missingDll
echo Debug DLL not found: "%addinDll%"
echo Build CKBusbarBridge in Debug in Visual Studio first.
goto failed

:missingRegAsm
echo 64-bit .NET Framework RegAsm not found: "%regAsm%"
goto failed

:solidWorksRunning
echo SolidWorks is running. Save your work and close it before registration.
goto failed

:registrationFailed
echo Registration failed. Run this script as administrator under the same Windows account.
goto failed

:startupFailed
echo COM registration succeeded, but automatic loading could not be enabled.

:failed
pause
exit /b 1

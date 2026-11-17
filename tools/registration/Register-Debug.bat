@echo off
setlocal

for %%I in ("%~dp0..\..\CKBusbarBridge\bin\Debug\CKBusbarBridge.dll") do set "addinDll=%%~fI"
set "regAsm=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
set "regTool=%SystemRoot%\System32\reg.exe"
set "addinId={46af9620-9777-42ed-9df4-25a2ac4f860d}"
set "addinComKey=SOFTWARE\Classes\CLSID\%addinId%\InprocServer32"
set "addinStartup=HKCU\Software\SolidWorks\AddInsStartup\%addinId%"

if not exist "%addinDll%" goto missingDll
if not exist "%regAsm%" goto missingRegAsm
"%SystemRoot%\System32\tasklist.exe" /FI "IMAGENAME eq SLDWORKS.exe" /NH | "%SystemRoot%\System32\find.exe" /I "SLDWORKS.exe" >nul
if not errorlevel 1 goto solidWorksRunning

"%regTool%" query "HKLM\%addinComKey%" /reg:64 >nul 2>nul
if errorlevel 1 goto register

rem Use the old assembly when it still exists; RegAsm must unregister its version.
set "oldDll=%addinDll%"
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -NonInteractive -Command "$key = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine','Registry64').OpenSubKey($env:addinComKey); if ($key) { $codeBase=$key.GetValue('CodeBase'); if ($codeBase) { $uri=[uri]$codeBase; if ($uri.IsFile) { $uri.LocalPath } } }"`) do if exist "%%I" set "oldDll=%%I"
echo Unregistering previous plugin: "%oldDll%"
"%regAsm%" "%oldDll%" /unregister /nologo
if errorlevel 1 goto unregistrationFailed
"%regTool%" query "HKLM\%addinComKey%" /reg:64 >nul 2>nul
if not errorlevel 1 goto unregistrationIncomplete

:register
"%regAsm%" "%addinDll%" /codebase /nologo
if errorlevel 1 goto registrationFailed

"%regTool%" add "%addinStartup%" /ve /t REG_DWORD /d 1 /f /reg:64 >nul
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

:unregistrationFailed
echo Could not unregister the previous plugin. Run as administrator and check the error above.
goto failed

:unregistrationIncomplete
echo The previous COM registration still exists. Unregister its original DLL before continuing.
goto failed

:registrationFailed
echo Registration failed. Run this script as administrator under the same Windows account.
goto failed

:startupFailed
echo COM registration succeeded, but automatic loading could not be enabled.

:failed
pause
exit /b 1

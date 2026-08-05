@echo off
setlocal

rem Launches the app the way the README's rehearsal path describes: built without the
rem elevation manifest and pointed at a *copy* of the hosts file. No UAC prompt, and it
rem cannot touch the real C:\Windows\System32\drivers\etc\hosts.
rem
rem   run.bat          rehearsal build against a sandbox copy (default)
rem   run.bat --real   Release build against the actual hosts file, elevated
rem
rem The app is single-instance on a fixed mutex, so an already-running copy (installed or
rem otherwise) will just surface itself instead of starting this build. Exit that one first.

set "ROOT=%~dp0"
set "SANDBOX=%LOCALAPPDATA%\HostsManager\rehearsal"

if /i "%~1"=="--real" goto real

echo Building rehearsal exe (no elevation manifest)...
dotnet build "%ROOT%src\HostsManager\HostsManager.csproj" -p:NoElevation=true -o "%ROOT%build" -v q --nologo
if errorlevel 1 exit /b 1

if not exist "%SANDBOX%" mkdir "%SANDBOX%"
if not exist "%SANDBOX%\hosts" copy "%SystemRoot%\System32\drivers\etc\hosts" "%SANDBOX%\hosts" >nul

echo Hosts file: %SANDBOX%\hosts
start "" "%ROOT%build\HostsManager.exe" --hosts-path "%SANDBOX%\hosts" --backups-dir "%SANDBOX%\backups"
goto :eof

:real
echo Building Release...
dotnet build "%ROOT%src\HostsManager\HostsManager.csproj" -c Release -v q --nologo
if errorlevel 1 exit /b 1

echo Hosts file: the real one. Windows will prompt for elevation.
start "" "%ROOT%src\HostsManager\bin\Release\net8.0-windows\HostsManager.exe"

@echo off
setlocal

rem Cuts a GitHub release: bumps the version, runs the tests, builds all six artifacts,
rem tags, pushes, and publishes. See installer/release.ps1 for what runs when and what
rem is still reversible at each point.
rem
rem   release.bat 1.0.5
rem   release.bat 1.0.5 -Draft
rem   release.bat 1.0.5 -NotesFile notes.md
rem   release.bat 1.0.5 -SkipTests
rem
rem Needs the WiX 5 CLI (see the README) and an authenticated gh.

rem Captured before the first shift: shift moves %0 too, so %~dp0 stops naming this file.
set "HERE=%~dp0"

set "VERSION=%~1"
if "%VERSION%"=="" goto usage

shift

rem shift doesn't rewrite %*, so the remaining switches are collected by hand and passed
rem straight through to the PowerShell script.
set "REST="
:collect
if "%~1"=="" goto run
set "REST=%REST% %1"
shift
goto collect

:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%installer\release.ps1" -Version %VERSION%%REST%
exit /b %errorlevel%

:usage
echo Usage: release.bat ^<version^> [-Draft] [-NotesFile ^<file^>] [-SkipTests]
echo.
echo   version   three numeric parts, e.g. 1.0.5
echo.
echo Example: release.bat 1.0.5 -NotesFile notes.md -Draft
exit /b 1

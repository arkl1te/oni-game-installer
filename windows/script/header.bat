@echo off
rem Installs Oni from your own disc, without running the disc's Setup, then optionally the
rem Anniversary Edition and Arklite, which it downloads.
rem Everything below "exit /b" is the installer's source code (C#), which is plain text you can
rem read. Windows' own PowerShell compiles it when you run this file.
setlocal
set "ONI_BAT=%~f0"
set "ONI_A1=%~1"
set "ONI_A2=%~2"
set "ONI_A3=%~3"
set "ONI_A4=%~4"
set "ONI_A5=%~5"
set "ONI_A6=%~6"
set "ONI_A7=%~7"
set "ONI_A8=%~8"
set "ONI_SILENT="
if /i "%~1"=="/S" set "ONI_SILENT=1"
if not defined ONI_SILENT echo Starting the Oni installer...
set "ONI_CWD=%CD%"
cd /d "%TEMP%"
set "ONI_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%ONI_PS%" set "ONI_PS=powershell.exe"
"%ONI_PS%" -NoProfile -STA -Command "$ErrorActionPreference='Stop';$s=[IO.File]::ReadAllText($env:ONI_BAT);$s=$s.Substring($s.IndexOf('//ONI'+'-CS'));Add-Type -TypeDefinition $s;exit [OniInstaller]::Run()"
set "ONI_CODE=%errorlevel%"
rem 77: uninstalled. This file can only delete itself as its very last action.
if "%ONI_CODE%"=="77" (goto) 2>nul & del "%~f0" & rd "%~dp0." 2>nul
if "%ONI_CODE%"=="0" exit /b 0
if defined ONI_SILENT exit /b %ONI_CODE%
echo.
echo The installer stopped with an error (code %ONI_CODE%).
pause
exit /b %ONI_CODE%

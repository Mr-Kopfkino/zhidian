@echo off
setlocal
title Zhidian - Verify and Extract
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Extract-Zhidian.ps1" %*
set "extractResult=%ERRORLEVEL%"
echo.
if not "%extractResult%"=="0" echo Extraction failed. Read the error above before trying again.
pause
exit /b %extractResult%

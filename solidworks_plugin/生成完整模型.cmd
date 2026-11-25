@echo off
cd /d "%~dp0"
title Reducer SW - Full v4 Build

if not exist "%~dp0parameters_v4.json" (
    echo.
    echo Missing parameters_v4.json
    echo Generate it first:  py -3.12 export_all.py
    echo.
    pause
    exit /b 1
)

echo SolidWorks must already be running.
echo Generating from parameters_v4.json ...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -ParameterFile "%~dp0parameters_v4.json"

echo.
if errorlevel 1 (
    echo FAILED. Open the newest folder under output\ and read generation.log
) else (
    echo DONE. Check the newest folder under output\
)
echo.
pause

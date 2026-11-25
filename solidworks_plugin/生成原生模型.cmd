@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -ParameterFile "%~dp0parameters_v3.json"
pause

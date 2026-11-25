@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Editor -ParameterFile "%~dp0parameters_two_stage.json"
pause

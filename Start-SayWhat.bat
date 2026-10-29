@echo off
setlocal
set "SAYWHAT_EXE=%~dp0artifacts\app\SayWhat.exe"
set "SAYWHAT_DATA_ROOT=%~dp0artifacts\dev-data"
if not exist "%SAYWHAT_EXE%" (
    echo Build the source first: powershell -File eng\Build.ps1
    echo Then run this launcher again. No translation services were started.
    pause
    exit /b 1
)
start "" "%SAYWHAT_EXE%"
if errorlevel 1 (
    echo SayWhat? could not open. No new translation services were started.
    pause
    exit /b 1
)
exit /b 0

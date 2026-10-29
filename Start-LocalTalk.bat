@echo off
rem Compatibility entry point for shortcuts created before the SayWhat? rename.
call "%~dp0Start-SayWhat.bat"
exit /b %errorlevel%

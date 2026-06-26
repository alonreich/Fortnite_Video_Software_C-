@echo off
setlocal
cd /d "%~dp0"
call "%~dp0Build.cmd" %*
exit /b %ERRORLEVEL%

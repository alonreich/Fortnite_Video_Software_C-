@echo off
rem SIGNLOCAL_01 - stop trusting the FVS local development root on this Windows account.
certutil -user -delstore Root "FVS Local Development Root CA"
exit /b %ERRORLEVEL%

@echo off
rem SIGNLOCAL_01 - trust the FVS local development root on THIS Windows account only.
rem Windows shows a security confirmation; answer Yes. No administrator rights needed.
certutil -user -addstore Root "%~dp0fvs-dev-root-ca.cer"
exit /b %ERRORLEVEL%

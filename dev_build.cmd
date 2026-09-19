@echo off
rem Lightweight entrypoint: local dev compile only - no git tags, no GitHub release,
rem and .\compiled is not wiped first. The pipeline lives in build\FvsBuild.
dotnet run --project "%~dp0build\FvsBuild\FvsBuild.csproj" -c Release -- --dev %*
exit /b %ERRORLEVEL%

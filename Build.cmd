@echo off
rem Lightweight entrypoint: the whole pipeline (staging, payload.zip, NativeAOT publish,
rem signing, single-file validation, optional GitHub release) lives in build\FvsBuild.
rem Usage: Build.cmd [--no-publish]     Exit codes: 0=OK  1=build failed  2=release not updated
dotnet run --project "%~dp0build\FvsBuild\FvsBuild.csproj" -c Release -- %*
exit /b %ERRORLEVEL%

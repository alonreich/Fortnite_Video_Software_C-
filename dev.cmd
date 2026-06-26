@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "PROJECT=src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj"
set "CONFIG=Debug"
set "RUNTIME=win-x64"
set "DOTNET_WATCH_SUPPRESS_EMOJIS=1"

if "%~1"=="" goto WATCH
if /I "%~1"=="run" goto RUN
if /I "%~1"=="build" goto BUILD
if /I "%~1"=="restore" goto RESTORE
if /I "%~1"=="clean" goto CLEAN

echo Unknown option: %1
echo.
echo Usage:
echo   dev          Hot reload mode. App stays open, UI updates on save.
echo   dev run      Single Debug launch.
echo   dev build    Build only, no run.
echo   dev restore  Restore NuGet packages after project/package changes.
echo   dev clean    Clean Debug output.
goto :EOF

:WATCH
echo Starting HOT RELOAD watch mode...
echo Edit any axaml or cs file and SAVE to see live UI updates.
echo Press Ctrl+C to stop.
echo.
dotnet watch run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% --no-self-contained --no-restore -- run-ui
goto :EOF

:RUN
echo Running Debug single launch...
dotnet run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% --no-self-contained --no-restore -- run-ui
goto :EOF

:BUILD
echo Building Debug no run...
dotnet build "%PROJECT%" -c %CONFIG% -r %RUNTIME% --no-self-contained --no-restore -consoleLoggerParameters:Summary
goto :EOF

:RESTORE
echo Restoring Debug dependencies...
dotnet restore "%PROJECT%" -r %RUNTIME%
goto :EOF

:CLEAN
echo Cleaning Debug output...
dotnet clean "%PROJECT%" -c %CONFIG% -r %RUNTIME% --no-self-contained -consoleLoggerParameters:Summary
goto :EOF

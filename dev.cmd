@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "PROJECT=src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj"
set "CONFIG=Debug"
set "RUNTIME=win-x64"
set "DOTNET_WATCH_SUPPRESS_EMOJIS=1"

REM ──────────────────────────────────────────────────────────────────────
REM AGGRESSIVE PROCESS CLEANUP: Ensure no previous dev session is locking bin/obj
REM ──────────────────────────────────────────────────────────────────────
taskkill /F /IM FortniteVideoSoftware.exe /T 2>nul
taskkill /F /IM FortniteVideoSoftware.App.exe /T 2>nul
dotnet build-server shutdown 2>nul

REM Sandbox the developer config to prevent corrupting the real installed app settings.
set "FVS_PROGRAMDATA_ROOT=%TMP%\Fortnite_Video_Software_DEV\.dev_data"

REM ──────────────────────────────────────────────────────────────────────
REM DEV LOG DIRECTORY: All dev-mode logs go EXCLUSIVELY to
REM %TMP%\Fortnite_Video_Software_DEV\. Never in the project root, %TMP%,
REM or %PROGRAMDATA%. This includes the app log and detailed MPV debug logs.
REM ──────────────────────────────────────────────────────────────────────
set "FVS_DEV_LOG_DIR=%TMP%\Fortnite_Video_Software_DEV"
if not exist "%FVS_DEV_LOG_DIR%" mkdir "%FVS_DEV_LOG_DIR%"

REM ──────────────────────────────────────────────────────────────────────
REM DEV CLEAN-SLATE: Wipe the sandboxed config/state on every dev run so
REM the app boots as if freshly installed (OOB). This deletes session_state.json,
REM recovery sentinels, logs, and any cached state under .dev_data.
REM Also clears previous dev logs for a clean debugging session.
REM ──────────────────────────────────────────────────────────────────────
call :WIPE_DEV_CONFIG

if "%~1"=="" goto WATCH
if /I "%~1"=="run" goto RUN
if /I "%~1"=="build" goto BUILD
if /I "%~1"=="restore" goto RESTORE
if /I "%~1"=="clean" goto CLEAN

echo Unknown option: %1
echo.
echo Usage:
echo   dev          Hot reload mode. App stays open, UI updates on save. (Performs clean first)
echo   dev run      Single Debug launch. (Incremental, fast)
echo   dev build    Build only, no run. (Incremental, fast)
echo   dev restore  Restore NuGet packages after project/package changes.
echo   dev clean    Clean Debug output.
goto :EOF

:WATCH
echo Cleaning project to ensure watch mode doesn't get stuck...
call :NUKE_BUILD
dotnet clean "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary >nul

echo Starting HOT RELOAD watch mode...
echo Edit any axaml or cs file and SAVE to see live UI updates.
echo Press Ctrl+C to stop.
echo.
dotnet watch run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% -- run-ui
goto :EOF

:RUN
echo Running Debug single launch (full clean)...
call :NUKE_BUILD
dotnet run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% -- run-ui
goto :EOF

:BUILD
echo Building Debug no run (full clean)...
call :NUKE_BUILD
dotnet build "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary
goto :EOF

:RESTORE
echo Restoring Debug dependencies...
dotnet restore "%PROJECT%" -r %RUNTIME%
goto :EOF

:CLEAN
echo Cleaning Debug output...
if exist "src\FortniteVideoSoftware.App\bin" rd /s /q "src\FortniteVideoSoftware.App\bin"
if exist "src\FortniteVideoSoftware.App\obj" rd /s /q "src\FortniteVideoSoftware.App\obj"
dotnet clean "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary
goto :EOF

REM ═══════════════════════════════════════════════════════════════════════
REM Subroutine: Wipe all sandboxed dev config/state for a fresh-install feel.
REM Deletes .dev_data\ entirely so the app re-creates defaults on next boot.
REM ═══════════════════════════════════════════════════════════════════════
:WIPE_DEV_CONFIG
if exist "%TMP%\Fortnite_Video_Software_DEV\.dev_data" (
    echo [DEV] Wiping sandboxed config %TMP%\Fortnite_Video_Software_DEV\.dev_data for clean-slate boot...
    rd /s /q "%TMP%\Fortnite_Video_Software_DEV\.dev_data" 2>nul
)
REM Clear previous dev logs for a fresh debugging session
if exist "%FVS_DEV_LOG_DIR%" (
    echo [DEV] Clearing previous dev logs in %FVS_DEV_LOG_DIR%...
    del /q "%FVS_DEV_LOG_DIR%\*" 2>nul
)
goto :EOF

REM ═══════════════════════════════════════════════════════════════════════
REM Subroutine: Nuke ALL build caches so no stale binary or XAML is ever shown.
REM Shuts the persistent Roslyn build server and deletes bin/ + obj/ for every
REM project. Guarantees the very next build is fully from source.
REM ═══════════════════════════════════════════════════════════════════════
:NUKE_BUILD
echo [DEV] Wiping build caches (bin/obj + build server) for a guaranteed-fresh build...
dotnet build-server shutdown >nul 2>nul
if exist "src\FortniteVideoSoftware.App\bin"  rd /s /q "src\FortniteVideoSoftware.App\bin"
if exist "src\FortniteVideoSoftware.App\obj"  rd /s /q "src\FortniteVideoSoftware.App\obj"
if exist "src\FortniteVideoSoftware.Core\bin" rd /s /q "src\FortniteVideoSoftware.Core\bin"
if exist "src\FortniteVideoSoftware.Core\obj" rd /s /q "src\FortniteVideoSoftware.Core\obj"
goto :EOF
@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "PROJECT=src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj"
set "CONFIG=Debug"
set "RUNTIME=win-x64"
set "DOTNET_WATCH_SUPPRESS_EMOJIS=1"
set "REPO_ROOT=%CD%"

REM Sandbox the developer config to prevent corrupting the real installed app settings.
set "FVS_PROGRAMDATA_ROOT=%TMP%\Fortnite_Video_Software_DEV\.dev_data"

REM ----------------------------------------------------------------------
REM DEV LOG DIRECTORY: All dev-mode logs go EXCLUSIVELY to
REM %TMP%\Fortnite_Video_Software_DEV\. Never in the project root, %TMP%,
REM or %PROGRAMDATA%. This includes the app log and detailed MPV debug logs.
REM ----------------------------------------------------------------------
set "FVS_DEV_LOG_DIR=%TMP%\Fortnite_Video_Software_DEV"
if not exist "%FVS_DEV_LOG_DIR%" mkdir "%FVS_DEV_LOG_DIR%"

REM ======================================================================
REM STALE-STATE PURGE. Runs before EVERY mode, no exceptions.
REM ======================================================================
call :KILL_STALE
call :WIPE_DEV_CONFIG
call :VERIFY_PATCHES
REM VERIFYHALT_01 - `exit /b` inside a CALLed subroutine returns from the SUBROUTINE, not
REM from the script. Without this line the halt above would set an errorlevel nobody reads
REM and the build would carry straight on - the same class of bug as the unread MISSING
REM variable it was written to fix.
if errorlevel 1 exit /b 1

if "%~1"=="" goto WATCH
if /I "%~1"=="run" goto RUN
if /I "%~1"=="build" goto BUILD
if /I "%~1"=="restore" goto RESTORE
if /I "%~1"=="clean" goto CLEAN
if /I "%~1"=="fresh" goto FRESH
if /I "%~1"=="trace" goto TRACE

echo Unknown option: %1
echo.
echo Usage:
echo   dev          Hot reload mode. App stays open, UI updates on save. (Performs clean first)
echo   dev run      Single Debug launch. (Incremental, fast)
echo   dev build    Build only, no run. (Incremental, fast)
echo   dev fresh    Like 'dev', but ALSO wipes the sandboxed config/state (.dev_data)
echo                so the app boots as if freshly installed.
echo   dev restore  Restore NuGet packages after project/package changes.
echo   dev clean    Clean Debug output.
echo   dev trace    Like 'dev', but writes the log to .devlogs\ INSIDE the repo so
echo                it can be read and shared. Diagnostics only (SYS-DEVBUILD).
goto :EOF

REM ======================================================================
REM TRACE MODE (TRANSPORT_TRACE_01 / SYS-DEVBUILD).
REM
REM Same as the default watch mode, except the log is written INSIDE the repo
REM at .devlogs\ instead of %TMP%. The rule that dev logs never land in the
REM project tree exists so a normal run cannot litter it and so a log can never
REM be committed; this mode is opt-in, announces itself, and .devlogs\ is
REM gitignored, so neither risk applies. It exists because a log nobody can
REM reach is a log nobody can read when a fault needs diagnosing off-machine.
REM ======================================================================
:TRACE
set "FVS_DEV_LOG_DIR=%REPO_ROOT%\.devlogs"
if not exist "%FVS_DEV_LOG_DIR%" mkdir "%FVS_DEV_LOG_DIR%"
del /q "%FVS_DEV_LOG_DIR%\*.log" 2>nul
echo [DEV] TRACE MODE: log goes to %FVS_DEV_LOG_DIR%
goto WATCH

:FRESH
call :WIPE_DEV_DATA
goto WATCH

:WATCH
echo Cleaning project to ensure watch mode doesn't get stuck...
call :NUKE_BUILD
dotnet clean "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary >nul

echo Starting HOT RELOAD watch mode...
echo   NOTE: hot reload cannot apply structural edits (new fields, new methods,
echo         changed signatures). If a change does not appear, STOP and re-run dev.cmd.
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
call :NUKE_BUILD
dotnet clean "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary
goto :EOF

REM ======================================================================
REM Subroutine: VERIFY_PATCHES - SYS-VERIFYTOOL.
REM
REM THIS SUBROUTINE NO LONGER PARSES ANYTHING. The sentinel list lives in
REM build\sentinels.txt and the checker is build\FvsVerify, a C# console
REM app with its own tests in tests\FvsVerify.Tests. dev.cmd's entire job
REM is to run it and read the exit code.
REM
REM WHY IT MOVED: as a batch FOR list this check broke three times and
REM every failure was silent.
REM   VERIFYLOOP_01  - `call :CHECK_TAG` seeked labels by BYTE OFFSET
REM                    through a mixed-EOL file; two seeks landed badly, so
REM                    2 of 49 sentinels were skipped and the run said OK.
REM   LISTCOMMENT_01 - REM is not a comment inside a FOR list. Each
REM                    annotation became six or seven bogus sentinels.
REM   BATCHPARENS_01 - one round bracket in an annotation, even inside a
REM                    REM, closed the list early and ran the next word.
REM   VERIFYHALT_01  - the MISSING variable was assigned and never read, so
REM                    for its whole existence this check could not fail.
REM
REM None of those are bugs in the sentinel idea. They are what a list of
REM 164 strings, a comment syntax and a file search cost in a language with
REM no list type, no comments inside a list, no escaping - and, decisively,
REM no way to write a test against the result. Invariant #8 says every rule
REM that can be a test is a test; this one now is, twice over.
REM
REM To add a sentinel: add one TAG=path line to build\sentinels.txt.
REM Nothing in this file changes, ever again.
REM ======================================================================
:VERIFY_PATCHES
dotnet run --project "%REPO_ROOT%\build\FvsVerify\FvsVerify.csproj" -v q -- "%REPO_ROOT%"
if errorlevel 1 exit /b 1
goto :EOF

REM ======================================================================
REM Subroutine: KILL EVERY STALE PROCESS THIS REPO OWNS.
REM
REM Name-based taskkill only catches what we can name. The PowerShell pass
REM below is the real guard: it kills ANY process whose executable lives
REM under this repository, which covers the app, its companion windows
REM (Merger, Crop Tools), and every backend/frontend child it spawns
REM (mpv.exe, ffmpeg.exe, ffprobe.exe) no matter how it was orphaned.
REM An orphaned mpv keeps a libmpv IPC pipe and a D3D device alive; an
REM orphaned app keeps a file lock on bin\, which is what makes the next
REM build silently reuse a stale binary.
REM ======================================================================
:KILL_STALE
echo [DEV] Purging stale processes owned by this repo...
taskkill /F /IM FortniteVideoSoftware.exe /T >nul 2>nul
taskkill /F /IM FortniteVideoSoftware.App.exe /T >nul 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$root = '%REPO_ROOT%';" ^
  "Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |" ^
  "  Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLower().StartsWith($root.ToLower()) } |" ^
  "  ForEach-Object {" ^
  "    Write-Host ('[DEV]   killing ' + $_.Name + ' (PID ' + $_.ProcessId + ')');" ^
  "    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" 2>nul

REM Roslyn/MSBuild servers hold obj\ handles and cache analyzer state between builds.
dotnet build-server shutdown >nul 2>nul
taskkill /F /IM VBCSCompiler.exe >nul 2>nul
taskkill /F /IM MSBuild.exe >nul 2>nul

REM Give Windows a moment to release the file handles before bin/obj are deleted.
ping -n 2 127.0.0.1 >nul 2>nul
goto :EOF

REM ======================================================================
REM Subroutine: clear the previous session's logs.
REM ======================================================================
:WIPE_DEV_CONFIG
if exist "%FVS_DEV_LOG_DIR%" (
    echo [DEV] Clearing previous dev logs in %FVS_DEV_LOG_DIR%...
    del /q "%FVS_DEV_LOG_DIR%\*.log" 2>nul
    del /q "%FVS_DEV_LOG_DIR%\*.crashdigest" 2>nul
    del /q "%FVS_DEV_LOG_DIR%\*.png" 2>nul
)
goto :EOF

REM ======================================================================
REM Subroutine: wipe the sandboxed config/state (OPT-IN via 'dev fresh').
REM Deletes .dev_data entirely so the app re-creates defaults on next boot:
REM session_state.json, recovery sentinels, window bounds, settings.
REM NOT part of the default run - a plain 'dev.cmd' keeps your settings.
REM ======================================================================
:WIPE_DEV_DATA
if exist "%FVS_PROGRAMDATA_ROOT%" (
    echo [DEV] Wiping sandboxed config %FVS_PROGRAMDATA_ROOT% for clean-slate boot...
    rd /s /q "%FVS_PROGRAMDATA_ROOT%" 2>nul
)
goto :EOF

REM ======================================================================
REM Subroutine: Nuke ALL build caches so no stale binary or XAML is ever shown.
REM Walks EVERY project under src\ and tests\ instead of naming two of them,
REM so a project added later cannot quietly keep serving a stale assembly.
REM ======================================================================
:NUKE_BUILD
echo [DEV] Wiping build caches (bin/obj for every project) for a guaranteed-fresh build...
dotnet build-server shutdown >nul 2>nul
for /d /r "%REPO_ROOT%\src" %%D in (bin obj) do if exist "%%D" rd /s /q "%%D" 2>nul
if exist "%REPO_ROOT%\tests" for /d /r "%REPO_ROOT%\tests" %%D in (bin obj) do if exist "%%D" rd /s /q "%%D" 2>nul
goto :EOF

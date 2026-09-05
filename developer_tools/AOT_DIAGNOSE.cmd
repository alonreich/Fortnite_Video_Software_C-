@echo off
setlocal
cd /d "%~dp0.."
rem ============================================================================
rem  AOT_DIAGNOSE.cmd
rem
rem  WHY THIS EXISTS: Build.cmd publishes with -consoleLoggerParameters:ErrorsOnly.
rem  ILC reports WHY a code-generation failure happened as an ordinary MSBuild
rem  MESSAGE, not as an error, so that filter throws away the only line that says
rem  what actually went wrong and leaves the useless wrapper:
rem      "One or more errors occurred. (Code generation failed for method ...)"
rem
rem  This run keeps every message AND compiles ILC single-threaded, which turns
rem  the parallel AggregateException into one deterministic failure with its real
rem  inner exception and stack. Slower, but it names the cause.
rem ============================================================================
echo Shutting down build servers...
dotnet build-server shutdown >nul 2>&1
echo.
echo Running NativeAOT publish, single-threaded ILC, full diagnostics.
echo This is SLOWER than a normal build - several minutes. Please let it finish.
echo Output -^> aot_diagnose.log
echo.
dotnet publish "src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj" -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true -p:IlcSingleThreaded=true -o "%TEMP%\FVS_AOT_DIAG" -v n > aot_diagnose.log 2>&1
echo.
echo ==================== LINES THAT MATTER ====================
findstr /I /N /C:"error" /C:"Exception" /C:"Code generation" /C:"ilc.exe" /C:"Unhandled" aot_diagnose.log
echo.
echo ===========================================================
echo Full log: aot_diagnose.log
echo Send me that file, or the lines above.
pause

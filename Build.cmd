@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1
rem ---------------------------------------------------------------------------
rem  Build.cmd - dual output: live console + .\build.log (ANSI colors in log).
rem  Log is deleted at the start of each run; header/footer include timestamps.
rem ---------------------------------------------------------------------------


shift
setlocal enabledelayedexpansion
cd /d "."

set "PROJECT_FILE=src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj"
set "PROJECT_EXE=FortniteVideoSoftware.App.exe"
set "OUTPUT_EXE=FortniteVideoSoftware.exe"
set "OUTPUT_DIR=.\compiled"
set "FINAL_DIR=.\obj\StandaloneTemp\NativeAot_final"
set "PUBLISH_BASE_ARGS=-p:TreatWarningsAsErrors=true"
set "PUBLISH_AOT_ARGS=-p:PublishAot=true -p:SelfContained=true"
set "DOTNET_LOG_ARGS=-consoleLoggerParameters:Summary;NoItemAndPropertyList"

echo ###########################################################
echo PURGING PREVIOUS BUILD ARTIFACTS...
echo ###########################################################
call :TERMINATE_PROCESSES
if exist "%OUTPUT_DIR%" rd /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"
call :CLEAN_ALL

echo.
echo ###########################################################
echo CHECKING NATIVE AOT TOOLCHAIN...
echo ###########################################################
call :DETECT_NATIVE_AOT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo BUILDING Fortnite Video Software: NativeAOT win-x64
echo ###########################################################
call :BUILD_NATIVE
if errorlevel 1 exit /b 1

call :VALIDATE_COMPILED_OUTPUT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo SUCCESS: Build completed successfully.
echo.
echo Native EXE: %OUTPUT_DIR%\%OUTPUT_EXE%
echo Log file:  .\build.log  (first line: OK / WARN / FAIL)
echo ###########################################################

exit /b 0

:BUILD_NATIVE
set "STAGING_DIR=.\obj\StandaloneTemp\Staging"
set "FINAL_DIR=.\obj\StandaloneTemp\NativeAot_final"

echo [NativeAOT] 1. Purging old temp folders...
if exist "%STAGING_DIR%" rd /s /q "%STAGING_DIR%"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
if exist "src\FortniteVideoSoftware.App\payload.zip" del /f /q "src\FortniteVideoSoftware.App\payload.zip"

echo [NativeAOT] 2. Publishing raw payload to staging...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 %PUBLISH_BASE_ARGS% -p:PublishAot=true -p:SelfContained=true -o "%STAGING_DIR%" %DOTNET_LOG_ARGS%
if errorlevel 1 exit /b 1

echo [NativeAOT] 2.5 Copying binaries to staging...
mkdir "%STAGING_DIR%\backend"
mkdir "%STAGING_DIR%\frontend"

copy /y ".\binaries\ffmpeg.exe" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\ffplay.exe" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\ffprobe.exe" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\av*.dll" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\sw*.dll" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\postproc*.dll" "%STAGING_DIR%\backend\" >nul

copy /y ".\binaries\libmpv-2.dll" "%STAGING_DIR%\frontend\" >nul
copy /y ".\binaries\mpv.exe" "%STAGING_DIR%\frontend\" >nul
copy /y ".\binaries\mpv.com" "%STAGING_DIR%\frontend\" >nul

echo [NativeAOT] 3. Zipping payload...
tar.exe -a -c -f "src\FortniteVideoSoftware.App\payload.zip" -C "%STAGING_DIR%" .

echo [NativeAOT] 4. Publishing standalone installer...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 %PUBLISH_BASE_ARGS% -p:PublishAot=true -p:SelfContained=true -o "%FINAL_DIR%" %DOTNET_LOG_ARGS%
if errorlevel 1 exit /b 1

echo [NativeAOT] 5. Moving final EXE to compiled folder...
if not exist "%FINAL_DIR%\%PROJECT_EXE%" (
  echo ERROR: Expected NativeAOT EXE was not produced in %FINAL_DIR%
  exit /b 1
)

move /y "%FINAL_DIR%\%PROJECT_EXE%" "%OUTPUT_DIR%\%OUTPUT_EXE%"
if errorlevel 1 exit /b 1

call :PURGE_COMPILED_EXTRAS
if errorlevel 1 exit /b 1

echo [NativeAOT] 6. Cleaning up temporary artifacts...
if exist "%STAGING_DIR%" rd /s /q "%STAGING_DIR%"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
if exist "src\FortniteVideoSoftware.App\payload.zip" del /f /q "src\FortniteVideoSoftware.App\payload.zip"

exit /b 0

:PURGE_COMPILED_EXTRAS
for %%F in ("%OUTPUT_DIR%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" (
    echo ERROR: Removing disallowed artifact from compiled: %%~nxF
    rd /s /q "%%~fF" 2>nul
    del /f /q "%%~fF" 2>nul
    exit /b 1
  )
)
exit /b 0

:VALIDATE_COMPILED_OUTPUT
set "INVALID=0"
if not exist "%OUTPUT_DIR%\%OUTPUT_EXE%" set "INVALID=1"
for %%F in ("%OUTPUT_DIR%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" set "INVALID=1"
)
if "!INVALID!"=="1" (
  echo ERROR: %OUTPUT_DIR% must contain only %OUTPUT_EXE%.
  echo Actual:
  dir /b "%OUTPUT_DIR%" 2>nul
  exit /b 1
)
echo Verified %OUTPUT_DIR% contains exactly %OUTPUT_EXE%.
exit /b 0

:DETECT_NATIVE_AOT
where link.exe >nul 2>&1
if not errorlevel 1 goto DETECT_NATIVE_AOT_OK

set "VSWHERE="
where vswhere.exe >nul 2>&1
if not errorlevel 1 set "VSWHERE=vswhere.exe"
if not defined VSWHERE if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not defined VSWHERE (
  echo ERROR: Native AOT platform linker ^(link.exe^) not found in PATH.
  echo ERROR: Open a Developer Command Prompt or install Visual Studio C++ build tools.
  exit /b 1
)

for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2^>nul`) do (
  if exist "%%I\Common7\Tools\VsDevCmd.bat" (
    set "VSCMD_SKIP_SENDTELEMETRY=1"
    call "%%I\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
    where link.exe >nul 2>&1
    if not errorlevel 1 goto DETECT_NATIVE_AOT_OK
  )
)

echo ERROR: Native AOT platform linker ^(link.exe^) not found.
echo ERROR: Open a Developer Command Prompt or install Visual Studio C++ build tools.
exit /b 1

:DETECT_NATIVE_AOT_OK
echo Native AOT toolchain detected.
exit /b 0

:TERMINATE_PROCESSES
taskkill /F /IM FortniteVideoSoftware.exe /T 2>nul
taskkill /F /IM FortniteVideoSoftware.App.exe /T 2>nul
dotnet build-server shutdown 2>nul
exit /b 0

:CLEAN_ALL
if exist "src\FortniteVideoSoftware.App\bin" rd /s /q "src\FortniteVideoSoftware.App\bin" 2>nul
if exist "src\FortniteVideoSoftware.App\obj" rd /s /q "src\FortniteVideoSoftware.App\obj" 2>nul
if exist "src\FortniteVideoSoftware.Core\bin" rd /s /q "src\FortniteVideoSoftware.Core\bin" 2>nul
if exist "src\FortniteVideoSoftware.Core\obj" rd /s /q "src\FortniteVideoSoftware.Core\obj" 2>nul
dotnet clean src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj -c Release -r win-x64 --nologo -v q >nul 2>&1
exit /b 0

@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

if "%~1"=="--internal-log" goto :run_logged
if exist build.log del /f /q build.log
powershell -NoProfile -Command "& { & '%~f0' --internal-log %* 2>&1 | Tee-Object -FilePath build.log; exit $LASTEXITCODE }"
set "RC=%ERRORLEVEL%"

rem =====================================================================================
rem  THE ONLY PLACE THAT PAUSES.
rem  It lives in the OUTER invocation on purpose: the inner run is piped through
rem  Tee-Object, and a `pause` inside a pipe prompts into the pipe instead of the console,
rem  which looks like a hang. Exit codes carry the verdict out here:
rem      0 = build OK and release published (or publishing was not requested)
rem      1 = BUILD FAILED           -> nothing was published, the release is untouched
rem      2 = build OK, PUBLISH DID NOT COMPLETE -> local exe is good, GitHub was not updated
rem  Success is silent so a clean run needs no keypress.
rem =====================================================================================
if "%RC%"=="1" (
  echo.
  echo ###########################################################
  echo  BUILD FAILED - nothing was published.
  echo  The existing GitHub release was NOT touched.
  echo  Scroll up for the first ERROR line, or read .\build.log
  echo ###########################################################
  pause
)
if "%RC%"=="2" (
  echo.
  echo ###########################################################
  echo  BUILD OK - but the release was NOT updated.
  echo  .\compiled\FortniteVideoSoftware.exe is good and usable.
  echo  Only the GitHub publish step did not finish - reason above.
  echo ###########################################################
  pause
)
exit /b %RC%

:run_logged
shift
setlocal enabledelayedexpansion
cd /d "."

rem --no-publish  = compile only, leave GitHub alone. Everything else needs no arguments.
set "DO_PUBLISH=1"
if /I "%~1"=="--no-publish" set "DO_PUBLISH=0"

set "PROJECT_FILE=src\FortniteVideoSoftware.App\FortniteVideoSoftware.App.csproj"
set "PROJECT_EXE=FortniteVideoSoftware.App.exe"
set "OUTPUT_EXE=FortniteVideoSoftware.exe"
set "OUTPUT_DIR=.\compiled"
set "FINAL_DIR=.\obj\StandaloneTemp\NativeAot_final"
set "PUBLISH_BASE_ARGS=-p:TreatWarningsAsErrors=true"
set "PUBLISH_AOT_ARGS=-p:PublishAot=true -p:SelfContained=true"
set "DOTNET_LOG_ARGS=-consoleLoggerParameters:ErrorsOnly"

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

if "!DO_PUBLISH!"=="0" (
  echo.
  echo [PUBLISH] Skipped on request ^(--no-publish^). GitHub was not touched.
  exit /b 0
)

echo.
echo ###########################################################
echo PUBLISHING RELEASE TO GITHUB...
echo ###########################################################
call :PUBLISH_RELEASE
if errorlevel 1 exit /b 2

exit /b 0

rem =====================================================================================
rem  :PUBLISH_RELEASE  -  replace the one-and-only GitHub release with what was just built.
rem
rem  ONLY REACHED WHEN THE BUILD SUCCEEDED. dotnet publish runs with
rem  -p:TreatWarningsAsErrors=true, so "no errors" is already enforced upstream; if any
rem  stage above failed the script exited 1 long before this point and GitHub is untouched.
rem
rem  ZERO MAINTENANCE BY DESIGN - nothing in here is hardcoded:
rem    * the repository is resolved from THIS FOLDER'S git remote via `gh repo view`,
rem      so renaming or forking the repo needs no edit here;
rem    * the tag is derived from today's date (vYYYY.MM.DD) through PowerShell rather than
rem      %DATE%, which is locale-dependent and would break on a non-US machine;
rem    * every pre-existing release is enumerated and removed, so "latest and only" stays
rem      true without anyone tracking version numbers.
rem
rem  ⚠️ THE UPLOAD IS VERIFIED BY HASH, NOT BY EXIT CODE. `gh release upload` reports
rem  success even when the asset that ends up attached is not the file you meant to send -
rem  that exact failure shipped a two-day-old binary once. Step 7 compares the SHA256 of
rem  the local exe against the digest GitHub reports and fails if they differ.
rem =====================================================================================
:PUBLISH_RELEASE

where gh >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: GitHub CLI ^(gh^) is not installed or not on PATH.
  echo [PUBLISH] Fix: install from https://cli.github.com  - or run  .\build.cmd --no-publish
  exit /b 1
)
echo [PUBLISH] 1/7 GitHub CLI found.                                    [OK]

gh auth status >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: gh is installed but not signed in.
  echo [PUBLISH] Fix: run  gh auth login
  exit /b 1
)
echo [PUBLISH] 2/7 GitHub sign-in valid.                                [OK]

set "REPO="
for /f "usebackq delims=" %%R in (`gh repo view --json nameWithOwner --jq .nameWithOwner 2^>nul`) do set "REPO=%%R"
if not defined REPO (
  echo [PUBLISH] STOPPED: could not work out the GitHub repository for this folder.
  echo [PUBLISH] Fix: confirm  git remote -v  points at GitHub and you can reach the network.
  exit /b 1
)
echo [PUBLISH] 3/7 Target repository: !REPO!            [OK]

set "LOCALHASH="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%OUTPUT_DIR%\%OUTPUT_EXE%" SHA256') do (
  if not defined LOCALHASH set "LOCALHASH=%%H"
)
set "LOCALHASH=!LOCALHASH: =!"
if not defined LOCALHASH (
  echo [PUBLISH] STOPPED: could not fingerprint the freshly built exe.
  exit /b 1
)
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd"`) do set "TAG=v%%D"
echo [PUBLISH] 4/7 Built exe fingerprint + tag !TAG! ready.        [OK]

set "REMOVED=0"
for /f "usebackq delims=" %%T in (`gh release list --repo !REPO! --json tagName --jq ".[].tagName" 2^>nul`) do (
  echo [PUBLISH]     removing previous release %%T
  gh release delete %%T --repo !REPO! --cleanup-tag --yes >nul 2>&1
  set /a REMOVED+=1
)
echo [PUBLISH] 5/7 Previous releases removed: !REMOVED!                    [OK]

gh release create !TAG! "%OUTPUT_DIR%\%OUTPUT_EXE%" --repo !REPO! --title "Fortnite Video Software !TAG!" --notes "Automated NativeAOT release published by build.cmd on !TAG!. SHA256 !LOCALHASH!" --latest >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: creating release !TAG! failed.
  echo [PUBLISH] Your build is fine - only the upload failed. Retry, or publish by hand.
  exit /b 1
)
echo [PUBLISH] 6/7 Release !TAG! created and asset uploaded.        [OK]

set "REMOTEHASH="
for /f "usebackq delims=" %%V in (`gh release view !TAG! --repo !REPO! --json assets --jq ".assets[0].digest" 2^>nul`) do set "REMOTEHASH=%%V"
set "REMOTEHASH=!REMOTEHASH:sha256:=!"
if /I not "!REMOTEHASH!"=="!LOCALHASH!" (
  echo [PUBLISH] STOPPED: the uploaded asset does NOT match the file that was just built.
  echo [PUBLISH]     built    : !LOCALHASH!
  echo [PUBLISH]     published: !REMOTEHASH!
  echo [PUBLISH] The release is serving the WRONG binary - fix before telling anyone about it.
  exit /b 1
)
echo [PUBLISH] 7/7 Published asset hash matches the built exe.          [OK]

echo.
echo ###########################################################
echo SUCCESS: release !TAG! is live and is the only release.
echo Download: https://github.com/!REPO!/releases/latest/download/%OUTPUT_EXE%
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
copy /y ".\binaries\ffprobe.exe" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\av*.dll" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\sw*.dll" "%STAGING_DIR%\backend\" >nul
copy /y ".\binaries\postproc*.dll" "%STAGING_DIR%\backend\" >nul

copy /y ".\binaries\libmpv-2.dll" "%STAGING_DIR%\frontend\" >nul
copy /y ".\binaries\mpv.exe" "%STAGING_DIR%\frontend\" >nul

rem =====================================================================================
rem  STARTER_01 — SHIP A SMALL STARTER LIBRARY INSIDE THE INSTALLER.
rem
rem  ⚠️ THIS IS WHY NEW INSTALLS USED TO ARRIVE COMPLETELY EMPTY. Nothing here copied any
rem  media, so payload.zip contained only the program and its codecs — while the seeding
rem  code hunted for an `mp3` folder FIVE LEVELS ABOVE the exe. That path only exists in
rem  the dev tree, so on a real machine every File.Exists() failed and the user got a
rem  meme folder, a music folder and an image folder that were all empty, with no hint
rem  that anything was missing.
rem
rem  DELIBERATELY A HANDFUL, NOT THE WHOLE LIBRARY: mp3\ alone is 197 MB. These eight
rem  files add ~45 MB to a ~247 MB installer. Everything else is a click away via the
rem  three "Download more" buttons, which pull straight from the GitHub project.
rem
rem  ⚠️ FILENAMES ARE THE CONTRACT. MemeAssets.StarterFiles in C# lists these exact names
rem  and MemeAssets.PrependByDefault tags one of them. Rename a file here and you must
rem  rename it there too, or it silently stops being seeded.
rem =====================================================================================
echo [NativeAOT] 2.6 Copying starter media to staging...
mkdir "%STAGING_DIR%\starter\mp3"  2>nul
mkdir "%STAGING_DIR%\starter\mp4"  2>nul
mkdir "%STAGING_DIR%\starter\jpeg" 2>nul

copy /y ".\mp3\Bonnie Tyler - Holding Out For A Hero.mp3"          "%STAGING_DIR%\starter\mp3\"  >nul
copy /y ".\mp3\Cool Dance Background Music (No CopyRights).mp3"    "%STAGING_DIR%\starter\mp3\"  >nul

copy /y ".\mp4\What the fuck am I doing here (Robert Deniro).mp4"  "%STAGING_DIR%\starter\mp4\"  >nul
copy /y ".\mp4\Donald Trump - He Died like a Dog.mp4"              "%STAGING_DIR%\starter\mp4\"  >nul
copy /y ".\mp4\I will find you and I will kill you.mp4"            "%STAGING_DIR%\starter\mp4\"  >nul

copy /y ".\jpeg\*.png"  "%STAGING_DIR%\starter\jpeg\" >nul
copy /y ".\jpeg\*.jpg"  "%STAGING_DIR%\starter\jpeg\" >nul

rem Fail loudly rather than shipping an installer that silently seeds nothing.
if not exist "%STAGING_DIR%\starter\mp4\I will find you and I will kill you.mp4" (
  echo ERROR: starter media missing from staging - the installer would seed an empty library.
  exit /b 1
)

echo [NativeAOT] 3. Zipping payload...
tar.exe -a -c -f "src\FortniteVideoSoftware.App\payload.zip" -C "%STAGING_DIR%" .
if errorlevel 1 (
    echo ERROR: Failed to zip payload.
    exit /b 1
)

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

call :CODE_SIGN "%OUTPUT_DIR%\%OUTPUT_EXE%"
if errorlevel 1 exit /b 1

call :PURGE_COMPILED_EXTRAS
if errorlevel 1 exit /b 1

echo [NativeAOT] 6. Cleaning up temporary artifacts...
if exist "%STAGING_DIR%" rd /s /q "%STAGING_DIR%"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
if exist "src\FortniteVideoSoftware.App\payload.zip" del /f /q "src\FortniteVideoSoftware.App\payload.zip"

exit /b 0

:CODE_SIGN
rem ============================================================================================
rem  ISSUE_03 - AUTHENTICODE SIGNING. DORMANT UNTIL A CERTIFICATE IS CONFIGURED.
rem
rem  Why this exists: the product ships as ONE .exe downloaded straight from the internet. An
rem  unsigned .exe triggers SmartScreen's full-screen "Windows protected your PC - Unknown
rem  publisher" wall on first run. That wall is the first thing a new user ever sees and it reads
rem  as malware. The csproj now supplies the Properties/Details metadata; this supplies the
rem  signature. BOTH are needed - metadata alone does not silence SmartScreen.
rem
rem  HOW TO TURN IT ON (no script edit required):
rem      set FVS_SIGN_PFX=C:\path\to\your-cert.pfx
rem      set FVS_SIGN_PASS=your-pfx-password
rem  then run Build.cmd exactly as usual. Leave them unset and the build behaves as it always has,
rem  printing a one-line reminder instead of failing.
rem
rem  DELIBERATE CHOICES, DO NOT "SIMPLIFY":
rem    * The password is read from the environment, never hardcoded here. A .pfx password committed
rem      to a public repository is a revoked certificate.
rem    * /tr with an RFC-3161 timestamp server is MANDATORY. Without a timestamp every copy of the
rem      product stops validating the day the certificate expires - including copies already on
rem      users machines.
rem    * If FVS_SIGN_PFX is set but signing FAILS, the build FAILS. Silently shipping an unsigned
rem      binary when the operator asked for a signed one is the worst of the three outcomes.
rem ============================================================================================
if "%FVS_SIGN_PFX%"=="" (
  echo [Sign] No FVS_SIGN_PFX set - shipping UNSIGNED. Windows SmartScreen will warn end users.
  echo [Sign] To sign: set FVS_SIGN_PFX and FVS_SIGN_PASS, then re-run Build.cmd.
  exit /b 0
)

if not exist "%FVS_SIGN_PFX%" (
  echo ERROR: FVS_SIGN_PFX is set but the file does not exist: %FVS_SIGN_PFX%
  exit /b 1
)

rem  signtool.exe is NOT on PATH in a plain shell - it lives inside the Windows SDK. Probe PATH
rem  first, then fall back to the newest x64 SDK copy. The powershell fallback is deliberately NOT
rem  wrapped in an if(...) block: escaping a pipe inside parentheses inside a for /f inside a batch
rem  block is a well-known way to silently produce an empty result. A goto keeps it at top level.
set "SIGNTOOL="
where signtool.exe >nul 2>&1
if not errorlevel 1 set "SIGNTOOL=signtool.exe"
if not "!SIGNTOOL!"=="" goto CODE_SIGN_HAVE_TOOL

for /f "usebackq delims=" %%S in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'; if (Test-Path $p) { Get-ChildItem -Path $p -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object FullName -like '*x64*' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName }"`) do set "SIGNTOOL=%%S"

:CODE_SIGN_HAVE_TOOL
if "!SIGNTOOL!"=="" (
  echo ERROR: FVS_SIGN_PFX is set but signtool.exe could not be found.
  echo        Install the Windows SDK Signing Tools, or put signtool.exe on PATH.
  exit /b 1
)

echo [Sign] Signing %~1 ...
"!SIGNTOOL!" sign /fd SHA256 /f "%FVS_SIGN_PFX%" /p "%FVS_SIGN_PASS%" /tr http://timestamp.digicert.com /td SHA256 "%~1"
if errorlevel 1 (
  echo ERROR: Authenticode signing FAILED. Refusing to ship an unsigned binary that was meant to be signed.
  exit /b 1
)

"!SIGNTOOL!" verify /pa "%~1"
if errorlevel 1 (
  echo ERROR: Signature verification failed for %~1
  exit /b 1
)
echo [Sign] Signed and verified.
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

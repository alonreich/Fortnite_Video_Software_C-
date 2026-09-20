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
REM Subroutine: VERIFY_PATCHES
REM
REM Fix sentinels have gone missing from source between a commit and a build
REM more than once, which silently produced a binary WITHOUT the fix and cost a
REM full test cycle to discover. Each sentinel below is a comment tag that sits
REM next to a specific fix. If one is absent the source has been reverted and
REM building is pointless - say so loudly BEFORE the build, not after the test.
REM ======================================================================
:VERIFY_PATCHES
REM VERIFYLOOP_01 - one data-driven loop, NOT 49 separate `call :CHECK_TAG` subroutine calls,
REM and the :CHECK_TAG subroutine itself is gone.
REM
REM The old shape failed in the field with:
REM     The system cannot find the batch label specified - CHECK_TAG
REM printed twice, while the other 47 calls worked fine. cmd.exe resolves `call :label` by
REM SEEKING through the batch file by byte offset, and this file was a mix of CRLF and bare-LF
REM lines (96 of them). Adding sentinel lines shifted every later offset, and two of the seeks
REM then landed badly.
REM
REM The silent damage was worse than the noise on screen: a seek that fails does NOT run the
REM check and does NOT record anything, so VERIFY_PATCHES was quietly testing 47 of 49
REM sentinels and reporting a clean pass for the two it skipped - which is the exact blind
REM spot this subroutine exists to close.
REM
REM One loop performs ZERO label seeks, so the failure cannot return however many sentinels are
REM added. The whole file is CRLF now, which is what a .cmd should be. Keep it that way.
REM
REM To add a sentinel: add one "TAG=relative\path" line to the list. Nothing else.
REM Do NOT pad the tags to line them up - everything left of the "=" IS the string findstr
REM searches for, so a padded tag matches nothing and reports every file as reverted.
set "MISSING="
for %%P in (
  "LAYOUTLOOP_01=src\FortniteVideoSoftware.App\Controls\TimelineLanesControl.axaml.cs"
  "ZOOMSIZE_02=src\FortniteVideoSoftware.App\Controls\TimelineLanesControl.axaml.cs"
  "LAYOUTLOOP_02=src\FortniteVideoSoftware.App\GranularSpeedEditorWindow.axaml.cs"
  "SEEKSTORM_01=src\FortniteVideoSoftware.App\GranularSpeedEditorWindow.axaml.cs"
  "EDGEGUARD_01=src\FortniteVideoSoftware.App\GranularSpeedEditorWindow.axaml.cs"
  "DRAGCOST_01=src\FortniteVideoSoftware.App\GranularSpeedEditorWindow.axaml.cs"
  REM STRIPCOST_01 is RETIRED, not lost. It guarded "scale with a RenderTransform, never with
  REM Width" on the per-slot filmstrip Image. Commit ad0b7bd deleted that whole code path and
  REM replaced it with Controls\TimelineFilmstrip, which draws via context.DrawImage with an
  REM explicit source and destination rect - so there is no layout box to oversize and no
  REM 32768px bitmap for Skia to rasterise. The defect is structurally unreachable, so there is
  REM no fix left for a sentinel to protect. Found by ArchitectureRuleTests.
  REM EveryDevCmdSentinelStillResolves once VERIFYHALT_01 made the check able to speak.
  "TRACEFLOOD_01=src\FortniteVideoSoftware.App\Program.cs"
  "THUMB_02=src\FortniteVideoSoftware.App\MainWindow.Canvas.cs"
  "MAINEND_01=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "FREEZE_01=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "TRANSPORT_TRACE_01=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "MPVEOF_01=src\FortniteVideoSoftware.Core\Media\MpvIpcClient.cs"
  "VOEND_01=src\FortniteVideoSoftware.App\VoiceOverWindow.axaml.cs"
  "VOMON_02=src\FortniteVideoSoftware.App\VoiceOverWindow.axaml.cs"
  "LIST_05=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml"
  "LIST_06=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml.cs"
  "P3ASYNC_01=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml.cs"
  "LAYOUT_03=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml"
  "SLIDER_06=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml"
  "SLIDER_07=src\FortniteVideoSoftware.App\AvaloniaApp.axaml"
  "SLIDER_08=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml"
  "SLIDER_09=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml"
  "GRIP_01=src\FortniteVideoSoftware.App\Controls\WindowResizeGrip.cs"
  "FIRSTFIT_01=src\FortniteVideoSoftware.App\WindowBoundsHelper.cs"
  "RESUME_01=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml.cs"
  "QUALITY_01=src\FortniteVideoSoftware.App\ViewModels\QualityLadder.cs"
  "QUALITY_02=src\FortniteVideoSoftware.App\ViewModels\QualityLadder.cs"
  "QUALITY_03=src\FortniteVideoSoftware.App\ViewModels\QualityLadder.cs"
  "QUALITY_04=src\FortniteVideoSoftware.App\MainWindow.Wireup.cs"
  "QUALITY_05=src\FortniteVideoSoftware.App\ViewModels\TimelineViewModel.cs"
  "SIZEESTIMATE_01=src\FortniteVideoSoftware.App\MainWindow.SizeEstimate.cs"
  "SIZEESTIMATE_01=src\FortniteVideoSoftware.App\MainWindow.axaml"
  "SIZEESTIMATE_01=src\FortniteVideoSoftware.App\MainWindow.Export.cs"
  "SIZEESTIMATE_01=src\FortniteVideoSoftware.App\VideoMergerWindow.axaml.cs"
  "SIZEESTIMATE_01=src\FortniteVideoSoftware.App\Services\LatestEstimateWorker.cs"
  "SIZEESTIMATE_01=src\FortniteVideoSoftware.Core\Media\OutputFileSize.cs"
  "DOUBLEFIRE_01=src\FortniteVideoSoftware.App\MainWindow.Wireup.cs"
  "LIST_07=src\FortniteVideoSoftware.App\MusicWizardWindow.axaml"
  "LAYOUT_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "GATE_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "GATE_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "ZOOM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "ZOOM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  REM WIZPROGRESS_01 is RETIRED, not lost. It guarded a ProgressBar added to fix a dead
  REM FindControl reference; WIZCOMPACT_01 then deleted the bar, the field AND the writer, so
  REM there is no fix left for a sentinel to protect. A guard for a deleted fix is a permanent
  REM false alarm - exactly the QUALITY_04 failure this list already learned from once.
  "CROPZOOMRESET_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "CROPSAVEPROMPT_02=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "CROPUNSAVED_01=src\FortniteVideoSoftware.App\Controls\ConfirmDialogWindow.axaml.cs"
  "CROPUNSAVED_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "CROPFIRSTBOOT_01=src\FortniteVideoSoftware.App\Infrastructure\MaskOverlayManager.cs"
  "FORTNITEDEFAULT_02=src\FortniteVideoSoftware.Core\Ipc\CropConfigDefaults.cs"
  "CROPFALLBACK_02=src\FortniteVideoSoftware.Core\Ipc\CropConfigStore.cs"
  "SPECTATINGDEFAULT_01=src\FortniteVideoSoftware.App\ViewModels\MainViewModel.cs"
  "NO_BOSS_HP_01=src\FortniteVideoSoftware.Core\Media\MobileFilterBuilder.cs"
  "SAVECONFIRM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "MAGICWAND_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "DELETEBTN_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "DELETESET_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "CROPCANVAS_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "AUTOZOOM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "WHEELZOOM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "PAN_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "BEZEL_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "SPLIT_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  REM --- Crop Tools rework, phase 3: the right pane is gone and naming happens at the box. ---
  "LAYERSPANE_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "ROLEPOPUP_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "ROLEPOPUP_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "ANTS_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "PLAYICON_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "TICKRULER_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "AUTOPLAY_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "ITEMMENU_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  REM --- Crop Tools rework, phase 4: hit-testing, ghosts, and vertical space. ---
  "ITEMHIT_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "GHOSTKILL_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "CANCELSEL_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "BACKTOVIDEO_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "WIZCOLLAPSE_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "PLAYOVERLAY_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "ZOOMBAR_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "WIZCOMPACT_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "TIMELINESLIM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "RESETMOVE_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "ORDERICONS_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  REM --- Crop Tools rework, phase 5. ---
  "RELAUNCHARG_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "HANDLECURSOR_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "NODUPES_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "COMPOSERDIM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "COMPOSERDIM_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "HEADERMERGE_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "TIMELINESLIM_02=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "PLAYROW_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  "POPUPCLEAR_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "AUTOZOOM_02=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "CROSSHAIR_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "POPUPCLEAR_01=src\FortniteVideoSoftware.App\CropToolWindow.axaml"
  REM --- Concurrency / lifetime audit, 2026-09-18. Each tag guards a fix that cost a full
  REM --- diagnosis cycle to find and would revert silently. See docs/05 and docs/03.
  REM Finding 1 - GPU image slots were mutated by three threads with no synchronisation.
  "GPUSLOT_01=src\FortniteVideoSoftware.App\MpvVideoView.cs"
  "GPUPRESENT_01=src\FortniteVideoSoftware.App\MpvVideoView.cs"
  REM Finding 2 - settings.json was written non-atomically to a fixed temp name, cross-process.
  "SETTINGSATOMIC_01=src\FortniteVideoSoftware.App\Infrastructure\SettingsManager.cs"
  "ATOMICTEXT_01=src\FortniteVideoSoftware.Core\Infrastructure\AtomicJsonFile.cs"
  REM Finding 3 - cancel re-armed PROCESS before the pipeline stopped; the next export disposed
  REM the CancellationTokenSource the previous worker was still registered on.
  "EXPORTSESSION_01=src\FortniteVideoSoftware.App\MainWindow.Export.cs"
  "EXPORTSESSION_01=src\FortniteVideoSoftware.App\MainWindow.Wireup.cs"
  "EXPORTSESSION_01=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "CANCELREG_01=src\FortniteVideoSoftware.Core\Media\ProcessWorker.cs"
  "OUTPATH_01=src\FortniteVideoSoftware.Core\Media\ProcessWorker.cs"
  REM Finding 4 - ProcessWorker is IDisposable and was never disposed, so ISSUE_11's
  REM kill-the-tree backstop was unreachable code.
  "WORKERLIFETIME_01=src\FortniteVideoSoftware.App\Services\MainMediaController.cs"
  "WORKERLIFETIME_02=src\FortniteVideoSoftware.App\Services\MainMediaController.cs"
  REM Finding 5 - the session-state flush debounce had no maximum-wait ceiling, and teardown
  REM disposed the CancellationTokenSource under a live listener.
  "FLUSHCEILING_01=src\FortniteVideoSoftware.Core\Ipc\NamedPipeStateServer.cs"
  "IPCTEARDOWN_01=src\FortniteVideoSoftware.Core\Ipc\NamedPipeStateServer.cs"
  "IPCLEASE_01=src\FortniteVideoSoftware.Core\Ipc\IpcProtocol.cs"
  "IPCLEASE_01=src\FortniteVideoSoftware.Core\Ipc\NamedPipeStateServer.cs"
  REM Finding 6 - the two-pass tail disposed the Process while its pipe readers were still live.
  "PIPEDRAIN_01=src\FortniteVideoSoftware.Core\Media\ProcessWorker.cs"
  REM Finding 7 - Cancel re-read a non-volatile Process field between the null test and Kill.
  "PROCGATE_01=src\FortniteVideoSoftware.Core\Media\ProcessWorker.cs"
  "PROCGATE_02=src\FortniteVideoSoftware.Core\Media\ProcessWorker.cs"
  REM Finding 9 - the Granular editor constructor blocked the UI thread on ffprobe.
  "GRANPROBE_01=src\FortniteVideoSoftware.App\GranularSpeedEditorWindow.axaml.cs"
  "GRANPROBE_01=src\FortniteVideoSoftware.App\MainWindow.Wireup.cs"
  REM --- Architecture remediation, phase 0 (foundation). docs/08_APPLICATION_COMPOSITION.md.
  REM --- Composition root, fault tiers, signing mandate and the executable spec rules.
  "COMPOSITION_01=src\FortniteVideoSoftware.App\Infrastructure\AppServices.cs"
  "COMPOSITION_01=src\FortniteVideoSoftware.App\Program.cs"
  "COMPOSITION_02=src\FortniteVideoSoftware.App\Infrastructure\AppServices.cs"
  "FAULTTIER_01=src\FortniteVideoSoftware.Core\Abstractions\Fault.cs"
  "FAULTTIER_01=src\FortniteVideoSoftware.Core\Abstractions\IFaultSink.cs"
  "FAULTTIER_01=src\FortniteVideoSoftware.App\Services\UserFacingFaultSink.cs"
  "FAULTSTORM_01=src\FortniteVideoSoftware.App\Services\UserFacingFaultSink.cs"
  "SEAM_01=src\FortniteVideoSoftware.Core\Abstractions\IProjectStore.cs"
  "SEAM_02=src\FortniteVideoSoftware.Core\Abstractions\IClock.cs"
  "SEAM_03=src\FortniteVideoSoftware.App\Abstractions\IUserNotifier.cs"
  "SEAM_04=src\FortniteVideoSoftware.App\Abstractions\IFilePickerService.cs"
  "PICKERMEMORY_01=src\FortniteVideoSoftware.App\Services\StorageProviderFilePicker.cs"
  "SIGNMANDATE_01=build\FvsBuild\CodeSigning.cs"
  "UPDATETRUST_02=src\FortniteVideoSoftware.App\Services\UpdateService.cs"
  "UPDATETRUST_02=src\FortniteVideoSoftware.App\Services\AuthenticodeVerifier.cs"
  "SCRIM_01=src\FortniteVideoSoftware.App\AvaloniaApp.axaml"
  "ZOOMCARD_01=src\FortniteVideoSoftware.App\AvaloniaApp.axaml"
  "ARCHTEST_01=tests\FortniteVideoSoftware.App.Tests\ArchitectureRuleTests.cs"
  "ASYNCUI_01=tests\FortniteVideoSoftware.App.Tests\ArchitectureRuleTests.cs"
  "ASYNCUI_02=tests\FortniteVideoSoftware.App.Tests\ArchitectureRuleTests.cs"
  REM --- Architecture remediation, phase 1. Document model wired to the UI + one undo.
  "PROJSESSION_01=src\FortniteVideoSoftware.App\Services\ProjectSession.cs"
  "PROJSESSION_01=src\FortniteVideoSoftware.App\MainWindow.Project.cs"
  "PROJSESSION_02=src\FortniteVideoSoftware.App\Services\ProjectSession.cs"
  "PROJSESSION_03=src\FortniteVideoSoftware.App\Services\ProjectSession.cs"
  "PROJSESSION_04=src\FortniteVideoSoftware.App\MainWindow.Shortcuts.cs"
  "PROJSESSION_05=src\FortniteVideoSoftware.App\MainWindow.Project.cs"
  "PROJSESSION_06=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "PROJSESSION_07=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "UNDO_11=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "UNDO_12=src\FortniteVideoSoftware.App\Services\ProjectSession.cs"
  "UNDO_13=src\FortniteVideoSoftware.App\MainWindow.Project.cs"
  REM --- Architecture remediation, phase 2. Companion tools open in-process.
  "TOOLNAV_01=src\FortniteVideoSoftware.App\Services\ToolNavigator.cs"
  "TOOLNAV_01=src\FortniteVideoSoftware.App\MainWindow.axaml.cs"
  "TOOLNAV_02=src\FortniteVideoSoftware.App\Services\ToolNavigator.cs"
  "TOOLNAV_03=src\FortniteVideoSoftware.App\Services\ToolNavigator.cs"
  "TOOLNAV_04=src\FortniteVideoSoftware.App\Services\ToolNavigator.cs"
  "TOOLNAV_04=src\FortniteVideoSoftware.App\CropToolWindow.axaml.cs"
  "TOOLNAV_04=src\FortniteVideoSoftware.App\VideoMergerWindow.axaml.cs"
) do (
    for /f "tokens=1,2 delims==" %%A in ("%%~P") do (
        if not exist "%%B" (
            set "MISSING=!MISSING! %%A[no-file]"
        ) else (
            findstr /C:"%%A" "%%B" >nul 2>nul
            REM `if errorlevel` reads the LIVE exit code. %ERRORLEVEL% would be expanded once
            REM when cmd parsed this whole block and would then never change.
            if errorlevel 1 set "MISSING=!MISSING! %%A"
        )
    )
)

REM ======================================================================
REM VERIFYHALT_01 - ACT ON THE RESULT. THIS BLOCK DID NOT EXIST.
REM
REM VERIFY_PATCHES built the MISSING list correctly and then... returned.
REM `MISSING` was assigned in two places and read in NONE, so every sentinel
REM in the list above - 134 of them - was being checked and the answer thrown
REM away. The subroutine SYS-DEVBUILD describes as the thing that "halts
REM loudly if one is absent" has been a no-op.
REM
REM This is not hypothetical damage. STRIPCOST_01 sat in the list pointing at
REM a tag that no longer existed in GranularSpeedEditorWindow.axaml.cs (the
REM code path was rewritten in ad0b7bd, superseding the fix rather than
REM reverting it) and nothing ever said so. A guard that cannot fail is a
REM guard that cannot be trusted, which is worse than no guard at all -
REM VERIFYLOOP_01 above learned exactly this lesson once already, about two
REM skipped entries, and the fix for it left the reporting half unwritten.
REM
REM A missing sentinel HALTS. It means either the fix it guards was reverted
REM (test cycle about to be wasted) or the sentinel is stale (retire it with
REM a REM, as WIZPROGRESS_01 and STRIPCOST_01 are). Both need a human.
REM ======================================================================
if defined MISSING (
    echo.
    echo [DEV] ==================================================================
    echo [DEV] FIX SENTINEL CHECK FAILED - BUILD HALTED.
    echo [DEV]
    echo [DEV] These tags are listed in VERIFY_PATCHES but were NOT found in
    echo [DEV] their files:
    echo [DEV]   !MISSING!
    echo [DEV]
    echo [DEV] Either the fix was reverted - restore it - or the fix was
    echo [DEV] superseded and the sentinel is stale - retire it with a REM
    echo [DEV] saying why, next to its entry in the list above.
    echo [DEV] ==================================================================
    echo.
    exit /b 1
)

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

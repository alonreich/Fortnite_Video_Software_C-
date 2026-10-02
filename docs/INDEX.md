# SYMBOL & FILE INDEX (routing lookup)

Flat lookup. Grep for a filename, symbol, constant or engineering tag; read ONLY the spec it names.
Notation: `03 §9 FFM-BINPATH` = `03_FFMPEG_EXPORT_PIPELINE.md`, section 9, stable anchor `FFM-BINPATH`.
Section NUMBERS shift as specs grow. The `{#ANCHOR}` ids are STABLE — quote anchors in Proof-of-Read headers, never bare numbers.
`mini-map only` = the symbol is bound to the spec but not discussed in its prose body; read that spec's Code Mini-Map row.
`[code-only: File]` = the tag/symbol is named in that source file but in NO spec's text; the route given is the closest spec section, or just the spec governing that file. Treat the missing prose as a documentation gap, not as permission.
`Mechanical Sentinels vs Spec Anchors`: Engineering tags in `build/sentinels.txt` are compile/verify gates enforced by `build/FvsVerify`. Tags listed below without a `build/sentinels.txt` line serve as specification section anchors and architectural invariant markers.

---

## 1. Source File -> Spec
`⚠` = CO-GOVERNED. Reading one listed spec is NOT compliance; read them all (`SPEC_GOVERNANCE.md` §2).

⚠️ **This list is NOT exhaustive** and never has been — many files under `src/` are absent from it. The authoritative routing for any file is the `[SPEC CONTRACT]` sentinel block at its own line 1, which every one of them carries and which `ArchitectureRuleTests.EveryProductionSourceFileCarriesTheSpecContract` enforces. Use this table as a fast lookup, not as proof that an unlisted file is ungoverned.

```
  AiSetupWizardWindow.axaml.cs               04
  AiSubjectPickerWindow.axaml.cs             04
⚠ AiTrajectorySmoother.cs                    01 03
  AmbientBubblesBackground.cs                04
  AppDataPaths.cs                            05 SYS-REBRAND
  AppDataPathsTests.cs                       05 SYS-REBRAND
  LegacyAppDataNames.txt                     05 SYS-REBRAND
⚠ ApplicationPaths.cs                        05 GOV
⚠ AtomicJsonFile.cs                          05 06
  AudioFilterChain.cs                        02
  AudioLoudnessProbe.cs                      02
  AvaloniaApp.axaml                          04
⚠ Build.cmd                                  05 09 GOV
  dev_build.cmd                              05 09 GOV
  CanvasMath.cs                              01
  CoachOverlay.cs                            04
⚠ CompositeTimeline.cs                       01 03
  ConfirmDialogWindow.axaml.cs               04
  CoordinateMath.cs                          01
  DeploymentLifecycle.cs                     05
⚠ dev.cmd                                    05 08
  ExportTimingTag.cs                         03
  FfmpegDiagnosticCollector.cs               03
  FloatingNotice.cs                          04
⚠ FluidVolumeSlider.cs                       02 04
  FramePtsProbe.cs                           03
  GeminiTrackingService.cs                   01 03 04
  GpuCapabilityProbe.cs                      03
  GranularSpeedBuilder.cs                    03
⚠ GranularSpeedEditorWindow.axaml.cs         01 04 05 07
⚠ GranularSpeedEditorWindow.AiZoom.cs        01 04
⚠ GranularSpeedEditorWindow.Merge.cs         01 04
  HardwareScanner.cs                         03
  KineticScrubController.cs                  01
  LatestEstimateWorker.cs                    05
  MainMediaController.cs                     03
  MainWindow.Canvas.cs                       01
  MainWindow.Export.cs                       03
  MainWindow.SizeEstimate.cs                 03
  MainWindow.Shortcuts.cs                    01
⚠ MainWindow.ToolReturn.cs                   05 08
  MainWindow.Wireup.cs                       01
⚠ MainWindow.axaml.cs                        01 02 04 GOV
  MaskOverlayManager.cs                      05
⚠ MemeLoudness.cs                            02 03
  MemePreviewDirector.cs                     03
⚠ MergeClipGraph.cs                          01 03
⚠ MergeEditorBridge.cs                       01 03
⚠ MergeEdl.cs                                01 03 06
  MergerAutosaveStore.cs                     05
⚠ MergerPreviewPlan.cs                       01 03
⚠ MergerSession.cs                           01 05
  MergerWorker.cs                            03
  MicLevelMonitor.cs                         02
  MobileFilterBuilder.cs                     03
  IpcProtocol.cs                             05
  MpvIpcClient.cs                            02
⚠ MpvVideoView.cs                            04 05
  NamedPipeStateServer.cs                    05
⚠ MusicWizardWindow.axaml.cs                 01 02
⚠ OutputTimeline.cs                          01 06
  OutputFileSize.cs                          03
  OutputSizeEstimator.cs                     03
⚠ PhoneFrameMockup.axaml.cs                  01 04
  ExportViewModel.cs                         03
  ProcessWorker.cs                           03
  ProgressiveLanes.cs                        04
  QualityLadder.cs                           03
  ProjectRecoveryService.cs                  05
⚠ RecoveryManager.cs                         05 GOV
  RuntimeLog.cs                              05
  SettingsWindow.axaml.cs                    04
  SettingsWindow.AiTracking.cs               04
  SpinningWheelSlider.cs                     04
  TextOverlayGenerator.cs                    03
  TimelineKnob.cs                            01
  TimelineLanesControl.axaml.cs              01
  TimelineReorder.cs                         04
  UiStateStore.cs                            05
  UpdateAvailableWindow.axaml.cs             04
⚠ VideoMergerWindow.ClipActions.cs           01 04
⚠ VideoMergerWindow.EdlPreview.cs            01 03 04
⚠ VideoMergerWindow.Effects.cs               01 04
⚠ VideoMergerWindow.History.cs               01 06
⚠ VideoMergerWindow.Lanes.cs                 01 04
⚠ VideoMergerWindow.Session.cs               01 04 05
⚠ VideoMergerWindow.TimelineSelect.cs        01 04
  WindowResizeGrip.cs                        04
  VoiceOverPreviewPlayer.cs                  02
⚠ VoiceOverWindow.axaml.cs                   01 02
  VoiceRecorder.cs                           02
⚠ WindowBoundsHelper.cs                      04 05
  ZoomPreviewSimulator.cs                    03
  FvsBuild Program.cs                        05
⚠ ProjectDocument.cs                         06 07
  ProjectSerializer.cs                       06
  ProjectStore.cs                            06
  RecentProjects.cs                          06
  AotJson.cs                                 06
  UndoStack.cs                               07
⚠ ProjectSession.cs                          06 07 08
⚠ MainWindow.Project.cs                      06 07 08
⚠ ToolNavigator.cs                           05 08
⚠ FfmpegJobLifetime.cs                       03 08
  AppServices.cs                             08
  Fault.cs                                   08
  IFaultSink.cs                              08
  IClock.cs                                  08
⚠ IProjectStore.cs                           06 08
⚠ IUserNotifier.cs                           04 08
  IFilePickerService.cs                      08
⚠ StorageProviderFilePicker.cs               05 08
  UserFacingFaultSink.cs                     08
  ArchitectureRuleTests.cs                   08
⚠ CodeSigning.cs                             05 08
  SettingsManager.cs                         05 (SETTX_01: all settings mutation via Update)
  HardwareTelemetrySampler.cs                04
  AuthenticodeVerifier.cs                    05
  FreeVideoStudio.App.csproj           06 (AOTSAFETY_01) | 05 §5 SYS-SIGNING
  RuntimePayloadManifest.cs                  09
  DiagnosticReport.cs                        05
  DiagnosticBundle.cs                        05
  UndoSidecarStore.cs                        07
  Faults.cs                                  08
  SentinelList.cs                            05
  FvsVerify Program.cs                       05
  build/sentinels.txt                        05
⚠ .github/workflows/ci.yml                   08 09
  .github/workflows/lfs-guard.yml            09
⚠ UpdateService.cs                           05 09
  Staging.cs                                 09
⚠ GranularSpeedEditorWindow.History.cs       04 07
⚠ MainWindow.Controls.cs                     01 02 04 GOV
  GranularSpeedEditorWindow.Controls.cs      04
  MusicWizardWindow.Controls.cs              04
  CropToolWindow.Controls.cs                 04
  VideoMergerWindow.Controls.cs              03
  PhaseOverlayControl.Controls.cs            04
  TimelineLanesControl.Controls.cs           04
  ConfirmDialogWindow.Controls.cs            04
  WindowsOnlyFactAttribute.cs                08
  TimelineViewModel.cs                       01
⚠ MergedTimeline.cs                          01 03
⚠ MusicPadAlignment.cs                       01 02 03
⚠ VideoMergerWindow.Timeline.cs              01 03
  EncoderManager.cs                          03
  TwoPassEncoding.cs                         03
  ExportColorPolicy.cs                       03
  IntroTag.cs                                03
  MergeClipAnalyzer.cs                       03
  GrabCursors.cs                             04
  VideoMergerWindow.Playhead.cs              04
⚠ LaneDiskCache.cs                           04 05
  WaveformPeaks.cs                           04
  CoreLogger.cs                              05
  CropConfigStore.cs                         05
⚠ MainWindow.Recovery.cs                     05 07
  FaultCounters.cs                           08
  .gitattributes                             09
  CooperativeShutdownGate.cs                 05
  CrashLogDigest.cs                          05
  CropConfigDefaults.cs                      05
  DiskSpaceGuard.cs                          05
  HardwareCapability.cs                      03
  HudAutoDetector.cs                         01
  HudConfig.cs                               01
  HudImageOps.cs                             01
  MainViewModel.cs                           04 08
  MemeAssets.cs                              03
  MemeCatalog.cs                             03
  MemePickerWindow.axaml                     04
  MemeWallControl.axaml                      04
  NamedPipeStateClient.cs                    05
  PreviewDetachController.cs                 04
  PreviewMonitorWindow.axaml                 04
  SingleInstanceGuard.cs                     05
  StateTransferStore.cs                      05
  ViewModelBase.cs                           04
  WavAudioReader.cs                          02
  WaveformGenerator.cs                       02
```

Full paths: see each spec's Code Mini-Map.

---

## 2. Symbol / Constant / Tag -> Spec Section

```
_isSafeToClose                               05 §3 SYS-WINSTATE
SIZEESTIMATE_01                              03 FFM-SIZEESTIMATE | 04 UI-THEME | 05 SYS-SIZEESTIMATE
MERGEQUALITY_01                              03 FFM-SIZEESTIMATE
MERGESIZE_01                                 03 FFM-SIZEESTIMATE
MEMELEVEL_01                                 03 §12 FFM-SCRAPER
CLIPFRAMES_01                                03 §12 FFM-SCRAPER | 02 §5 AUD-CONCAT
MemeLoudness                                 03 §12 FFM-SCRAPER
CANCELREG_01                                 03 §8b FFM-EXPORTLIFETIME
EXPORTSESSION_01                             03 §8b FFM-EXPORTLIFETIME
FLUSHCEILING_01                              05 §4d SYS-IPCLIFETIME
FlushMaxWaitMs                               05 §4d SYS-IPCLIFETIME
GPUPRESENT_01                                04 §7b UI-GPUSLOT
GPUSLOT_01                                   04 §7b UI-GPUSLOT
GRANPROBE_01                                 04 §7c UI-GRANOPEN
ImportedImageSlot                            04 §7b UI-GPUSLOT
IPCLEASE_01                                  05 §4d SYS-IPCLIFETIME
IPCTEARDOWN_01                               05 §4d SYS-IPCLIFETIME
OUTPATH_01                                   03 §8b FFM-EXPORTLIFETIME
PIPEDRAIN_01                                 03 §8b FFM-EXPORTLIFETIME
PROCGATE_01                                  03 §8b FFM-EXPORTLIFETIME
PROCGATE_02                                  03 §8b FFM-EXPORTLIFETIME
ProcessVideoCoreAsync                        03 §8b FFM-EXPORTLIFETIME
SETTINGSATOMIC_01                            05 §4c SYS-ATOMICWRITE
TryRetireSlot                                04 §7b UI-GPUSLOT
WORKERLIFETIME_01                            03 §8b FFM-EXPORTLIFETIME
WORKERLIFETIME_02                            03 §8b FFM-EXPORTLIFETIME
_exportRunning                               03 §8b FFM-EXPORTLIFETIME
_presentGates                                04 §7b UI-GPUSLOT
_procGate                                    03 §8b FFM-EXPORTLIFETIME
_lastFreezeTriggerMs                         01 §4 TL-FREEZE
_mainEndParkIssued                           01 §8 TL-ENDSTOP
_previewParkedAtEnd                          01 §8 TL-ENDSTOP
_recalculatingTrackColumns                   04 §2 UI-DPI
_timeline                                    01 (mini-map only)
AiSetupWizardWindow                          04 §13 UI-AIZOOM
AiSmartZoomBtnCtl                            04 §13 UI-AIZOOM
AiSubjectPickerWindow                        04 §13 UI-AIZOOM
AiTrackingPreviewBarCtl                      04 §13 UI-AIZOOM
AiTrajectorySmoother                         01 §11 TL-AITRACKING | 03 §2 FFM-ZOOMGRAPH
AmbientBubblesBackground                     04 (mini-map only)
AppContext.BaseDirectory                     03 §9 FFM-BINPATH
AppDangerBrush                               04 §1 UI-THEME
ApplicationPaths                             05 (mini-map only)
ApplyFirstRunDisplayFit                      05 §3 SYS-WINSTATE
ApplyProfile                                 05 (mini-map only)
ApplyTrackFilterAndSort                      02 §6 AUD-DIALOGS
AppPrimaryButtonGradient                     04 §1 UI-THEME
AppTableHairlineBrush                        04 §1 UI-THEME
AppZoomBrush                                 01 §6 TL-ZOOM | 04 §1 UI-THEME
AskEditOrRemoveAsync                         02 §6 AUD-DIALOGS | 04 (mini-map only)
ATOMICTEXT_01                                05 §4c SYS-ATOMICWRITE
AtomicJsonFile                               05 §4 SYS-RECOVERY
AtomicJsonFile.WriteText                     05 §4c SYS-ATOMICWRITE
AudioFilterChain                             02 §7 AUD-MUSICFADE
AudioLoudnessProbe                           02 §2 AUD-MASTERING
BeginMoveDrag                                04 §7 UI-BORDERLESS
BillableSeconds                              03 §8a FFM-QUALITY
BlockingCollection<string>                   05 §2 SYS-LOGGING
BubbleCount                                  04 §7 UI-BORDERLESS  [= 35]
Build                                        01 §1 TL-PORTRAIT | 03 (mini-map only) | 05 §5 SYS-SIGNING
BuildAtempoChain                             03 (mini-map only)
CalculateEffectiveDurationMs                 03 §8a FFM-QUALITY
CalculateFreezeOutputMs                      03 §8a FFM-QUALITY
CanvasMath                                   01 (mini-map only)
CheckFault                                   05 §4 SYS-RECOVERY
ChunkSpec                                    03 (mini-map only)
ClampZoomInsideItsBlock                      01 (mini-map only)
ClearLiveZoomCrop                            04 §6 UI-GRANULAR   [code-only: counterpart of UpdateLiveZoomCrop]
CoachOverlay                                 04 §5 UI-COACH
CoachTours                                   04 §5 UI-COACH
Compute                                      03 (mini-map only)
ConfirmDialogWindow                          02 §6 AUD-DIALOGS | 04 (mini-map only)
CoordinateMath                               01 §1 TL-PORTRAIT
CoreLogger                                   05 (mini-map only)
Create                                       01 (mini-map only)
Cut                                          01 §2 TL-OUTPUTTIMELINE, 01 §5 TL-CUTS | 02 §2 AUD-MASTERING, 02 §4 AUD-VOICEOVER | 04 §4 UI-SAFEGUARDS
CUTS_02                                      02 §4 AUD-VOICEOVER
DebounceMs                                   03  [= 450]   [code-only: FilmstripPrewarm.cs]
DefaultValues.QualityIndex                   03 §8a FFM-QUALITY
DeploymentLifecycle                          05 §1 SYS-MUTEX
DOUBLEFIRE_01                                04 §4 UI-SAFEGUARDS
EnableGlobalRipple                           04 §1 UI-THEME
EndThumbnailMarkerDrag                       01 §9 TL-HITBOX
EndUndoGesture                               01 (mini-map only)
EnsureDefaults                               05 (mini-map only)
EnsureStep2WaveformPresent                   02 §6 AUD-DIALOGS
EnsureWritableDirectories                    05 (mini-map only)
ExportPayload                                03   [code-only: ExportPayload.cs]
FFM-QUALITY                                  03 §8a FFM-QUALITY
FfmpegDiagnosticCollector                    03 (mini-map only)
File.Move                                    05 §4 SYS-RECOVERY
FileOptions.WriteThrough                     05 §4 SYS-RECOVERY
FIRSTFIT_01                                  05 §3 SYS-WINSTATE
FirstRunAspect                               05 §3 SYS-WINSTATE
FirstRunCoverage                             05 §3 SYS-WINSTATE
fitDisplayOnFirstRun                         05 §3 SYS-WINSTATE
FloatingNotice                               04 §5 UI-COACH
FluidVolumeSlider                            02 (mini-map only) | 04 (mini-map only)
FormatDiagnosticReport                       03   [code-only: ExportFailure.cs]
FREEZE_01                                    01 §4 TL-FREEZE
FreezeSecondCostFactor                       03 §8a FFM-QUALITY
FVS_DEV_LOG_DIR                              05 §4a SYS-DEVBUILD
FVS_SIGN_PASS                                05 §5 SYS-SIGNING
FVS_SIGN_PFX                                 05 §5 SYS-SIGNING
GeneratePng                                  03 (mini-map only)
GeminiTrackingService                        01 §11 TL-AITRACKING | 03 §2 FFM-ZOOMGRAPH | 04 §13 UI-AIZOOM | 05 §6 SYS-AISETTINGS
GetQualitySettings                           03 §8a FFM-QUALITY
GlobalMasterVolume                           02 §1 AUD-MASTERVOL
GpuCapabilityProbe                           03 §1 FFM-HWENC
GranularSpeedBuilder                         03 (mini-map only) | 05 §3 SYS-WINSTATE
GranularSpeedEditorWindow                    01 (mini-map only) | 04 (mini-map only) | 05 (mini-map only)
GRIP_01                                      04 §7a UI-RESIZEGRIP
HardwareScanner                              03 §1 FFM-HWENC
HasUnsavedWork                               05 (mini-map only)
HudAutoDetector                              01 §12 TL-HUDSTREAMING
InsertionAt                                  01 (mini-map only)
IsActive                                     02 §4 AUD-VOICEOVER | 03 §5 FFM-MEMEPREVIEW
IsEof                                        02 §2a AUD-IPCSTATE
IsMainPlayerCharacter                        01 §11 TL-AITRACKING | 04 §13 UI-AIZOOM
IsMainPreviewAtEnd                           01 §8 TL-ENDSTOP
IsSafeModeActive                             05 (mini-map only)
KineticScrubController                       01 (mini-map only)
LAYOUT_03                                    02 §6 AUD-DIALOGS
LIST_05                                      02 §6 AUD-DIALOGS
LIST_06                                      04 §2 UI-DPI
LIST_07                                      02 §6 AUD-DIALOGS
LoadState                                    05 (mini-map only)
LogMutexName                                 05 (mini-map only)
MAINEND_01                                   01 §8 TL-ENDSTOP
MainTimelineEndSeconds                       01 §8 TL-ENDSTOP
MainWindow                                   01 §2 TL-OUTPUTTIMELINE | 02 (mini-map only) | 04 (mini-map only) | 05 §3 SYS-WINSTATE
MaskOverlayManager                           05 (mini-map only)
MaxUndoDepth                                 04 §6 UI-GRANULAR  [= 40]
MEME_06                                      01 §7 TL-MEME
MEME_07                                      02 §4 AUD-VOICEOVER | 03 §5 FFM-MEMEPREVIEW
MemePreviewDirector                          02 §4 AUD-VOICEOVER | 03 §5 FFM-MEMEPREVIEW
MemeRebuildOverlay                           03 §5 FFM-MEMEPREVIEW
MemeSwapOverlay                              03 §5 FFM-MEMEPREVIEW
MergerWorker                                 03 (mini-map only)
MicLevelMonitor                              02 §4 AUD-VOICEOVER
MinHeight                                    04 §2 UI-DPI | 05 §3 SYS-WINSTATE
MinWidth                                     04 §2 UI-DPI | 05 §3 SYS-WINSTATE
MixFader                                     04 §1 UI-THEME
MobileFilterBuilder                          01 §1 TL-PORTRAIT | 03 (mini-map only)
MPVEOF_01                                    02 §2a AUD-IPCSTATE
MPVSHUTDOWN_01                               05 §3 SYS-WINSTATE
MpvIpcClient                                 02 §1 AUD-MASTERVOL | 03 §9 FFM-BINPATH
MusicVolSlider                               02 §6 AUD-DIALOGS
MusicWizardWindow                            01 (mini-map only) | 02 (mini-map only)
NormalizeCuts                                01 (mini-map only)
Notify                                       08 (mini-map only)
NotifyError                                  04   [code-only: GranularSpeedEditorWindow.axaml.cs]
ObserveProperty                              02 (mini-map only)
OnClosed                                     05 §3 SYS-WINSTATE
OnClosing                                    05 §3 SYS-WINSTATE
OnPointerMoved                               02 (mini-map only) | 04 (mini-map only)
OnPointerPressed                             02 | 04   [code-only: FluidVolumeSlider.cs, SpinningWheelSlider.cs]
OnPointerReleased                            02 | 04   [code-only: FluidVolumeSlider.cs, SpinningWheelSlider.cs]
OnPointerWheelChanged                        04 (mini-map only)
OnTick                                       01 (mini-map only)
OnTrackSelected                              02 §6 AUD-DIALOGS
OnVolumeChanged                              02   [code-only: VoiceOverWindow.axaml.cs]
OnlineFrameAccumulator                       01 §12 TL-HUDSTREAMING
OutputTimeline                               01 §2 TL-OUTPUTTIMELINE | 02 §6 AUD-DIALOGS
OverlayCanvas                                04 §5 UI-COACH
P3ASYNC_01                                   02 §6 AUD-DIALOGS
PhoneFrameMockup                             01 §1 TL-PORTRAIT | 04 (mini-map only)
PostWithRetryAsync                           05 §6 SYS-AISETTINGS
ProcessWorker                                03 §9 FFM-BINPATH
ProgramDataRoot                              05 (mini-map only)
ProjectRecoveryService                       05 (mini-map only)
PushUndo                                     04 (mini-map only)
QUALITY_01                                   03 §8a FFM-QUALITY
QUALITY_02                                   03 §8a FFM-QUALITY
QUALITY_03                                   03 §8a FFM-QUALITY
QUALITY_04                                   04 §4 UI-SAFEGUARDS
QUALITY_05                                   03 §8a FFM-QUALITY
QualityLabel                                 04 §4 UI-SAFEGUARDS
QualityLadder                                03 §8a FFM-QUALITY
QualitySliderValue                           03 §8a FFM-QUALITY
QuantizeItemSize                             01 §1 TL-PORTRAIT
QuietBoostReductionFactor                    02 §2 AUD-MASTERING  [= 0.70]
ReadInt                                      05 (mini-map only)
ReadObject                                   05 (mini-map only)
ReadString                                   06   [code-only: ProjectSerializer.cs]
RecoveryManager                              05 §4 SYS-RECOVERY
RecoveryStateFile                            05 (mini-map only)
Redo                                         04 §6 UI-GRANULAR
Register                                     04 (mini-map only)
RelayoutFrameLane                            01 (mini-map only)
Reload                                       02 (mini-map only)
ReportMicHealth                              02 §4 AUD-VOICEOVER
ResizeGrip                                   04 §7a UI-RESIZEGRIP
ResolveHostPanel                             04 §5 UI-COACH
ResultSegments                               05 §3 SYS-WINSTATE
RESUME_01                                    02 §6 AUD-DIALOGS
ResumeFromInitialStateAsync                  02 §6 AUD-DIALOGS
RewindFromTimelineEnd                        01 §8 TL-ENDSTOP
RotateBackupsUnlocked                        05 §4 SYS-RECOVERY
RuntimeLog                                   05 (mini-map only)
SampleRate                                   04 (mini-map only)  [= 4000, WaveformPeaks]
SaveBoundsSync                               04 (mini-map only) | 05 (mini-map only)
SaveRecoveryState                            02 (mini-map only) | 05 SYS-EDITHOT | 07 §6 UNDO-EQUALITY
SaveState                                    05 (mini-map only)
CurrentSchemaVersion                         05 (mini-map only)  [= 10, SettingsManager]
SEAM_01                                      01 §3 TL-MARKERS | 04 §6 UI-GRANULAR
SEEKSTORM_01                                 04 §6 UI-GRANULAR
SETTX_01                                     05 §4c (settings transaction API — Update/SetAutoUpdateChecks; mutation guard)
SegDragMode                                  01 (mini-map only)
SelectSegment                                01 (mini-map only)
SerializeState                               05 (mini-map only)
SessionStateFile                             05 (mini-map only)
SetButtonText                                04 (mini-map only)
SetGlobalMasterVolume                        02 §1 AUD-MASTERVOL
SizeToContent                                04 §2 UI-DPI
SLIDER_06                                    02 §6 AUD-DIALOGS
SLIDER_07                                    04 §1 UI-THEME | 02 §6 AUD-DIALOGS
SnapInsertionPoint                           01 §7 TL-MEME
SourceMsToOutputSeconds                      01 §2 TL-OUTPUTTIMELINE
SourceToOutput                               01 §7 TL-MEME
SpinningWheelSlider                          04 (mini-map only)
SPLICE_01                                    03 §3 FFM-CONCAT
SPLICE_02                                    03 §3 FFM-CONCAT
StartRecording                               02 (mini-map only)
StopRecording                                02 (mini-map only)
SurvivingSourceWidth                         01 §1 TL-PORTRAIT  [= 720]   [formula term, not a code symbol]
SYS-DEVBUILD                                 05 §4a SYS-DEVBUILD
Tactile                                      04 §1 UI-THEME
TargetLufs                                   02 (mini-map only)  [= -14.0]
TargetMbFor                                  03 §8a FFM-QUALITY
TextOverlayGenerator                         03 (mini-map only)
THUMB_01                                     01 §9 TL-HITBOX
THUMB_02                                     01 §9 TL-HITBOX
TimelineKnob                                 01 (mini-map only)
TimelineLanesControl                         01 (mini-map only)
TimelineStartSeconds                         01 (mini-map only)
CurrentTime                                  02 §2a AUD-IPCSTATE | 02 §4 AUD-VOICEOVER
TL-ENDSTOP                                   01 §8 TL-ENDSTOP
TogglePlayPauseTransport                     01 §8 TL-ENDSTOP
ToWorkerQualityLevel                         03 §8a FFM-QUALITY
Track                                        04 (mini-map only) | 05 §3 SYS-WINSTATE
TrackScrollGutterPx                          02 §6 AUD-DIALOGS
TrackTableWidth                              02 §6 AUD-DIALOGS
TRANSPORT_TRACE_01                           05 §4a SYS-DEVBUILD
TransportToggleCoalesceMs                    04 §4 UI-SAFEGUARDS
TransportTrace                               05 §4a SYS-DEVBUILD
TryExecutePlayPause                          04 §4 UI-SAFEGUARDS
UI-RESIZEGRIP                                04 §7a UI-RESIZEGRIP
UiStateStore                                 04 §5 UI-COACH | 05 (mini-map only)
Undo                                         04 §6 UI-GRANULAR
Uninstall                                    05 §1 SYS-MUTEX
UpdateEstimatedQuality                       03 §8a FFM-QUALITY
UpdateReadyLamp                              02 §4 AUD-VOICEOVER
UpdateThumbnailButtonState                   01 (mini-map only)
UpdateTimelineMarkers                        01 (mini-map only)
VERIFY_PATCHES                               05 §4a SYS-DEVBUILD
VideoVolSlider                               02 §6 AUD-DIALOGS
VOEND_01                                     01 §8 TL-ENDSTOP
VoiceOverPreviewPlayer                       02 §4 AUD-VOICEOVER
VoiceOverWindow                              01 (mini-map only) | 02 (mini-map only)
VoiceRecorder                                02 §4 AUD-VOICEOVER
VolumeChanged                                02   [code-only: VoiceRecorder.cs, VoiceOverWindow.axaml.cs]
VolumeSlider                                 02 (mini-map only)
VOMON_02                                     02 §4 AUD-VOICEOVER
WindowBoundsHelper                           04 §8 UI-DETACH | 05 §3 SYS-WINSTATE
WindowResizeGrip                             04 §7a UI-RESIZEGRIP
WINSEED_01                                   05 §3 SYS-WINSTATE
WorkerConstantQualityLevel                   03 §8a FFM-QUALITY
WrapText                                     03 (mini-map only)
WriteInt                                     05 (mini-map only)
WriteObject                                  05 §4 SYS-RECOVERY
ZOOMLIVE_07                                  04 §6 UI-GRANULAR
ZOOMCARD_01                                  04 §6 UI-GRANULAR
ZOOMANTS_01                                  04 §6 UI-GRANULAR
ZOOMANTS_02                                  04 §6 UI-GRANULAR
ZoomBandThicknessPx                          04 §6 UI-GRANULAR  [= 2.5]
ZoomAntsBrush                                04 §6 UI-GRANULAR
_marchingAntsOffset                          04 §6 UI-GRANULAR
ZOOMSTYLE_02                                 04 §6 UI-GRANULAR   [spec-only: no in-code tag]
ZOOMPREVIEW_01                               04 §6 UI-GRANULAR   [spec-only: no in-code tag]
ZOOMCOMMIT_01                                04 §6 UI-GRANULAR   [spec-only: no in-code tag]
CheckManualAsync                             05 §6 SYS-AUTOUPDATE
ShowAboutAsync                               04 §11 UI-SETTINGS-ABOUT
GetCurrentVersion                            05 §6 SYS-AUTOUPDATE
TryParseVersion                              05 §6 SYS-AUTOUPDATE
SynchronizeVersionFiles                      05 §6 SYS-AUTOUPDATE
ZoomPreviewSimulator                         02 §4 AUD-VOICEOVER | 03 (mini-map only)
ZoomRampSeconds                              03  [= 0.5]   [code-only: GranularSpeedBuilder.cs, ZoomPreviewSimulator.cs]
AOTSAFETY_01                                 06 §4 PROJ-AOT
EndGesture                                   07 §2 UNDO-RULES
GestureIdleMs                                07 §2 UNDO-RULES  [= 900]
U1                                           07 §2 UNDO-RULES
U2                                           07 §2 UNDO-RULES
U3                                           07 §2 UNDO-RULES
U4                                           07 §2 UNDO-RULES
UNDO_10                                      07 §1 UNDO-WHY | 07 §3 UNDO-STATE   [code-only: UndoStack.cs]
UNDO_11                                      07 §2 UNDO-RULES   [code-only: UndoStack.cs — U2 ceiling]
UndoEntry                                    07 §4 UNDO-PERSIST
UndoStack                                    07 §2 UNDO-RULES
AOTSAFETY_02                                 06 §4 PROJ-AOT
AOTSAFETY_04                                 06 §4 PROJ-AOT
AOTSAFETY_05                                 06 §4 PROJ-AOT | 05 §5 SYS-SIGNING
AddNode                                      06 §4 PROJ-AOT
AotJson                                      06 §4 PROJ-AOT
IndentedContext                              06 §4 PROJ-AOT
BackupSuffix                                 06 §5 PROJ-DISK
BuildTimeline                                06 §2 PROJ-INPUTS
CheckSource                                  06 §6 PROJ-INTEGRITY
EffectiveDurationMs                          06 §2 PROJ-INPUTS
MinimumReadableSchemaVersion                 06 §3 PROJ-SCHEMA
PROJ_01                                      06 §2 PROJ-INPUTS
PROJ_02                                      06 §3 PROJ-SCHEMA | 06 §4 PROJ-AOT
PROJ_03                                      06 §3 PROJ-SCHEMA
PROJ_04                                      06 §2 PROJ-INPUTS
PROJ_05                                      06 §6 PROJ-INTEGRITY
PROJ_08                                      06 §5 PROJ-DISK
PROJ_09                                      06 §7 PROJ-RECENT
ProjectDocument                              06 §2 PROJ-INPUTS
ProjectIoResult                              06 §5 PROJ-DISK
ProjectSerializer                            06 §4 PROJ-AOT
ProjectStore                                 06 §5 PROJ-DISK
RecentProjects                               06 §7 PROJ-RECENT
SchemaVersion                                06 §3 PROJ-SCHEMA
SourceClip                                   06 §6 PROJ-INTEGRITY
SourceIntegrity                              06 §6 PROJ-INTEGRITY
SuppressAotAnalysisWarnings                  06 §4 PROJ-AOT
SuppressTrimAnalysisWarnings                 06 §4 PROJ-AOT
UnknownFields                                06 §3 PROJ-SCHEMA
ARCHTEST_01                                  08 §3 COMP-ARCHTEST
ASYNCUI_01                                   08 §3 COMP-ARCHTEST
ASYNCUI_02                                   08 §3 COMP-ARCHTEST
BATCHPARENS_01                               05 §4a SYS-DEVBUILD
LISTCOMMENT_01                               05 §4a SYS-DEVBUILD
BATCHPARENS_02                               05 §4a SYS-DEVBUILD
COMPOSITION_01                               08 §1 COMP-ROOT
COMPOSITION_02                               08 §1 COMP-ROOT
FAULTSTORM_01                                08 §2 COMP-FAULTS
FAULTTIER_01                                 08 §2 COMP-FAULTS
INJSEAM_01                                   08 §1 COMP-ROOT   [IProjectStore]
INJSEAM_02                                   08 §1 COMP-ROOT   [IClock]
INJSEAM_03                                   08 §1 COMP-ROOT   [IUserNotifier]
INJSEAM_04                                   08 §1 COMP-ROOT   [IFilePickerService]
MVVM_01                                      08 §3a COMP-MVVM
MVVM_02                                      08 §3a COMP-MVVM
PICKERMEMORY_01                              08 (mini-map only) | 05 §3 SYS-WINSTATE
PIPELIFE_01                                  03 (mini-map only)
PIPELIFE_02                                  03 | 08   [code-only: FfmpegJobLifetime.cs, MergerWorker.cs]
PROJSESSION_01                               08 (mini-map only)
PROJSESSION_02                               06   [code-only: ProjectSession.cs]
PROJSESSION_03                               06 | 05 §4 SYS-RECOVERY   [code-only: ProjectSession.cs]
PROJSESSION_04                               06 §8 PROJ-TODO | 07 §5 UNDO-TODO
PROJSESSION_05                               06 §8 PROJ-TODO
PROJSESSION_06                               07 §3 UNDO-STATE   [code-only: MainWindow.Project.cs]
PROJSESSION_07                               05 §3 SYS-WINSTATE
PROJSESSION_08                               06 §9 PROJ-CLOSEGUARD
PROJSESSION_09                               06 §9 PROJ-CLOSEGUARD
SCRIM_01                                     04 §1 UI-THEME
SIGNMANDATE_01                               05 §5 SYS-SIGNING
TOOLNAV_01                                   08 (mini-map only)
TOOLNAV_02                                   05 §3 SYS-WINSTATE
TOOLNAV_03                                   05 §3 SYS-WINSTATE
TOOLNAV_04                                   05 §3 SYS-WINSTATE   [code-only: ToolNavigator.cs, CropToolWindow.axaml.cs, VideoMergerWindow.axaml.cs]
UNDO_20                                      07 §2 UNDO-RULES
UNDO_21                                      07 §3 UNDO-STATE   [code-only: ProjectSession.cs]
UNDO_22                                      04 §6 UI-GRANULAR   [code-only: MainWindow.Project.cs]
UPDATETRUST_02                               05 §5 SYS-SIGNING
VERIFYHALT_01                                05 §4a SYS-DEVBUILD
SYS-VERIFYTOOL                               05 SYS-VERIFYTOOL
SYS-CI                                       08 COMP-CI | 09 §5 DIST-CIWATCH
SYS-DIAGREPORT                               05 SYS-DIAGREPORT
SYS-PAYLOADSPLIT                             09 §3 DIST-SPLIT | 05 SYS-PAYLOADSPLIT
SYS-REPOWEIGHT                               09 §4 DIST-REPOWEIGHT
DIST-AGENTS                                  09 §0 DIST-AGENTS | GOV §0a
CITEST_01                                    08 COMP-CI
FAULTTIER_02                                 08 COMP-FAULTCHANNEL
MVVM_03                                      08 §3 COMP-ARCHTEST | 01 (mini-map only)
UNDO_23                                      07 §5 UNDO-TODO
UNDO_24                                      07 §5 UNDO-TODO | 07 §4 UNDO-PERSIST
UNDO_25                                      07 §5 UNDO-TODO | 04 §6 UI-GRANULAR
PROJ_10                                      06 §8 PROJ-TODO
PROJ_11                                      06 §8 PROJ-TODO
PROJ_12                                      06 §8 PROJ-TODO
AOTSAFETY_06                                 06 §4 PROJ-AOT   [code-only: ProjectSerializer.cs, UndoSidecarStore.cs]
ProjectMask                                  06 §8 PROJ-TODO
ProjectMerge                                 06 §8 PROJ-TODO | 05 §4 SYS-RECOVERY
MergeClip                                    06 §8 PROJ-TODO   [code-only: ProjectDocument.cs]
RuntimePayloadManifest                       09 §3 DIST-SPLIT
UndoSidecarStore                             07 §4 UNDO-PERSIST
DiagnosticReport                             05 SYS-DIAGREPORT
Faults.Install                               08 §2 COMP-FAULTS
AOTSAFETY_03                                 06 §10 PROJ-AOTPOLICY
AOTCLEAN_01                                  06 §10 PROJ-AOTPOLICY
AOTCLEAN_02                                  06 §10 PROJ-AOTPOLICY
AOTCLEAN_03                                  06 §10 PROJ-AOTPOLICY | 04 UI-GPUPRESENT2
AOTCLEAN_04                                  06 §10 PROJ-AOTPOLICY
WavAudioReader                               06 §10 PROJ-AOTPOLICY
D3D11Interop                                 06 §10 PROJ-AOTPOLICY
SIGNLOCAL_01                                 05 §5 SYS-SIGNING
SIGNLOCAL_02                                 05 §5 SYS-SIGNING
ILCCRASH_01                                  06 §10 PROJ-AOTPOLICY
RELEASEASSETS_01                             09 §3 DIST-SPLIT
GPUPRESENT_02                                04 §12 UI-GPUPRESENT2
TryAcquireProducerKey                        04 §12 UI-GPUPRESENT2
EDITHOT_01                                   05 SYS-EDITHOT
EDITHOT_02                                   05 SYS-EDITHOT
LiveMaskCache                                05 SYS-EDITHOT
MainWindow.Recovery.cs                       05 SYS-EDITHOT | 07 UNDO-EQUALITY
UNDOEQ_01                                    07 §6 UNDO-EQUALITY
UNDOEQ_02                                    07 §6 UNDO-EQUALITY
MUSICSYNC_01                                 02 §8 AUD-PREVIEWSYNC
MUSICPAD_01                                  02 §8 AUD-PREVIEWSYNC
MusicPadAlignment                            02 §8 AUD-PREVIEWSYNC | 03 §6 FFM-FADES
MUSICSYNC_02                                 02 §8 AUD-PREVIEWSYNC
MusicBedPlan                                 02 §8 AUD-PREVIEWSYNC
PreviewAudioSync                             02 §8 AUD-PREVIEWSYNC
PreviewSourceToOutputSeconds                 02 §8 AUD-PREVIEWSYNC
COLOR_01                                     03 §11 FFM-COLOR
ExportColorPolicy                            03 §11 FFM-COLOR
VideoColorInfo                               03 §11 FFM-COLOR
LOGVIS_01                                    08 COMP-LOGVIS
FaultCounters                                08 COMP-LOGVIS
WarnThrottled                                08 COMP-LOGVIS
WRITEORDER_01                                05 SYS-WRITEORDER
WRITEORDER_02                                05 SYS-WRITEORDER
USERSCOPE_01                                 05 SYS-USERSCOPE
DefaultUserRoot                              05 SYS-USERSCOPE
UserScopedName                               05 SYS-USERSCOPE
SCRAPER_01                                   03 §12 FFM-SCRAPER
TIMINGTAG_02                                 03 §12 FFM-SCRAPER
FRAMESNAP_01                                 03 §12 FFM-SCRAPER
ExportTimingTag                              03 §12 FFM-SCRAPER
SCRAPER_02                                   03 §12 FFM-SCRAPER
SCRAPER_03                                   03 §12 FFM-SCRAPER
SCRAPER_04                                   03 §12 FFM-SCRAPER
SCRAPER_05                                   03 §12 FFM-SCRAPER
IntroTag                                     03 §12 FFM-SCRAPER
MergedTimeline                               03 §12 FFM-SCRAPER
MergeClipAnalyzer                            03 §12 FFM-SCRAPER
MergerThumbnailScraper                       03 §12 FFM-SCRAPER
MERGEEDL_01                                  01 §10 TL-COMPOSITE
EDLNULL_01                                   01 §10 TL-COMPOSITE
RESTOREMISS_01                               05 §4 SYS-RECOVERY
MergeEdl                                     01 §10 TL-COMPOSITE
COMPOSITE_01                                 01 §10 TL-COMPOSITE
MERGESESSION_01                              05 §4 SYS-RECOVERY
MergerAutosaveStore                          05 §4 SYS-RECOVERY
MergerSession                                05 §4 SYS-RECOVERY
ClipIdList                                   05 §4 SYS-RECOVERY
MERGEUNDO_01                                 06 §8 PROJ-TODO | 07 §2 UNDO-RULES
LANES_01                                     04 §9 UI-MERGERQUEUE
LANECACHE_02                                 04 §9 UI-MERGERQUEUE
ThumbGrid                                    04 §9 UI-MERGERQUEUE
WaveformPeaks                                04 §9 UI-MERGERQUEUE
LaneDiskCache                                04 §9 UI-MERGERQUEUE | 05 (mini-map only)
REMOVEUX_01                                  04 §9 UI-MERGERQUEUE | 04 §4 UI-SAFEGUARDS | 05 §6 SYS-AUTOUPDATE
EMPTYQUEUE_01                                04 §9 UI-MERGERQUEUE
LanePlanner                                  04 §9 UI-MERGERQUEUE
ProgressiveLaneRunner                        04 §9 UI-MERGERQUEUE
ANTS_01                                      04 §9 UI-MERGERQUEUE
TimelineReorder                              04 §9 UI-MERGERQUEUE
CompositeTimeline                            01 §10 TL-COMPOSITE | 02 §5 AUD-CONCAT
MERGEEDIT_01                                 01 §10 TL-COMPOSITE
MergeEditorSource                            01 §10 TL-COMPOSITE
MERGEPREVIEW_EDL_01                          01 §10 TL-COMPOSITE | 04 §9 UI-MERGERQUEUE
MERGEPREVIEW_01                              01 §10 TL-COMPOSITE | 04 §9 UI-MERGERQUEUE
MergerPreviewPlan                            01 §10 TL-COMPOSITE
MERGEEDIT_02                                 04 §9 UI-MERGERQUEUE
CLIPACTIONS_01                               04 §9 UI-MERGERQUEUE
MERGERUX_01                                  04 §9 UI-MERGERQUEUE
MERGERPLAYHEAD_01                            04 §9 UI-MERGERQUEUE
AppPlayheadBrush                             04 §9 UI-MERGERQUEUE
AntsThickness                                04 §9 UI-MERGERQUEUE
GRABCURSOR_01                                04 §9 UI-MERGERQUEUE
GrabCursors                                  04 §9 UI-MERGERQUEUE
FramePtsProbe                                03 §12 FFM-SCRAPER
MERGEGRAPH_01                                03 §12 FFM-SCRAPER
MergeClipGraph                               03 §12 FFM-SCRAPER
MUSICMAP_01                                  03 §12 FFM-SCRAPER | 02 §5 AUD-CONCAT
OUTTAG_01                                    03 §12 FFM-SCRAPER
OutputToSourceRelative                       01 §2 TL-OUTPUTTIMELINE
CoordinateConstants                          01 (mini-map only)
MaxZoomUpscale                               01 §6 TL-ZOOM  [= 8]
ReplaceCurrent                               07 (mini-map only) | 06 §8 PROJ-TODO
NextUndoLabel                                07 (mini-map only) | 07 §5 UNDO-TODO
NextRedoLabel                                07 (mini-map only) | 07 §5 UNDO-TODO
TimePosChanged                               02 §4 AUD-VOICEOVER
MusicBedLufs                                 02 §2 AUD-MASTERING  [= TargetLufs = -14.0]
DUCKOFF_01                                   02 §3 AUD-SIDECHAIN
carving_enabled                              02 §3 AUD-SIDECHAIN
VOPROT_01                                    02 §4 AUD-VOICEOVER
AutoVoiceNormalization                       02 §4 AUD-VOICEOVER
granularTimeMapper                           02 §4 AUD-VOICEOVER
StreamGeometry                               02 §4 AUD-VOICEOVER
FITEND_01                                    02 §6 AUD-DIALOGS
SLIDER_08                                    02 §6 AUD-DIALOGS | 04 §1 UI-THEME
SLIDER_09                                    02 §6 AUD-DIALOGS | 04 §1 UI-THEME
ISSUE_04                                     02 §7 AUD-MUSICFADE
CrossfadeOutSec                              02 §7 AUD-MUSICFADE
CrossfadeInSec                               02 §7 AUD-MUSICFADE
EdgeFadeSec                                  02 §7 AUD-MUSICFADE
KeepMusicDuringMeme                          02 §7 AUD-MUSICFADE
EncoderManager                               03 §1 FFM-HWENC
TwoPassEncoding                              03 §1 FFM-HWENC
BinaryPathResolver                           03 §9 FFM-BINPATH
ThumbnailStripGenerator                      03 §8 FFM-THUMBSTRIP | 04 §9 UI-MERGERQUEUE
InstallerGateName                            05 §1 SYS-MUTEX
RunUninstallWorkerAsync                      05 §1 SYS-MUTEX
EveryFixSentinelStillResolves                05 SYS-VERIFYTOOL | 08 §3 COMP-ARCHTEST
DevCmdDelegatesTheSentinelCheckRatherThanParsingIt  05 SYS-VERIFYTOOL | 08 §3 COMP-ARCHTEST
EveryCatchBlockReportsSomewhere              08 COMP-FAULTCHANNEL | 08 §3 COMP-ARCHTEST
R9                                           09 §3 DIST-SPLIT | 05 SYS-PAYLOADSPLIT
PROJ_06                                      06 §2 PROJ-INPUTS   [code-only: ProjectDocument.cs]
PROJ_07                                      07 §3 UNDO-STATE   [code-only: ProjectDocument.cs]
SWITCHPROMPT_01                              06 §9 PROJ-CLOSEGUARD
LAYOUTLOOP_01                                01   [code-only: TimelineLanesControl.axaml.cs]
ZOOMSIZE_02                                  01   [code-only: TimelineLanesControl.axaml.cs]
LAYOUTLOOP_02                                04 §6 UI-GRANULAR   [code-only: GranularSpeedEditorWindow.axaml.cs — same defect as LAYOUTLOOP_01]
EDGEGUARD_01                                 01 §3 TL-MARKERS   [code-only: GranularSpeedEditorWindow.axaml.cs]
DRAGCOST_01                                  04 §6 UI-GRANULAR   [code-only: GranularSpeedEditorWindow.axaml.cs]
TRACEFLOOD_01                                05 §4a SYS-DEVBUILD   [code-only: Program.cs]
LAYOUT_01                                    04   [code-only: CropToolWindow.axaml]
GATE_01                                      04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
ZOOM_01                                      04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
CROPZOOMRESET_01                             04 §4 UI-SAFEGUARDS
CROPSAVEPROMPT_02                            04 §4 UI-SAFEGUARDS
CROPUNSAVED_01                               04 §4 UI-SAFEGUARDS | 06 §5 PROJ-DISK
CROPFIRSTBOOT_01                             05 §4 SYS-RECOVERY
FORTNITEDEFAULT_02                           05 §4 SYS-RECOVERY
CROPFALLBACK_02                              05 §4 SYS-RECOVERY
SPECTATINGDEFAULT_01                         04 §4 UI-SAFEGUARDS
NO_BOSS_HP_01                                04 §4 UI-SAFEGUARDS
SAVECONFIRM_01                               04   [code-only: CropToolWindow.axaml.cs]
MAGICWAND_01                                 04   [code-only: CropToolWindow.axaml.cs]
DELETEBTN_01                                 04   [code-only: CropToolWindow.axaml.cs]
DELETESET_01                                 04   [code-only: CropToolWindow.axaml.cs]
CROPCANVAS_01                                04   [code-only: CropToolWindow.axaml.cs]
AUTOZOOM_01                                  04   [code-only: CropToolWindow.axaml.cs]
WHEELZOOM_01                                 04   [code-only: CropToolWindow.axaml.cs]
PAN_01                                       04   [code-only: CropToolWindow.axaml.cs]
BEZEL_01                                     04   [code-only: CropToolWindow.axaml]
SPLIT_01                                     04   [code-only: CropToolWindow.axaml]
LAYERSPANE_01                                04   [code-only: CropToolWindow.axaml]
ROLEPOPUP_01                                 04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
PLAYICON_01                                  04   [code-only: CropToolWindow.axaml]
TICKRULER_01                                 04   [code-only: CropToolWindow.axaml.cs]
AUTOPLAY_01                                  04   [code-only: CropToolWindow.axaml.cs]
ITEMMENU_01                                  04   [code-only: CropToolWindow.axaml.cs]
ITEMHIT_01                                   04   [code-only: CropToolWindow.axaml.cs]
GHOSTKILL_01                                 04   [code-only: CropToolWindow.axaml.cs]
CANCELSEL_01                                 04   [code-only: CropToolWindow.axaml.cs]
BACKTOVIDEO_01                               04   [code-only: CropToolWindow.axaml.cs]
WIZCOLLAPSE_01                               04   [code-only: CropToolWindow.axaml.cs]
PLAYOVERLAY_01                               04   [code-only: CropToolWindow.axaml]
ZOOMBAR_01                                   04   [code-only: CropToolWindow.axaml]
WIZCOMPACT_01                                04   [code-only: CropToolWindow.axaml]
TIMELINESLIM_01                              04   [code-only: CropToolWindow.axaml]
RESETMOVE_01                                 04   [code-only: CropToolWindow.axaml]
ORDERICONS_01                                04   [code-only: CropToolWindow.axaml]
RELAUNCHARG_01                               04   [code-only: CropToolWindow.axaml.cs]
HANDLECURSOR_01                              04   [code-only: CropToolWindow.axaml.cs]
NODUPES_01                                   04   [code-only: CropToolWindow.axaml.cs]
COMPOSERDIM_01                               04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
HEADERMERGE_01                               04   [code-only: CropToolWindow.axaml]
TIMELINESLIM_02                              04   [code-only: CropToolWindow.axaml]
PLAYROW_01                                   04   [code-only: CropToolWindow.axaml]
POPUPCLEAR_01                                04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
AUTOZOOM_02                                  04   [code-only: CropToolWindow.axaml.cs]
CROSSHAIR_01                                 04   [code-only: CropToolWindow.axaml.cs]
TOOLRETURN_01                                05 §3 SYS-WINSTATE
```


## Rebrand identity and verification routes

| File, symbol or tag | Specification |
| :--- | :--- |
| `FreeVideoStudio.sln`, application/core assembly identities, `FreeVideoStudio.App.update.zip` | `09` DIST-IDENTITY |
| `REBRAND_01`, `AppDataDir`, `LocalCacheDir`, `MigrateDirectory`, `FvsFreeVideoStudioMutex` | `05` SYS-REBRAND |
| `ProductionNamespacesUseTheProductRoot` | `08` COMP-ARCHTEST |

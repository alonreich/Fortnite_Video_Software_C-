# SYMBOL & FILE INDEX (routing lookup)

Flat lookup. Grep for a filename, symbol, constant or engineering tag; read ONLY the spec it names.
Notation: `03 §9 FFM-BINPATH` = `03_FFMPEG_EXPORT_PIPELINE.md`, section 9, stable anchor `FFM-BINPATH`.
Section NUMBERS shift as specs grow. The `{#ANCHOR}` ids are STABLE — quote anchors in Proof-of-Read headers, never bare numbers.
`mini-map only` = the symbol is bound to the spec but not discussed in its prose body; read that spec's Code Mini-Map row.

---

## 1. Source File -> Spec
`⚠` = CO-GOVERNED. Reading one listed spec is NOT compliance; read them all (`SPEC_GOVERNANCE.md` §2).

```
  AmbientBubblesBackground.cs                04
⚠ ApplicationPaths.cs                        05 GOV
  AtomicJsonFile.cs                          05
  AudioFilterChain.cs                        02
  AudioLoudnessProbe.cs                      02
  AvaloniaApp.axaml                          04
  Build.cmd                                  05
  CanvasMath.cs                              01
  CoachOverlay.cs                            04
  ConfirmDialogWindow.axaml.cs               04
  CoordinateMath.cs                          01
  DeploymentLifecycle.cs                     05
  dev.cmd                                    05
  FfmpegDiagnosticCollector.cs               03
  FloatingNotice.cs                          04
⚠ FluidVolumeSlider.cs                       02 04
  GpuCapabilityProbe.cs                      03
  GranularSpeedBuilder.cs                    03
⚠ GranularSpeedEditorWindow.axaml.cs         01 04 05
  HardwareScanner.cs                         03
  KineticScrubController.cs                  01
  LatestEstimateWorker.cs                    05
  MainMediaController.cs                     03
  MainWindow.Canvas.cs                       01
  MainWindow.Export.cs                       03
  MainWindow.SizeEstimate.cs                 03
  MainWindow.Shortcuts.cs                    01
  MainWindow.Wireup.cs                       01
⚠ MainWindow.axaml.cs                        01 02 04 GOV
  MaskOverlayManager.cs                      05
  MemePreviewDirector.cs                     03
  MergerWorker.cs                            03
  MicLevelMonitor.cs                         02
  MobileFilterBuilder.cs                     03
  IpcProtocol.cs                             05
  MpvIpcClient.cs                            02
  MpvVideoView.cs                            04
  NamedPipeStateServer.cs                    05
⚠ MusicWizardWindow.axaml.cs                 01 02
  OutputTimeline.cs                          01
  OutputFileSize.cs                          03
  OutputSizeEstimator.cs                     03
⚠ PhoneFrameMockup.axaml.cs                  01 04
  ExportViewModel.cs                         03
  ProcessWorker.cs                           03
  QualityLadder.cs                           03
  ProjectRecoveryService.cs                  05
⚠ RecoveryManager.cs                         05 GOV
  RuntimeLog.cs                              05
  SettingsManager.cs                         05
  SettingsWindow.axaml.cs                    04
  SpinningWheelSlider.cs                     04
  TextOverlayGenerator.cs                    03
  TimelineKnob.cs                            01
  TimelineLanesControl.axaml.cs              01
  UiStateStore.cs                            05
  UpdateAvailableWindow.axaml.cs             04
  UpdateService.cs                           05
  WindowResizeGrip.cs                        04
  VoiceOverPreviewPlayer.cs                  02
⚠ VoiceOverWindow.axaml.cs                   01 02
  VoiceRecorder.cs                           02
⚠ WindowBoundsHelper.cs                      04 05
  ZoomPreviewSimulator.cs                    03
  FvsBuild (Program.cs / Staging.cs)         05
```

Full paths: see each spec's Code Mini-Map.

---

## 2. Symbol / Constant / Tag -> Spec Section

```
_isSafeToClose                               05 §3 SYS-WINSTATE
SIZEESTIMATE_01                              03 FFM-SIZEESTIMATE | 04 UI-THEME | 05 SYS-SIZEESTIMATE
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
ServerLeaseName                              05 §4d SYS-IPCLIFETIME
SETTINGSATOMIC_01                            05 §4c SYS-ATOMICWRITE
TryRetireSlot                                04 §7b UI-GPUSLOT
WORKERLIFETIME_01                            03 §8b FFM-EXPORTLIFETIME
WORKERLIFETIME_02                            03 §8b FFM-EXPORTLIFETIME
_exportRunning                               03 §8b FFM-EXPORTLIFETIME
_presentGates                                04 §7b UI-GPUSLOT
_procGate                                    03 §8b FFM-EXPORTLIFETIME
_lastFreezeTriggerMs                         01 §4 TL-FREEZE
_mainEndParkIssued                           01 §8 TL-ENDSTOP
_muteCache                                   02 (mini-map only)
_previewParkedAtEnd                          01 §8 TL-ENDSTOP
_recalculatingTrackColumns                   04 §2 UI-DPI
_timeline                                    01 (mini-map only)
AcquireMutex                                 05 (mini-map only)
AmbientBubblesBackground                     04 (mini-map only)
AnalyzeFailure                               03 (mini-map only)
Anomalies                                    04 (mini-map only)
AppContext.BaseDirectory                     03 §9 FFM-BINPATH
AppDangerBrush                               04 §1 UI-THEME
ApplicationPaths                             05 (mini-map only)
Apply23Crop                                  03 (mini-map only)
ApplyCrossover                               02 (mini-map only)
ApplyExtrudedBorders                         03 (mini-map only)
ApplyFirstRunDisplayFit                      05 §3 SYS-WINSTATE
ApplyFriction                                01 (mini-map only)
ApplyMusicBed                                02 (mini-map only)
ApplyProfile                                 05 (mini-map only)
ApplySlowRamp                                03 (mini-map only)
ApplyTimeMapping                             02 (mini-map only)
ApplyTrackFilterAndSort                      02 §6 AUD-DIALOGS
AppPrimaryButtonGradient                     04 §1 UI-THEME
AppTableHairlineBrush                        04 §1 UI-THEME
AppTubeBrush                                 02 (mini-map only) | 04 (mini-map only)
AppZoomBrush                                 01 §6 TL-ZOOM | 04 §1 UI-THEME
AskEditOrRemoveAsync                         02 §6 AUD-DIALOGS | 04 (mini-map only)
ATOMICTEXT_01                                05 §4c SYS-ATOMICWRITE
AtomicJsonFile                               05 §4 SYS-RECOVERY
AtomicJsonFile.WriteText                     05 §4c SYS-ATOMICWRITE
AttachHost                                   03 (mini-map only)
AudioFilterChain                             02 §7 AUD-MUSICFADE
AudioLevelChanged                            02 (mini-map only)
AudioLoudnessProbe                           02 §2 AUD-MASTERING
AuthenticodeSign                             05 (mini-map only)
backend/                                     03 (mini-map only)
BeginMoveDrag                                04 §7 UI-BORDERLESS
BillableSeconds                              03 §8a FFM-QUALITY
BlockingCollection<string>                   05 §2 SYS-LOGGING
BubbleCount                                  04 §7 UI-BORDERLESS  [= 35]
Build                                        01 §1 TL-PORTRAIT | 03 (mini-map only) | 05 §5 SYS-SIGNING
BuildAtempoChain                             03 (mini-map only)
BuildAtempoFilter                            02 (mini-map only)
BuildFilterGraph                             03 (mini-map only)
BuildLoudnormFilter                          02 (mini-map only)
BuildMergeConcatGraph                        03 (mini-map only)
BuildSidechainGraph                          02 (mini-map only)
BuildWatermarkFilter                         03 (mini-map only)
CalculateEffectiveDurationMs                 03 §8a FFM-QUALITY
CalculateEndFit                              02 (mini-map only)
CalculateFreezeOutputMs                      03 §8a FFM-QUALITY
CanvasHeight                                 01 (mini-map only)  [= 1080]
CanvasMath                                   01 (mini-map only)
CanvasWidth                                  01 (mini-map only)  [= 1920]
CaptureStderr                                03 (mini-map only)
CascadeBak                                   05 (mini-map only)
CenterClearSlice                             01 (mini-map only) | 04 (mini-map only)  [= 720]
CheckFault                                   05 §4 SYS-RECOVERY
ChunkKind                                    01 (mini-map only)
ChunkSpec                                    03 (mini-map only)
ClaimOrphanedMutex                           05 (mini-map only)
ClampToClip                                  01 (mini-map only)
ClampZoomInsideItsBlock                      01 (mini-map only)
ClearLiveZoomCrop                            03 (mini-map only)
CoachOverlay                                 04 §5 UI-COACH
CoachTours                                   04 (mini-map only)
Compute                                      03 (mini-map only)
ConfirmDialogWindow                          02 §6 AUD-DIALOGS | 04 (mini-map only)
ContentAspect                                01 (mini-map only)  [= 2.0/3.0]
CoordinateMath                               01 §1 TL-PORTRAIT
CoreLogger                                   05 (mini-map only)
Create                                       01 (mini-map only)
Cut                                          01 §2 TL-OUTPUTTIMELINE, 01 §5 TL-CUTS | 02 §2 AUD-MASTERING, 02 §4 AUD-VOICEOVER | 04 §4 UI-SAFEGUARDS
CUTS_02                                      02 §4 AUD-VOICEOVER
DebounceMs                                   04 (mini-map only) | 05 (mini-map only)  [= 700]
DefaultValues.QualityIndex                   03 §8a FFM-QUALITY
DeploymentLifecycle                          05 §1 SYS-MUTEX
DimmerFlanks                                 01 (mini-map only) | 04 (mini-map only)
DOUBLEFIRE_01                                04 §4 UI-SAFEGUARDS
EnableGlobalRipple                           02 (mini-map only) | 04 §1 UI-THEME
EndThumbnailMarkerDrag                       01 §9 TL-HITBOX
EndUndoGesture                               01 (mini-map only)
EnsureDefaults                               05 (mini-map only)
EnsureStep2WaveformPresent                   02 §6 AUD-DIALOGS
EnsureWritableDirectories                    05 (mini-map only)
ExecuteAsync                                 03 (mini-map only)
ExecuteCutaway                               03 (mini-map only)
ExecuteInstall                               05 (mini-map only)
ExecuteMergeAsync                            03 (mini-map only)
ExportPayload                                03 (mini-map only)
FallbackToCpu                                03 (mini-map only)
FFM-QUALITY                                  03 §8a FFM-QUALITY
FfmpegDiagnosticCollector                    03 (mini-map only)
File.Move                                    05 §4 SYS-RECOVERY
FileOptions.WriteThrough                     05 §4 SYS-RECOVERY
FIRSTFIT_01                                  05 §3 SYS-WINSTATE
FirstRunAspect                               05 §3 SYS-WINSTATE
FirstRunCoverage                             05 §3 SYS-WINSTATE
FitByEndOfVideo                              01 (mini-map only) | 02 (mini-map only)
fitDisplayOnFirstRun                         05 §3 SYS-WINSTATE
FixRdpWddmRegistry                           03 (mini-map only)
FloatingNotice                               04 §5 UI-COACH
FluidVolumeSlider                            02 (mini-map only) | 04 (mini-map only)
FormatDiagnosticReport                       03 (mini-map only)
FREEZE_01                                    01 §4 TL-FREEZE
FreezeSecondCostFactor                       03 §8a FFM-QUALITY
FVS_DEV_LOG_DIR                              05 §4a SYS-DEVBUILD
FVS_SIGN_PASS                                05 §5 SYS-SIGNING
FVS_SIGN_PFX                                 05 §5 SYS-SIGNING
GeneratePng                                  03 (mini-map only)
GetQualitySettings                           03 §8a FFM-QUALITY
GlobalMasterVolume                           02 (mini-map only)
GpuCapabilityProbe                           03 §1 FFM-HWENC
GranularSpeedBuilder                         03 (mini-map only) | 05 §3 SYS-WINSTATE
GranularSpeedEditorWindow                    01 (mini-map only) | 04 (mini-map only) | 05 (mini-map only)
GRIP_01                                      04 §7a UI-RESIZEGRIP
HardwareScanner                              03 (mini-map only)
HasAmf                                       03 (mini-map only)
HasNvenc                                     03 (mini-map only)
HasUnsavedWork                               05 (mini-map only)
HitTestGrabZone                              01 (mini-map only)
InertiaDecay                                 01 (mini-map only)
InsertionAt                                  01 (mini-map only)
IsActive                                     02 §4 AUD-VOICEOVER | 03 §5 FFM-MEMEPREVIEW
IsEof                                        02 §2a AUD-IPCSTATE
IsMainPreviewAtEnd                           01 §8 TL-ENDSTOP
IsRdpSession                                 03 (mini-map only)
IsSafeModeActive                             05 (mini-map only)
KineticScrubController                       01 (mini-map only)
LAYOUT_03                                    02 §6 AUD-DIALOGS
LayoutPass                                   04 (mini-map only)
LIST_05                                      02 §6 AUD-DIALOGS
LIST_06                                      04 §2 UI-DPI
LIST_07                                      02 §6 AUD-DIALOGS
LoadState                                    05 (mini-map only)
LogMutex                                     05 (mini-map only)
MAINEND_01                                   01 §8 TL-ENDSTOP
MainTimelineEndSeconds                       01 §8 TL-ENDSTOP
MainWindow                                   01 §2 TL-OUTPUTTIMELINE | 02 (mini-map only) | 04 (mini-map only) | 05 §3 SYS-WINSTATE
MaskOverlayManager                           05 (mini-map only)
MaxUndoDepth                                 04 §6 UI-GRANULAR  [= 40]
MeasureColumns                               02 (mini-map only)
MeasureLoudness                              02 (mini-map only)
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
MpvIpcClient                                 02 §1 AUD-MASTERVOL | 03 §9 FFM-BINPATH
MusicVolSlider                               02 §6 AUD-DIALOGS
MusicWizardWindow                            01 (mini-map only) | 02 (mini-map only)
NormalizeCuts                                01 (mini-map only)
Notify                                       04 (mini-map only)
NotifyError                                  04 (mini-map only)
ObserveProperty                              02 (mini-map only)
OnArmingTimeout                              02 (mini-map only)
OnClosed                                     05 §3 SYS-WINSTATE
OnClosing                                    05 §3 SYS-WINSTATE
OnConfirm                                    04 (mini-map only)
OnPointerMoved                               01 (mini-map only) | 02 (mini-map only) | 04 (mini-map only)
OnPointerPressed                             01 (mini-map only)
OnPointerReleased                            01 (mini-map only)
OnPointerWheelChanged                        04 (mini-map only)
OnRenderTick                                 04 (mini-map only)
OnScrubberPointerMoved                       01 (mini-map only)
OnScrubTick                                  01 (mini-map only)
OnTrackSelected                              02 §6 AUD-DIALOGS
OnVolumeChanged                              02 (mini-map only)
OutputTimeline                               01 §2 TL-OUTPUTTIMELINE | 02 §6 AUD-DIALOGS
OutputToSource                               01 §2 TL-OUTPUTTIMELINE
OutXToSrcMs                                  01 (mini-map only)
OverlayCanvas                                04 §5 UI-COACH
P3ASYNC_01                                   02 §6 AUD-DIALOGS
PaintQualityEstimate                         04 §4 UI-SAFEGUARDS
ParseProgress                                03 (mini-map only)
PerformFFTAnalysis                           02 (mini-map only)
PhoneFrameMockup                             01 §1 TL-PORTRAIT | 04 (mini-map only)
PhysicsStep                                  04 (mini-map only)  [= 30Hz]
ProbeEncoders                                03 (mini-map only)
ProcessWorker                                03 §9 FFM-BINPATH
ProgramDataRoot                              05 (mini-map only)
ProgressInfo                                 03 (mini-map only)
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
ReadString                                   05 (mini-map only)
RecoveryManager                              05 §4 SYS-RECOVERY
RecoveryStateFile                            05 (mini-map only)
Redo                                         04 §6 UI-GRANULAR
RedrawLanes                                  01 (mini-map only)
RedrawTimelineCanvas                         01 (mini-map only)
Register                                     04 (mini-map only)
RelayoutFrameLane                            01 (mini-map only)
Reload                                       02 (mini-map only)
ReloadTimeline                               01 (mini-map only)
RenderTextBitmap                             03 (mini-map only)
RenderTicks                                  01 (mini-map only)
RenderWaveform                               02 (mini-map only)
ReportMicHealth                              02 §4 AUD-VOICEOVER
ResampleTo60Fps                              03 (mini-map only)
ResizeGrip                                   04 §7a UI-RESIZEGRIP
ResolveHostPanel                             04 §5 UI-COACH
ResolveTargetMb                              03 §8a FFM-QUALITY
RestoreBounds                                04 (mini-map only) | 05 (mini-map only)
RestoreRecoveryState                         05 (mini-map only)
ResultSegments                               05 §3 SYS-WINSTATE
RESUME_01                                    02 §6 AUD-DIALOGS
ResumeFromInitialStateAsync                  02 §6 AUD-DIALOGS
RetentionDays                                05 (mini-map only)  [= 14]
RewindFromTimelineEnd                        01 §8 TL-ENDSTOP
RotateBackups                                05 (mini-map only)
RotateLogs                                   05 (mini-map only)
RuntimeLog                                   05 (mini-map only)
SampleRate                                   02 (mini-map only)  [= 44100]
SaveBoundsSync                               04 (mini-map only) | 05 (mini-map only)
SaveRecoveryState                            02 (mini-map only)
SaveState                                    05 (mini-map only)
ScanGpu                                      03 (mini-map only)
SchemaVersion                                05 (mini-map only)  [= 1]
SEAM_01                                      01 §3 TL-MARKERS | 04 §6 UI-GRANULAR
SEEKSTORM_01                                 04 §6 UI-GRANULAR
SegDragMode                                  01 (mini-map only)
SelectSegment                                01 (mini-map only)
SerializeState                               05 (mini-map only)
SessionStateFile                             05 (mini-map only)
SetButtonText                                04 (mini-map only)
SetMasterVolume                              02 §1 AUD-MASTERVOL
SetValueSmooth                               04 (mini-map only)
SizeToContent                                04 §2 UI-DPI
SLIDER_06                                    02 §6 AUD-DIALOGS
SLIDER_07                                    04 §1 UI-THEME
SliderThumb                                  04 (mini-map only)
SnapInsertionPoint                           01 §7 TL-MEME
SnapToTick                                   04 (mini-map only)
SourceMsToOutputSeconds                      01 §2 TL-OUTPUTTIMELINE
SourceToOutput                               01 §7 TL-MEME
SourceToPortraitCoords                       01 (mini-map only)
SpinningWheelSlider                          04 (mini-map only)
SPLICE_01                                    03 §3 FFM-CONCAT
SPLICE_02                                    03 §3 FFM-CONCAT
SrcMsToOutX                                  01 (mini-map only)
StartMonitoring                              02 (mini-map only)
StartRecordingAsync                          02 (mini-map only)
StartTour                                    04 (mini-map only)
StopMonitoring                               02 (mini-map only)
StopRecordingAsync                           02 (mini-map only)
SubPixelXOffset                              01 (mini-map only)
SurvivingSourceWidth                         01 §1 TL-PORTRAIT  [= 720]
SyncPlayback                                 02 (mini-map only)
SYS-DEVBUILD                                 05 §4a SYS-DEVBUILD
Tactile                                      02 (mini-map only) | 04 §1 UI-THEME
TargetLufs                                   02 (mini-map only)  [= -14.0]
TargetMbFor                                  03 §8a FFM-QUALITY
TextOverlayGenerator                         03 (mini-map only)
ThrottledPaint                               04 (mini-map only)
THUMB_01                                     01 §9 TL-HITBOX
THUMB_02                                     01 §9 TL-HITBOX
TimelineChunk                                01 (mini-map only)
TimelineKnob                                 01 (mini-map only)
TimelineLanesControl                         01 (mini-map only)
TimelineStartSeconds                         01 (mini-map only)
TimePos                                      02 (mini-map only)
TimeSpanToPixels                             01 (mini-map only)
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
Uninstall                                    05 (mini-map only)
UpdateEstimatedQuality                       03 §8a FFM-QUALITY
UpdateMarkerFollow                           01 (mini-map only)
UpdateMarkerRects                            01 (mini-map only)
UpdateMonotonicProgress                      03 (mini-map only)
UpdateReadyLamp                              02 §4 AUD-VOICEOVER
UpdateThumbnailButtonState                   01 (mini-map only)
UpdateTimelineMarkers                        01 (mini-map only)
UpdateVisualizer                             02 (mini-map only)
Velocity                                     01 (mini-map only)
VERIFY_PATCHES                               05 §4a SYS-DEVBUILD
VideoVolSlider                               02 §6 AUD-DIALOGS
VOEND_01                                     01 §8 TL-ENDSTOP
VoiceOverPreviewPlayer                       02 §4 AUD-VOICEOVER
VoiceOverWindow                              01 (mini-map only) | 02 (mini-map only)
VoiceProtectionSystem                        02 (mini-map only)
VoiceRecorder                                02 §4 AUD-VOICEOVER
VolumeChanged                                02 (mini-map only) | 04 (mini-map only)
VolumeSlider                                 02 (mini-map only)
VOMON_02                                     02 §4 AUD-VOICEOVER
WasapiCapture                                02 (mini-map only)
WindowBoundsHelper                           04 §8 UI-DETACH | 05 §3 SYS-WINSTATE
WindowResizeGrip                             04 §7a UI-RESIZEGRIP
WINSEED_01                                   05 §3 SYS-WINSTATE
WorkerConstantQualityLevel                   03 §8a FFM-QUALITY
WorkerThread                                 02 (mini-map only)
WrapText                                     03 (mini-map only)
WriteInt                                     05 (mini-map only)
WriteObject                                  05 §4 SYS-RECOVERY
WriteString                                  05 (mini-map only)
ZoomCropResult                               03 (mini-map only)
ZOOMLIVE_07                                  04 §6 UI-GRANULAR
ZOOMCARD_01                                  04 §6 UI-GRANULAR
ZOOMSTYLE_02                                 04 §6 UI-GRANULAR
ZOOMPREVIEW_01                               04 §6 UI-GRANULAR
ZOOMCOMMIT_01                                04 §6 UI-GRANULAR
CheckManualAsync                             05 §6 SYS-AUTOUPDATE
ShowAboutAsync                               04 §11 UI-SETTINGS-ABOUT
GetCurrentVersion                            05 §6 SYS-AUTOUPDATE
TryParseVersion                              05 §6 SYS-AUTOUPDATE
SynchronizeVersionFiles                      05 §6 SYS-AUTOUPDATE
ZoomPreviewSimulator                         02 §4 AUD-VOICEOVER | 03 (mini-map only)
ZoomRampRequiredGap                          03 (mini-map only)  [= 1.0]
ZoomRampSeconds                              03 (mini-map only)  [= 0.5]
```


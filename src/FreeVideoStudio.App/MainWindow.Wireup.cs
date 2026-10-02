using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private void WireComponents()
    {
        this.AddHandler(DragDrop.DragEnterEvent, OnVideoDragEnter);
        this.AddHandler(DragDrop.DragOverEvent, OnVideoDragOver);
        this.AddHandler(DragDrop.DragLeaveEvent, OnVideoDragLeave);
        this.AddHandler(DragDrop.DropEvent, OnVideoDrop);

        var radialMenu = RadialMenuCtl;
        if (radialMenu != null)
        {
            radialMenu.AddItem("meme", "Meme", Avalonia.Media.SolidColorBrush.Parse("#2094f3"));
            radialMenu.AddItem("speed", "Speed", Avalonia.Media.SolidColorBrush.Parse("#8b5cf6"));
            radialMenu.AddItem("export", "Export", Avalonia.Media.SolidColorBrush.Parse("#10b981"));
            radialMenu.ItemSelected += (id) =>
            {
                if (id == "meme")
                {
                    var cb = this.FindControl<Avalonia.Controls.ToggleSwitch>("AddMemeCheckbox");
                    if (cb != null) cb.IsChecked = !cb.IsChecked;
                }
                else if (id == "speed")
                {
                    var btn = this.FindControl<Avalonia.Controls.Button>("GranularButton");
                    btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                }
                else if (id == "export")
                {
                    var btn = this.FindControl<Avalonia.Controls.Button>("ProcessButton");
                    btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                }
            };
        }

        this.AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        this.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        this.AddHandler(InputElement.PointerReleasedEvent, OnWindowPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Win32FileDropInterop.Attach(this, paths => _ = HandleExternalFileDropAsync(paths));

        var overlay = OverlayLayerCtl;
        if (overlay != null)
        {
            overlay.CancelRequested += (s, e) =>
            {
                if (_processCts != null && !_processCts.IsCancellationRequested)
                {
                    try { _processCts.Cancel(); }
                    catch (ObjectDisposedException swallowed)
                    {
                        global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
                    }

                    var btn = ProcessButtonCtl;
                    if (btn != null)
                    {
                        btn.IsEnabled = false;
                        btn.Content = "CANCELLING...";
                    }
                    ShowTacticalFeedback("Cancelling — stopping the encoder");
                    PlayUiSound();
                }
            };
        }

        this.Loaded += (s, e) => InitializeMpv();

        this.Loaded += (s, e) => Controls.CoachOverlay.Register(this, Controls.CoachTours.MainAppKey, Controls.CoachTours.MainApp);


        SettingsManager.Load();
        ThemeManager.ApplyFromSettings();
        FreeVideoStudio.App.Infrastructure.MaskOverlayManager.ApplyProfile(FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay);
        ApplyMaskProfileToOverlayUi();

        var settingsBtn = this.FindControl<MenuItem>("MenuSettingsBtn");
        if (settingsBtn != null)
        {
            settingsBtn.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Settings menu button.");
                var settingsWin = new FreeVideoStudio.App.Controls.SettingsWindow();
                bool changed = await settingsWin.ShowDialog<bool>(this);
                if (changed)
                {
                    UpdateTooltips();
                    RefreshTransportKeyBindings();
                    ApplyMaskProfileToOverlayUi();
                }
            };
        }

        var menuUploadVideo = this.FindControl<MenuItem>("MenuUploadVideo");
        if (menuUploadVideo != null) menuUploadVideo.Click += OnUploadVideoClicked;

        var menuTogglePreview = MenuTogglePreviewMonitorCtl;
        if (menuTogglePreview != null) menuTogglePreview.Click += async (s, e) =>
        {
            if (!IsPreviewDetached) await DetachPreviewMonitor();
            else await AttachPreviewMonitor();
        };

        var detachOverlayBtn = DetachOverlayButtonCtl;
        if (detachOverlayBtn != null) detachOverlayBtn.Click += async (s, e) =>
        {
            if (!IsPreviewDetached) await DetachPreviewMonitor();
            else await AttachPreviewMonitor();
        };

        var menuExportConfig = this.FindControl<MenuItem>("MenuExportConfig");
        if (menuExportConfig != null) menuExportConfig.Click += OnExportConfigClicked;

        var menuImportConfig = this.FindControl<MenuItem>("MenuImportConfig");
        if (menuImportConfig != null) menuImportConfig.Click += OnImportConfigClicked;

        var menuExit = this.FindControl<MenuItem>("MenuExit");
        if (menuExit != null) menuExit.Click += (s, e) => Close();

        var menuCropSettings = this.FindControl<MenuItem>("MenuCropSettings");
        if (menuCropSettings != null) menuCropSettings.Click += async (s, e) =>
            await SwitchToCompanionAppAsync("--crop-tool", "Crop Tools");

        var menuVideoMerger = this.FindControl<MenuItem>("MenuVideoMerger");
        if (menuVideoMerger != null) menuVideoMerger.Click += async (s, e) =>
            await SwitchToCompanionAppAsync("--merger", "Video Merger");

        var menuShowShortcuts = this.FindControl<MenuItem>("MenuShowShortcuts");
        if (menuShowShortcuts != null) menuShowShortcuts.Click += (s, e) =>
        {
            var sheet = ShortcutSheetOverlayCtl;
            if (sheet == null) return;
            if (!sheet.IsVisible) BuildShortcutSheetRows();
            sheet.IsVisible = !sheet.IsVisible;
            RuntimeLog.Info("UI", $"Keyboard shortcut sheet {(sheet.IsVisible ? "opened" : "closed")} from the View menu.");
        };

        var menuShowHelp = this.FindControl<MenuItem>("MenuShowHelp");
        if (menuShowHelp != null) menuShowHelp.Click += (s, e) => CoachOverlay.Replay(this);

        var menuAboutBtn = this.FindControl<MenuItem>("MenuAboutBtn");
        if (menuAboutBtn != null)
        {
            menuAboutBtn.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked File -> About.");
                await FreeVideoStudio.App.Controls.SettingsWindow.ShowAboutAsync(this);
            };
        }

        var menuHelpAbout = this.FindControl<MenuItem>("MenuHelpAbout");
        if (menuHelpAbout != null)
        {
            menuHelpAbout.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Help -> About.");
                await FreeVideoStudio.App.Controls.SettingsWindow.ShowAboutAsync(this);
            };
        }

        var menuCheckUpdates = this.FindControl<MenuItem>("MenuCheckUpdates");
        if (menuCheckUpdates != null)
        {
            menuCheckUpdates.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Help -> Check for Updates.");
                await FreeVideoStudio.App.Services.UpdateService.CheckManualAsync(this);
            };
        }

        var menuHelpShortcuts = this.FindControl<MenuItem>("MenuHelpShortcuts");
        if (menuHelpShortcuts != null)
        {
            menuHelpShortcuts.Click += (s, e) =>
            {
                var sheet = ShortcutSheetOverlayCtl;
                if (sheet == null) return;
                if (!sheet.IsVisible) BuildShortcutSheetRows();
                sheet.IsVisible = !sheet.IsVisible;
                RuntimeLog.Info("UI", $"Keyboard shortcut sheet {(sheet.IsVisible ? "opened" : "closed")} from the Help menu.");
            };
        }

        var menuHelpTour = this.FindControl<MenuItem>("MenuHelpTour");
        if (menuHelpTour != null) menuHelpTour.Click += (s, e) => CoachOverlay.Replay(this);

        var shortcutSheetClose = this.FindControl<Button>("ShortcutSheetCloseButton");
        if (shortcutSheetClose != null) shortcutSheetClose.Click += (s, e) =>
        {
            var sheet = ShortcutSheetOverlayCtl;
            if (sheet != null) sheet.IsVisible = false;
        };

        UpdateTooltips();
        RefreshTransportKeyBindings();

        FreeVideoStudio.App.WindowBoundsHelper.Track(this, "MainWindowBounds", fitDisplayOnFirstRun: true);

        FreeVideoStudio.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;

        InitializeUxInnovations();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        AttachTitleBarDrag();

        var canvas = TimelineMarkersCanvasCtl;
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => UpdateTimelineMarkers();
        }

        this.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            bool markerDragActive = IsMarkerGestureActive;

            if (_isMusicBlockFocused)
            {
                if (_suppressNextMusicDeselect)
                {
                    _suppressNextMusicDeselect = false;
                }
                else
                {
                    _isMusicBlockFocused = false;
                    if (!markerDragActive) UpdateTimelineMarkers();
                }
            }
            if (_isThumbnailMarkerSelected && !_isDraggingThumbnailMarker)
            {
                _isThumbnailMarkerSelected = false;
                if (!markerDragActive) UpdateTimelineMarkers();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        _marchingAntsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _marchingAntsTimer.Tick += (s, e) =>
        {
            bool hasThumbnailMarkerAnts = (_isThumbnailMarkerSelected || _isDraggingThumbnailMarker) &&
                                          _thumbnailMarkerIconAntsRef != null &&
                                          _thumbnailMarkerLineAntsRef != null;
            if (hasThumbnailMarkerAnts)
            {
                _marchingAntsOffset += 1;
                if (_marchingAntsOffset > 1000) _marchingAntsOffset = 0;
                _thumbnailMarkerIconAntsRef!.StrokeDashOffset = _marchingAntsOffset;
                _thumbnailMarkerLineAntsRef!.StrokeDashOffset = _marchingAntsOffset;
            }

            if (_isMusicBlockFocused && _musicBlockRectRef != null)
            {
                _marchingAntsOffset += 1;
                if (_marchingAntsOffset > 1000) _marchingAntsOffset = 0;
                _musicBlockRectRef.StrokeDashOffset = _marchingAntsOffset;
            }
        };
        _marchingAntsTimer.Start();

        var slider = TimelineSliderCtl;

        var cancelButton = CancelButtonCtl;
        if (cancelButton != null)
        {
            cancelButton.Click += (s, e) =>
            {
                if (!SettingsManager.Instance.ConfirmMainAppCancel)
                {
                    cancelButton.Flyout?.Hide();
                    RuntimeLog.Info("UI", "Cancel button pressed with confirmations disabled, closing app.");
                    Close();
                }
            };
        }

        var keepWorkingButton = this.FindControl<Button>("KeepWorkingButton");
        if (keepWorkingButton != null)
        {
            keepWorkingButton.Click += (s, e) =>
            {
                CancelButtonCtl?.Flyout?.Hide();
                RuntimeLog.Info("UI", "User backed out of the Cancel button and kept working.");
            };
        }

        var confirmCancelButton = this.FindControl<Button>("ConfirmCancelButton");
        if (confirmCancelButton != null)
        {
            confirmCancelButton.Click += (s, e) =>
            {
                var btn = CancelButtonCtl;
                btn?.Flyout?.Hide();
                RuntimeLog.Info("UI", "User confirmed Cancel button, closing app.");
                Close();
            };
        }

        var processButton = ProcessButtonCtl;
        if (processButton != null)
        {
            processButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked PROCESS button.");
                await ProcessVideoAsync(processButton);
            };
        }


        var granularButton = GranularButtonCtl;
        if (granularButton != null)
        {
            granularButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked GRANULAR SPEED button.");

                if (_isGranularSpeedActive)
                {
                    var choice = await ConfirmDialogWindow.AskEditOrRemoveAsync(
                        this,
                        "Edit Speed Changes?",
                        "You already have speed changes on this video.\n\n" +
                        "EDIT reopens the Speed Editor with every segment, freeze, zoom and cut still in place, so you can change them one at a time.\n" +
                        "REMOVE deletes the speed segments and the freeze, putting the video back to a single speed. Any deleted parts stay deleted.\n" +
                        "CANCEL changes nothing.");

                    if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Cancelled) return;

                    if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Remove)
                    {
                        _speedSegments.Clear();
                        _freezeTimeMs = -1;
                        SetGranularButtonActive(false);
                        _lastAppliedSpeed = _baseSpeed;
                        if (ActiveVideoHost?.IpcClient != null)
                            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed",
                                _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                        InvalidateVoiceOverRecordingForTimingChange();
                        UpdateEstimatedQuality();
                        ShowTacticalFeedback("Speed segments removed");
                        UpdateTimelineMarkers();
                        SaveRecoveryState(label: "remove speed segments");
                        RuntimeLog.Info("UI", "User removed all granular speed segments via the EDIT/REMOVE prompt.");
                        return;
                    }

                    RuntimeLog.Info("UI", "User chose EDIT on existing granular speed segments.");
                }

                if (string.IsNullOrWhiteSpace(_loadedVideoPath))
                {
                    ShowTacticalFeedback("Load a video first!");
                    PlayUiSound();
                    return;
                }

                if (ActiveVideoHost?.IpcClient != null)
                    _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

                EnsureTrimPointsSet();

                SetTimelinePopupsVisible(false);

                bool isMobileForZoom = PortraitModeCheckboxCtl?.IsChecked == true;
                var zoomIpc = ActiveVideoHost?.IpcClient;
                string zoomSrcRes = (zoomIpc != null && zoomIpc.VideoWidth > 0 && zoomIpc.VideoHeight > 0)
                    ? $"{zoomIpc.VideoWidth}x{zoomIpc.VideoHeight}"
                    : "1920x1080";
                bool cutsChangedByEditor = false;
                var editor = await GranularSpeedEditorWindow.CreateAsync(
                    _loadedVideoPath,
                    _trimStartMs,
                    _trimEndMs > 0 ? _trimEndMs : (zoomIpc?.Duration ?? 0) * 1000,
                    _speedSegments,
                    _baseSpeed,
                    _freezeTimeMs,
                    _freezeDurationS,
                    isMobileForZoom,
                    zoomSrcRes,
                    _voiceOverResult,
                    _cuts,
                    _memePlacements);

                editor.AvailableMemes = _memeItems;

                await editor.ShowDialog(this);
                ReturnToTrimStartPaused();

                SetTimelinePopupsVisible(true);
                if (_musicBlockRectRef != null) _musicBlockRectRef.IsVisible = true;

                if (editor.Accepted)
                {
                    _speedSegments.Clear();
                    _speedSegments.AddRange(editor.ResultSegments);
                    _baseSpeed = editor.ResultBaseSpeed;
                    _freezeTimeMs = editor.ResultFreezeTimeMs;
                    _freezeDurationS = editor.ResultFreezeDurationS;

                    _cuts.Clear();
                    _cuts.AddRange(editor.ResultCuts);
                    cutsChangedByEditor = true;

                    _memePlacements.Clear();
                    _memePlacements.AddRange(editor.ResultMemes);

                    ClearLiveZoomCrop();

                    int count = _speedSegments.Count;
                    RuntimeLog.Info("UI", $"Granular editor closed. {count} segment(s) saved. " +
                        $"Base speed={_baseSpeed:F2}x. FreezeTimeMs={_freezeTimeMs}. " +
                        $"{_cuts.Count} cut(s) removing {RemovedCutSeconds():F2}s.");

                    bool hasGranularEdits = count > 0 || _freezeTimeMs >= 0;

                    SetGranularButtonActive(hasGranularEdits);

                    ShowTacticalFeedback(hasGranularEdits
                        ? "Granular settings applied"
                        : "Granular segments cleared");

                    InvalidateVoiceOverRecordingForTimingChange();
                    UpdateEstimatedQuality();
                    UpdateTimelineMarkers();
                    SaveRecoveryState(label: "granular speed edits");

                    if (cutsChangedByEditor) await AfterCutsChangedAsync();
                }
            };
        }

        var uploadButton = this.FindControl<Button>("UploadButton");
        if (uploadButton != null)
        {
            uploadButton.Click += (s, e) => { RuntimeLog.Info("UI", "User clicked Upload Video button"); OnUploadVideoClicked(s, e); };
        }

        var centerUploadButton = this.FindControl<Button>("CenterUploadButton");
        if (centerUploadButton != null)
        {
            centerUploadButton.Click += (s, e) => { RuntimeLog.Info("UI", "User clicked Center Upload Video button"); OnUploadVideoClicked(s, e); };
        }

        var videoMergerButton = this.FindControl<Button>("VideoMergerButton");
        if (videoMergerButton != null)
        {
            videoMergerButton.Click += async (s, e) => await SwitchToCompanionAppAsync("--merger", "Video Merger");
        }

        var cropSettingsButton = this.FindControl<Button>("CropSettingsButton");
        if (cropSettingsButton != null)
        {
            cropSettingsButton.Click += async (s, e) =>
            {
                if (BlockCropToolsForNoMaskProfile()) return;
                await SwitchToCompanionAppAsync("--crop-tool", "Crop Tools");
            };
        }


        var setThumbnailButton = SetThumbnailButtonCtl;
        if (setThumbnailButton != null)
        {
            setThumbnailButton.Click += (s, e) =>
            {
                double time = GetCurrentMpvTime();

                if (_thumbnailSet && IsPlayheadOnThumbnail())
                {
                    _thumbnailSet = false;
                    _thumbnailPosMs = 0;
                    _isThumbnailMarkerSelected = false;
                    _isDraggingThumbnailMarker = false;
                    ShowTacticalFeedback("Thumbnail removed");
                }
                else
                {
                    bool moved = _thumbnailSet;
                    _thumbnailPosMs = time * 1000;
                    _thumbnailSet = true;
                    _isThumbnailMarkerSelected = true;

                    PlayUiSound();
                    ShowTacticalFeedback($"📸 {(moved ? "Moved to " : "")}{TimeSpan.FromSeconds(time):mm\\:ss\\.ff}");
                    ShowTimelineGlow(_thumbnailPosMs, Avalonia.Media.Brushes.DeepSkyBlue);
                }

                UpdateThumbnailButtonState();
                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
                SaveRecoveryState(label: "set thumbnail");
            };
        }

        var voiceOverButton = VoiceOverButtonCtl;
        if (voiceOverButton != null)
        {
            voiceOverButton.Click += async (s, e) =>
            {
                if (string.IsNullOrEmpty(_loadedVideoPath)) return;

                if (_voiceOverResult != null)
                {
                    var choice = await ConfirmDialogWindow.AskEditOrRemoveAsync(
                        this,
                        "Edit Voice Over?",
                        "You already have voice-over takes on this video.\n\n" +
                        "EDIT reopens the studio with your takes still there, so you can add, trim, mute or delete individual takes.\n" +
                        "REMOVE throws all of them away.\n" +
                        "CANCEL changes nothing.");

                    if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Cancelled) return;

                    if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Remove)
                    {
                        ApplyVoiceOverState(null);
                        ShowTacticalFeedback("Voice Over Removed");
                        return;
                    }
                }

                bool wasPlaying = ActiveVideoHost?.IpcClient?.IsPaused == false;
                if (wasPlaying) _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

                var dialog = new VoiceOverWindow(
                    _loadedVideoPath,
                    GetCurrentMpvTime(),
                    _trimStartMs,
                    _trimEndSet ? _trimEndMs : 0,
                    BuildExportSpeedSegments(),
                    _baseSpeed,
                    _cuts,
                    _memePlacements)
                {
                    InitialState = _voiceOverResult
                };
                dialog.SourceMeasuredLufs = _sourceMeasuredLufs;
                dialog.IsPortraitPreview =
                    PortraitModeCheckboxCtl?.IsChecked == true;
                try
                {
                    await dialog.ShowDialog(this);
                    ReturnToTrimStartPaused();

                    if (HasVoiceOverEffect(dialog.Result))
                    {
                        ApplyVoiceOverState(dialog.Result);
                        ShowTacticalFeedback(HasVoiceOverWav(dialog.Result) ? "Voice Over Applied" : "Voice Effects Applied");
                    }
                }
                finally
                {
                    if (wasPlaying && ActiveVideoHost?.IpcClient != null)
                    {
                        _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "no");
                    }
                }
            };
        }



        var timelineSlider = TimelineSliderCtl;
        if (timelineSlider != null)
        {
            timelineSlider.ValueChanged += (s, e) =>
            {
                if (!_isTimerUpdatingSlider)
                {
                    double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
                    if (duration > 0)
                    {
                        double targetTime = (e.NewValue / 100.0) * duration;
                        _ = SeekInternal(targetTime);
                        ShowPlayheadBadge(targetTime, e.NewValue);
                    }
                }
            };

            var timelineOverlay = TimelineOverlayCtl;
            if (timelineOverlay != null && canvas != null)
            {
                Controls.TimelineKnob.Attach(timelineOverlay, timelineSlider);

                bool isScrubbing = false;
                timelineOverlay.PointerPressed += (s, e) => {
                    if (e.GetCurrentPoint(timelineOverlay).Properties.IsLeftButtonPressed) {
                        isScrubbing = true;
                        e.Pointer.Capture(timelineOverlay);
                        SeekTimelineFromPointer(e, canvas, timelineSlider);
                    }
                };
                timelineOverlay.PointerMoved += (s, e) => {
                    if (isScrubbing && e.GetCurrentPoint(timelineOverlay).Properties.IsLeftButtonPressed) {
                        SeekTimelineFromPointer(e, canvas, timelineSlider);
                    }
                };
                timelineOverlay.PointerReleased += (s, e) => {
                    isScrubbing = false;
                    e.Pointer.Capture(null);
                };
            }
        }

        var mainSpeedSlider = this.FindControl<FreeVideoStudio.App.Controls.SpinningWheelSlider>("MainSpeedSlider");
        if (mainSpeedSlider != null)
        {
            mainSpeedSlider.SetRange(1, 40);
            var speedLabels = new System.Collections.Generic.List<string>();
            for (int i = 1; i <= 40; i++) speedLabels.Add((i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x");
            mainSpeedSlider.SetLabels(speedLabels);
            mainSpeedSlider.Value = 11;
            SpeedPresetButtons.ConfigureBaseButton(this, SpeedPresetButtons.NativeDefaultSpeed, "Set speed to the app default 1.1x");
            SpeedPresetButtons.WirePresetButtons(this, SpeedPresetButtons.NativeDefaultSpeed, ApplyMainSpeedPreset);
            mainSpeedSlider.ValueChanged += (s, e) =>
            {
                double previousSpeed = _baseSpeed;
                _baseSpeed = e / 10.0;
                if (ActiveVideoHost?.IpcClient != null)
                    _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("speed", _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                if (Math.Abs(previousSpeed - _baseSpeed) > 0.001)
                    InvalidateVoiceOverRecordingForTimingChange();
                UpdateEstimatedQuality();
                UpdateSpeedLabel();
                SaveRecoveryState(label: "change speed", gestureKey: "speed-dial");
            };
            mainSpeedSlider.ValueChangeCompleted += (s, e) =>
            {
                EndProjectGesture();
                RuntimeLog.Info("UI", $"Speed slider final resting value: {e / 10.0:F1}x");
            };
            UpdateSpeedLabel();
        }

        var volumeSlider = VolumeSliderCtl;
        var volumeBadgeText = this.FindControl<TextBlock>("VolumeBadgeText");
        var volumeSpeakerIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("VolumeSpeakerIcon");
        if (volumeSlider != null && volumeBadgeText != null)
        {
            volumeSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty && e.NewValue != null)
                {
                    if (_isSyncingMasterVolume) return;
                    int vol = System.Convert.ToInt32(e.NewValue);
                    volumeBadgeText.Text = $"{vol}%";
                    ApplyMasterVolume(vol);
                    if (volumeSpeakerIcon != null)
                    {
                        if (vol == 0)
                        {
                            volumeSpeakerIcon.Data = Avalonia.Media.Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M12,5 L16,13 M16,5 L12,13");
                        }
                        else
                        {
                            volumeSpeakerIcon.Data = Avalonia.Media.Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M13,5 A4,4 0 0,1 13,11 M16,2 A8,8 0 0,1 16,14");
                        }
                    }
                }
            };

            var speakerHitBox = this.FindControl<Button>("SpeakerHitBox");
            if (speakerHitBox != null)
            {
                speakerHitBox.Click += SpeakerIcon_Click;
            }

            volumeSlider.PointerReleased += (s, e) =>
            {
                double volume = volumeSlider.Value;
                _ = PersistMainVolumeAsync(volume);
            };
        }

        var qualitySlider = QualitySliderCtl;
        if (qualitySlider != null)
        {
            qualitySlider.SetRange(0, FreeVideoStudio.App.ViewModels.QualityLadder.MaxIndex);
            var labels = new System.Collections.Generic.List<string>();
            foreach (var tier in FreeVideoStudio.App.ViewModels.QualityLadder.Tiers)
                labels.Add(tier.Name.ToUpperInvariant());
            qualitySlider.SetLabels(labels);
            qualitySlider.Value = FreeVideoStudio.App.ViewModels.QualityLadder.DefaultIndex;
            qualitySlider.ValueChanged += (s, v) =>
            {
                UpdateEstimatedQuality();
                SaveRecoveryState(label: "change quality", gestureKey: "quality-dial");
            };
            qualitySlider.ValueChangeCompleted += (s, v) =>
            {
                EndProjectGesture();
                RuntimeLog.Info("UI", $"Quality dial resting on '{FreeVideoStudio.App.ViewModels.QualityLadder.NameOf(v)}' (tier {v}).");
            };

            UpdateEstimatedQuality();
        }


        var addMusicButton = AddMusicButtonCtl;
        if (addMusicButton != null)
        {
            addMusicButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked ADD MUSIC button.");
                RuntimeLog.Info("UI", "User clicked ADD MUSIC button (launching Wizard).");

                bool resumeExistingMusic = false;
                if (_isMusicActive)
                {
                    var choice = await ConfirmDialogWindow.AskEditOrRemoveAsync(
                        this,
                        "Edit Background Music?",
                        "This video already has background music.\n\n" +
                        "EDIT reopens the wizard on the final step with your song, start point, volumes and settings still there.\n" +
                        "REMOVE takes the music off the video.\n" +
                        "CANCEL changes nothing.");

                    if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Cancelled) return;

                    if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Remove)
                    {
                        _musicWizardResult = null;
                        SetMusicButtonActive(false);
                        UpdateEstimatedQuality();
                        ShowTacticalFeedback("Music removed");
                        UpdateTimelineMarkers();
                        SaveRecoveryState(label: "remove music");
                        RuntimeLog.Info("UI", "User removed background music via the EDIT/REMOVE prompt.");
                        return;
                    }

                    resumeExistingMusic = true;
                    RuntimeLog.Info("UI", "User chose EDIT on the existing background music.");
                }

                EnsureTrimPointsSet();

                if (ActiveVideoHost?.IpcClient != null)
                    _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");

                SetTimelinePopupsVisible(false);
                var wizard = new MusicWizardWindow(
                    _loadedVideoPath,
                    _trimStartMs,
                    _trimEndMs > 0 ? _trimEndMs : (ActiveVideoHost?.IpcClient?.Duration ?? 0) * 1000,
                    _baseSpeed,
                    BuildExportSpeedSegments(),
                    _voiceOverResult,
                    _cuts,
                    _memePlacements);
                wizard.IsPortraitPreview = PortraitModeCheckboxCtl?.IsChecked == true;

                if (resumeExistingMusic) wizard.InitialState = _musicWizardResult;

                wizard.SourceMeasuredLufs = _sourceMeasuredLufs;
                wizard.GameBusTargetLufs = _applyLoudnessNormalization == true
                    ? FreeVideoStudio.Core.Media.AudioLoudnessProbe.TargetLufs
                    : (double?)null;

                await wizard.ShowDialog(this);
                ReturnToTrimStartPaused();
                SetTimelinePopupsVisible(true);

                if (wizard.Result != null)
                {
                    _musicWizardResult = wizard.Result;
                    NormalizeMusicPlacement(_musicWizardResult);

                    RuntimeLog.Info("UI", $"User added music via wizard: {Path.GetFileName(_musicWizardResult.MusicFilePath)}, ducking={_musicWizardResult.EnableDucking}");
                    RuntimeLog.Debug("UI", $"Music path added via wizard: {_musicWizardResult.MusicFilePath}");

                    SetMusicButtonActive(true);

                    var addMemeCb = AddMemeCheckboxCtl;
                    var memeCb = MemeComboBoxCtl;
                    if (addMemeCb?.IsChecked == true && memeCb?.SelectedItem != null)
                    {
                        _keepMusicDuringMeme = NativeDialog.ShowQuestion(
                            "Would you like the background music to keep on playing as the meme plays or not?\n\nYes! Keep the music playing in the background as the meme video plays.\nNO! Stop the music as the meme plays.",
                            "Background Music Behavior"
                        );
                    }

                    var volSlider = this.FindControl<Avalonia.Controls.Slider>("VolumeSlider");
                    if (volSlider != null)
                    {
                        ApplyMasterVolume((int)volSlider.Value);
                    }

                    UpdateTimelineMarkers();
                }
            };
        }

        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)MobileCheckboxCtl ?? PortraitModeCheckboxCtl;

        if (mobileCheckbox != null)
        {
            UpdatePortraitOverlay();
            mobileCheckbox.IsCheckedChanged += (s, e) =>
            {
                UpdatePortraitOverlay();
                ApplyMemeItemsToCombo(preserveSelection: true);
                SaveRecoveryState(label: "toggle portrait mode");
            };
        }

        var teammatesCb = TeammatesCheckboxCtl;
        if (teammatesCb != null) teammatesCb.IsCheckedChanged += (s, e) => SaveRecoveryState(label: "toggle teammates");

        var spectatingCb = SpectatingCheckboxCtl;
        if (spectatingCb != null) spectatingCb.IsCheckedChanged += (s, e) => SaveRecoveryState(label: "toggle spectating");

        var enableFadeCb = EnableFadeCheckboxCtl;
        if (enableFadeCb != null) enableFadeCb.IsCheckedChanged += (s, e) => SaveRecoveryState(label: "toggle fade");

        var portraitTextInput = PortraitTextInputCtl;
        if (portraitTextInput != null) portraitTextInput.TextChanged += (s, e) => { UpdatePortraitOverlay(); ScheduleRecoveryStateSave(); };

        var addMemeCb = AddMemeCheckboxCtl;
        if (addMemeCb != null)
        {
            addMemeCb.IsCheckedChanged += (s, e) => {
                if (addMemeCb.IsChecked == true) {
                    var cb = MemeComboBoxCtl;
                    if (cb != null) cb.IsDropDownOpen = true;
                }
                SaveRecoveryState(label: "toggle meme");
            };
        }
        
        var memeCb = MemeComboBoxCtl;
        WireMemePlacementCombo();

        if (memeCb != null) memeCb.SelectionChanged += (s, e) => {
            if (memeCb.SelectedItem is MemeItem action && action.IsDownloadAction)
            {
                memeCb.SelectedItem = e.RemovedItems != null && e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
                _ = RunCloudMemeSyncAsync(action.DownloadCategory);
                return;
            }

            if (memeCb.SelectedItem is MemeItem sel)
            {
                RuntimeLog.Info("Memes",
                    $"MemeSelected: type={(sel.IsImage ? "image" : "video")}, file={sel.FileName}, " +
                    $"width={sel.Width}, height={sel.Height}, aspect={sel.AspectRatio:0.###}");
                RuntimeLog.Debug("Memes", $"MemeSelected full path: {sel.FullPath}");
                SyncMemePlacementUi(sel.FullPath);
            }

            SaveRecoveryState(label: "choose meme");
            if (addMemeCb?.IsChecked == true && memeCb.SelectedItem != null && _musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
            {
                _keepMusicDuringMeme = NativeDialog.ShowQuestion(
                    "Would you like the background music to keep on playing as the meme plays or not?\n\nYes! Keep the music playing in the background as the meme video plays.\nNO! Stop the music as the meme plays.",
                    "Background Music Behavior"
                );
            }
        };

        var volSliderForRecovery = VolumeSliderCtl;
        if (volSliderForRecovery != null) volSliderForRecovery.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty) ScheduleRecoveryStateSave();
        };

        UpdateTooltips();
        AddHandler(InputElement.KeyDownEvent, GlobalKeyDownHandler, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, GlobalKeyUpHandler, RoutingStrategies.Tunnel);

        Infrastructure.MemeDirectory.Changed += PopulateMemeComboBox;
        this.Closed += (_, _) => Infrastructure.MemeDirectory.Changed -= PopulateMemeComboBox;

        this.Loaded += async (s, e) => {
            _ = PushAssetsAsync();
            PopulateMemeComboBox();

            bool hadFault = _recovery.CheckFault();
            if (hadFault)
            {
                RuntimeLog.Info("RECOVERY", "Previous crash detected. Prompting user for recovery.");
                bool shouldRestore = await Task.Run(() => NativeDialog.ShowQuestion(
                    "The app was closed unexpectedly during your last session.\n\n" +
                    "Would you like to restore your previous work? This includes your video, " +
                    "trim points, speed settings, music, and all edits exactly as you left them.\n\n" +
                    "Click Yes to recover, or No to start fresh.",
                    "Free Video Studio - Session Recovery"));

                _recovery.AcquireLock();

                if (shouldRestore)
                {
                    RuntimeLog.Info("RECOVERY", "User chose to restore previous session.");
                    _recovery.ActivateSafeMode();
                    await RestoreRecoveryStateAsync();
                    _recovery.DeactivateSafeMode();
                }
                else
                {
                    RuntimeLog.Info("RECOVERY", "User chose to start fresh. Discarding recovery state.");
                    _recovery.ClearState();
                }
            }
            else
            {
                _recovery.AcquireLock();
            }

            var store = new FreeVideoStudio.Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync();


            var newObj = new System.Text.Json.Nodes.JsonObject();
            var preserveKeys = new[] { "schema_version", "MainWindowBounds", "PreviewMonitorWindowBounds", "GranularPreviewMonitorBounds", "MusicWizardPreviewMonitorBounds", "VoiceOverPreviewMonitorBounds", "MergerPreviewMonitorBounds", "VideoMergerBounds", "CropToolBounds", "GranularBounds", "MusicWizardBounds", "SettingsBounds", "VoiceOverWindowBounds", "UploadVideoDirectory", "MergerUploadDirectory", "MergerOutputDirectory", "CropToolUploadDirectory", "CustomMusicDirectory", "WizardVideoVolume", "WizardMusicVolume", "MainVolume" };
            foreach (var key in preserveKeys) {
                if (state.TryGetPropertyValue(key, out var gb)) {
                    newObj[key] = gb?.DeepClone();
                }
            }
            await store.SaveAsync(newObj);

            await InitializeHardwareScanAsync();
            UpdatePortraitOverlay();

            string? pendingOpenWith = OpenWithLaunch.PendingVideoPath;
            if (!string.IsNullOrEmpty(pendingOpenWith))
            {
                OpenWithLaunch.PendingVideoPath = null;
                await LoadVideoIntoEditorAsync(pendingOpenWith, "opened-with");
            }

            SingleInstanceGuard.VideoPathReceived += OnVideoHandedOffFromAnotherLaunch;

            string? settingsFailure = Infrastructure.SettingsManager.LoadFailureMessage;
            if (!string.IsNullOrEmpty(settingsFailure))
            {
                await ErrorReporter.ShowAsync(this, "Settings could not be loaded", settingsFailure,
                    "SettingsManager.Load() failed — see the log entries tagged [Settings].");
            }
        };
    }

    private bool _suppressMemePlacementEvent;

    private void SyncMemePlacementUi(string? memePath)
    {
        var row = this.FindControl<Grid>("MemePlacementRow");
        var combo = this.FindControl<ComboBox>("MemePlacementCombo");
        if (row == null || combo == null) return;

        bool has = !string.IsNullOrWhiteSpace(memePath) && System.IO.File.Exists(memePath);
        row.IsVisible = has;
        if (!has) return;

        _suppressMemePlacementEvent = true;
        combo.SelectedIndex =
            Infrastructure.MemePlacementStore.Get(memePath!) == Infrastructure.MemePlacement.Start ? 1 : 0;
        _suppressMemePlacementEvent = false;
    }

    private void WireMemePlacementCombo()
    {
        var combo = this.FindControl<ComboBox>("MemePlacementCombo");
        if (combo == null) return;

        combo.SelectionChanged += async (_, _) =>
        {
            if (_suppressMemePlacementEvent) return;

            var memeCb = MemeComboBoxCtl;
            if (memeCb?.SelectedItem is not MemeItem sel || sel.IsDownloadAction) return;

            var chosen = combo.SelectedIndex == 1
                ? Infrastructure.MemePlacement.Start
                : Infrastructure.MemePlacement.End;

            Infrastructure.MemePlacementStore.Set(sel.FullPath, chosen);
            SaveRecoveryState(label: "change meme placement");

            if (Infrastructure.MemePlacementStore.ContradictsShippedDefault(sel.FullPath, chosen))
            {
                await ErrorReporter.ShowAsync(this,
                    "This meme usually goes first",
                    $"'{sel.FileName}' is the kind of meme that normally plays BEFORE your gameplay — " +
                    "it is an intro, so putting it at the end can feel out of place.\n\n" +
                    "Your choice has been saved either way. Switch it back to \"at the START\" if that " +
                    "was not what you meant.",
                    "You will not be asked about this meme again.");
            }
        };
    }
}

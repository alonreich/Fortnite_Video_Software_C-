// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.
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
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

public partial class MainWindow
{
    private void WireComponents()
    {
        this.AddHandler(DragDrop.DragEnterEvent, OnVideoDragEnter);
        this.AddHandler(DragDrop.DragOverEvent, OnVideoDragOver);
        this.AddHandler(DragDrop.DragLeaveEvent, OnVideoDragLeave);
        this.AddHandler(DragDrop.DropEvent, OnVideoDrop);

        var radialMenu = this.FindControl<Controls.RadialMenuControl>("RadialMenu");
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

        var overlay = this.FindControl<FortniteVideoSoftware.App.Controls.PhaseOverlayControl>("OverlayLayer");
        if (overlay != null)
        {
            // ══════════════════════════════════════════════════════════════════════════════════
            // EXPORTSESSION_01 — CANCEL SIGNALS. IT DOES NOT DECLARE THE FLOOR CLEAR.
            //
            // This used to call Cancel() and then, in the same breath, StopOverlay() and
            // btn.IsEnabled = true — telling the user the export was over while FFmpeg was still
            // being killed, the reader pipes were still draining and a multi-gigabyte temp directory
            // was still being deleted. That re-armed button is what let a SECOND pipeline start on
            // top of the first. See the EXPORTSESSION_01 block on _exportRunning in
            // MainWindow.axaml.cs for the full three-part failure chain.
            //
            // Cancel now only: (a) signals the token, (b) shows "CANCELLING..." so the click is
            // acknowledged within a frame. The overlay is dismissed and the button re-armed in the
            // ONE place that knows the pipeline has genuinely stopped — the finally in
            // ProcessVideoAsync.
            // ══════════════════════════════════════════════════════════════════════════════════
            overlay.CancelRequested += (s, e) =>
            {
                if (_processCts != null && !_processCts.IsCancellationRequested)
                {
                    try { _processCts.Cancel(); }
                    catch (ObjectDisposedException) { }

                    var btn = this.FindControl<Button>("ProcessButton");
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

        // KEYFOCUS_01 — the hard-coded Space tunnel interceptor that lived here is gone.
        // Play/Pause now flows through PlayPauseButton.Command (and its KeyBinding); the
        // settings-bound gesture is dispatched by GlobalKeyDownHandler → TryExecutePlayPause.
        // While a text input (TextBox / NumericUpDown / ComboBox) holds focus, the key belongs
        // to that control — no interception at the window root.

        SettingsManager.Load();
        ThemeManager.ApplyFromSettings();
        FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.ApplyProfile(FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay);
        ApplyMaskProfileToOverlayUi();

        var settingsBtn = this.FindControl<MenuItem>("MenuSettingsBtn");
        if (settingsBtn != null)
        {
            settingsBtn.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Settings menu button.");
                var settingsWin = new FortniteVideoSoftware.App.Controls.SettingsWindow();
                bool changed = await settingsWin.ShowDialog<bool>(this);
                if (changed)
                {
                    UpdateTooltips();
                    RefreshTransportKeyBindings();   // KEYFOCUS_01 — re-sync transport gestures if keybinds changed
                    // NOMASK_01 — the Mask Overlay tab is the only place the profile changes at
                    // runtime, and SettingsWindow.Save has already called ApplyProfile by now.
                    ApplyMaskProfileToOverlayUi();
                }
            };
        }

        var menuUploadVideo = this.FindControl<MenuItem>("MenuUploadVideo");
        if (menuUploadVideo != null) menuUploadVideo.Click += OnUploadVideoClicked;

        var menuTogglePreview = this.FindControl<MenuItem>("MenuTogglePreviewMonitor");
        if (menuTogglePreview != null) menuTogglePreview.Click += async (s, e) =>
        {
            if (!IsPreviewDetached) await DetachPreviewMonitor();
            else await AttachPreviewMonitor();
        };

        var detachOverlayBtn = this.FindControl<Button>("DetachOverlayButton");
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
            var sheet = this.FindControl<Grid>("ShortcutSheetOverlay");
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
                await FortniteVideoSoftware.App.Controls.SettingsWindow.ShowAboutAsync(this);
            };
        }

        var menuHelpAbout = this.FindControl<MenuItem>("MenuHelpAbout");
        if (menuHelpAbout != null)
        {
            menuHelpAbout.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Help -> About.");
                await FortniteVideoSoftware.App.Controls.SettingsWindow.ShowAboutAsync(this);
            };
        }

        var menuCheckUpdates = this.FindControl<MenuItem>("MenuCheckUpdates");
        if (menuCheckUpdates != null)
        {
            menuCheckUpdates.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Help -> Check for Updates.");
                await FortniteVideoSoftware.App.Services.UpdateService.CheckManualAsync(this);
            };
        }

        var menuHelpShortcuts = this.FindControl<MenuItem>("MenuHelpShortcuts");
        if (menuHelpShortcuts != null)
        {
            menuHelpShortcuts.Click += (s, e) =>
            {
                var sheet = this.FindControl<Grid>("ShortcutSheetOverlay");
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
            var sheet = this.FindControl<Grid>("ShortcutSheetOverlay");
            if (sheet != null) sheet.IsVisible = false;
        };

        UpdateTooltips();
        RefreshTransportKeyBindings();   // KEYFOCUS_01 — attach transport Commands + KeyBindings (post-settings-load)

        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "MainWindowBounds", fitDisplayOnFirstRun: true);   // FIRSTFIT_01

        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;

        InitializeUxInnovations();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        AttachTitleBarDrag();

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => UpdateTimelineMarkers();
        }

        this.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            // THUMB_02 — the thumbnail marker was MISSING from this hand-written list. Every other
            // marker drag suppresses the canvas rebuild while it is in flight, for exactly the
            // reason the thumbnail one needed it most: UpdateTimelineMarkers clears and recreates
            // the marker controls, so a rebuild during a gesture destroys the control holding
            // pointer capture.
            // TIMELINEDRAW_01 — the list itself now lives in exactly one place, MainWindow's
            // IsMarkerGestureActive, so it cannot be transcribed wrongly again. The checks below
            // are kept (they still short-circuit the call) even though the render pass now enforces
            // the same rule for all thirty call sites.
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

        var slider = this.FindControl<Slider>("TimelineSlider");

        var cancelButton = this.FindControl<Button>("CancelButton");
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
                this.FindControl<Button>("CancelButton")?.Flyout?.Hide();
                RuntimeLog.Info("UI", "User backed out of the Cancel button and kept working.");
            };
        }

        var confirmCancelButton = this.FindControl<Button>("ConfirmCancelButton");
        if (confirmCancelButton != null)
        {
            confirmCancelButton.Click += (s, e) =>
            {
                var btn = this.FindControl<Button>("CancelButton");
                btn?.Flyout?.Hide();
                RuntimeLog.Info("UI", "User confirmed Cancel button, closing app.");
                Close();
            };
        }

        var processButton = this.FindControl<Button>("ProcessButton");
        if (processButton != null)
        {
            processButton.Click += async (s, e) =>
            {
                // EXPORTSESSION_01 — the single-flight guard lives inside ProcessVideoAsync so every
                // entry point is covered, not just this one. Do NOT set the caption here: a click
                // the guard rejects must leave the live caption ("CANCELLING...", "PROCESSING... 42%")
                // untouched.
                RuntimeLog.Info("UI", "User clicked PROCESS button.");
                await ProcessVideoAsync(processButton);
            };
        }


        var granularButton = this.FindControl<Button>("GranularButton");
        if (granularButton != null)
        {
            granularButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked GRANULAR SPEED button.");

                // ══════════════════════════════════════════════════════════════════════
                // EDIT3_01 — WAS A ONE-CLICK WIPE WITH NO PROMPT AT ALL.
                //
                // Pressing this button while speeds existed deleted every segment, the freeze and
                // every cut on the spot. The editor it opens has ALWAYS been able to load the
                // existing work — it is handed `_speedSegments`, `_freezeTimeMs` and `_cuts` a few
                // lines below — so "change one of my twelve segments" was possible the whole time
                // and simply had no route to it. The only way in was to destroy the twelve and
                // rebuild them.
                // ══════════════════════════════════════════════════════════════════════
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
                        // ⚠️ `_cuts` are deliberately NOT cleared here. Cuts are a separate feature
                        // (DELETE PARTS) that happens to be edited in the same window, and this
                        // button offers REMOVE. Deleting a user's cuts as a side effect of
                        // clearing their speed segments is exactly the kind of unannounced loss the
                        // prompt above exists to prevent — and the prompt does not mention cuts.
                        SetGranularButtonActive(false);
                        _lastAppliedSpeed = _baseSpeed;
                        if (ActiveVideoHost?.IpcClient != null)
                            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("speed",
                                _baseSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                        InvalidateVoiceOverRecordingForTimingChange();
                        UpdateEstimatedQuality();
                        ShowTacticalFeedback("Speed segments removed");
                        UpdateTimelineMarkers();
                        SaveRecoveryState();
                        RuntimeLog.Info("UI", "User removed all granular speed segments via the EDIT/REMOVE prompt.");
                        return;
                    }

                    RuntimeLog.Info("UI", "User chose EDIT on existing granular speed segments.");
                    // EDIT falls through to the normal open below, which already seeds the editor
                    // with the current segments, freeze, zoom source and cuts.
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

                bool isMobileForZoom = this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked == true;
                var zoomIpc = ActiveVideoHost?.IpcClient;
                string zoomSrcRes = (zoomIpc != null && zoomIpc.VideoWidth > 0 && zoomIpc.VideoHeight > 0)
                    ? $"{zoomIpc.VideoWidth}x{zoomIpc.VideoHeight}"
                    : "1920x1080";
                bool cutsChangedByEditor = false;
                // GRANPROBE_01 — CreateAsync, not `new`. The duration probe this window needs before
                // it can lay out a timeline used to run as a blocking Task.Wait inside the
                // constructor, freezing the UI for up to half a second on every open. It now runs
                // off the dispatcher and the window is constructed once the answer is in hand.
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
                    _cuts,           // CUT_02 — cuts are edited in the Granular editor now
                    _memePlacements); // MEME_06 — and so are memes

                // MEME_06 — handed over rather than re-scanned: this window already scanned the
                // meme folder and probed every file's dimensions on startup.
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

                    // CUT_02 — cuts come home in absolute source ms, same as the segments above.
                    _cuts.Clear();
                    _cuts.AddRange(editor.ResultCuts);
                    cutsChangedByEditor = true;

                    // MEME_06 — memes come home in CLIP-RELATIVE SOURCE seconds and stay that way.
                    // Unlike the cuts above there is no trim offset to re-apply; MemePlacement is
                    // defined in clip-relative time end to end, from this list through
                    // ExportPayload to the FFmpeg graph.
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
                    SaveRecoveryState();

                    // ══════════════════════════════════════════════════════════════════════
                    // CUTS_02 — A CUT MADE IN THE EDITOR IS A CHANGE TO THE WHOLE PROJECT.
                    //
                    // This block used to hand-roll a partial refresh — markers and a save — while
                    // AfterCutsChangedAsync (the method that exists precisely so no caller can
                    // forget a step) was never called on the one path that actually changes the cut
                    // list. Three things were therefore skipped: the cuts were not re-normalised
                    // with the same rules the export uses, the main preview was left parked inside
                    // deleted footage if that is where the playhead happened to be, and the
                    // "your video got shorter, re-run ADD MUSIC" notice never appeared.
                    // ══════════════════════════════════════════════════════════════════════
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
                // NOMASK_01 — the reserved profile has nothing to edit and must not be edited.
                // Crop Tools CLOSES the Main App to launch, so this check has to happen before
                // that hand-off, not inside the Crop Tools window.
                if (BlockCropToolsForNoMaskProfile()) return;
                await SwitchToCompanionAppAsync("--crop-tool", "Crop Tools");
            };
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // DOUBLEFIRE_01 — THE PLAY BUTTON'S Click HANDLER IS GONE. THIS WAS THE WHOLE BUG.
        //
        // KEYFOCUS_01 moved Play/Pause onto `PlayPauseButton.Command` (RefreshTransportKeyBindings
        // assigns `btn.Command = _playPauseCommand`) but LEFT the old `Click` handler attached here.
        // Avalonia raises BOTH on a single press, so every click ran the toggle TWICE:
        //
        //     click -> Command -> TogglePlayPauseTransport()  : paused  -> PLAY
        //           -> Click   -> TogglePlayPauseTransport()  : playing -> PAUSE
        //
        // Net effect of pressing PLAY: the player runs for the few milliseconds between the two
        // calls — one frame — and stops. Pressing it again does exactly the same thing. That is
        // the "play advances one frame at a time and pauses itself, trapped" fault, and it is why
        // MARK START released it: ExecuteMarkStart issues an unconditional `pause=no` rather than a
        // TOGGLE, so running it twice is idempotent and playback survives.
        //
        // It is invisible to reading because the two wirings live in different files and neither
        // is wrong on its own — only their sum is. TRANSPORT_TRACE_01 is what exposed it: two
        // `user-transport` lines, PLAY then PAUSE, in the same second, from one click.
        //
        // ⚠️ A TOGGLE MUST HAVE EXACTLY ONE ACTIVATION PATH. Do not re-add a Click handler to any
        // control that already carries a Command. See TryExecutePlayPause for the guard that now
        // makes a recurrence loud instead of silent.
        // ══════════════════════════════════════════════════════════════════════════════════════

        var setThumbnailButton = this.FindControl<Button>("SetThumbnailButton");
        if (setThumbnailButton != null)
        {
            // ══════════════════════════════════════════════════════════════════════════
            // THUMB_01 — THREE OUTCOMES, NOT TWO.
            //
            // This was a plain toggle: once a thumbnail existed the button could ONLY remove it.
            // So "click the timeline where I want it, then press SET THUMBNAIL" — the obvious way
            // to change your mind — did nothing but delete the marker, and you had to press the
            // button a second time to place a new one. Two presses and a destroyed marker to move
            // a thing you could already see.
            //
            // Now the button does whatever its label says, and the label follows the playhead:
            //   nothing set                     SET THUMBNAIL        place it here
            //   set, playhead somewhere else    MOVE THUMBNAIL HERE  pick it up and put it here
            //   set, playhead on the marker     REMOVE THUMBNAIL     you are asking to clear it
            //
            // The "playhead is on the marker" test is what makes remove reachable without a second
            // control: park on your own thumbnail and the button offers to clear it. See
            // UpdateThumbnailButtonState.
            // ══════════════════════════════════════════════════════════════════════════
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
                    _isThumbnailMarkerSelected = true;   // leave it armed for the arrow keys

                    PlayUiSound();
                    ShowTacticalFeedback($"📸 {(moved ? "Moved to " : "")}{TimeSpan.FromSeconds(time):mm\\:ss\\.ff}");
                    ShowTimelineGlow(_thumbnailPosMs, Avalonia.Media.Brushes.DeepSkyBlue);
                }

                UpdateThumbnailButtonState();
                UpdateTimelineMarkers();
                UpdateEstimatedQuality();
                SaveRecoveryState();
            };
        }

        var voiceOverButton = this.FindControl<Button>("VoiceOverButton");
        if (voiceOverButton != null)
        {
            voiceOverButton.Click += async (s, e) =>
            {
                if (string.IsNullOrEmpty(_loadedVideoPath)) return;

                // EDIT3_01 — the shared three-way prompt. This screen invented it; the Granular
                // Speed and Add Music buttons now use the SAME method rather than their own
                // wording, colours and keyboard behaviour.
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
                    _cuts,          // CUTS_02 — deleted sections are shown, skipped and mapped
                    _memePlacements) // MEME_06 — so a take after a meme maps to the right instant
                {
                    InitialState = _voiceOverResult
                };
                dialog.SourceMeasuredLufs = _sourceMeasuredLufs;
                // ZOOMLIVE_06 — the studio simulates the zoom now, and portrait changes what the
                // usable area is, exactly as it does for the Music Wizard's copy of this line.
                dialog.IsPortraitPreview =
                    this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked == true;
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

        // KEYFOCUS_01 — MARK START / MARK END now bind through commands (see
        // MainWindow.Shortcuts.RefreshTransportKeyBindings); the click behaviour moved
        // verbatim into ExecuteMarkStart / ExecuteMarkEnd.

        // KEYFOCUS_01 — MARK END wiring moved to _markEndCommand (MainWindow.Shortcuts.ExecuteMarkEnd).

        var timelineSlider = this.FindControl<Slider>("TimelineSlider");
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

            var timelineOverlay = this.FindControl<Border>("TimelineOverlay");
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

        var mainSpeedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("MainSpeedSlider");
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
                SaveRecoveryState();
            };
            mainSpeedSlider.ValueChangeCompleted += (s, e) =>
            {
                RuntimeLog.Info("UI", $"Speed slider final resting value: {e / 10.0:F1}x");
            };
            UpdateSpeedLabel();
        }

        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
        var volumeBadgeText = this.FindControl<TextBlock>("VolumeBadgeText");
        var volumeSpeakerIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("VolumeSpeakerIcon");
        if (volumeSlider != null && volumeBadgeText != null)
        {
            volumeSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty && e.NewValue != null)
                {
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
                try
                {
                    new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                        .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                        {
                            ["MainVolume"] = volumeSlider.Value
                        });
                }
                catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
            };
        }

        var qualitySlider = this.FindControl<SpinningWheelSlider>("QualitySlider");
        if (qualitySlider != null)
        {
            // QUALITY_01 — the dial's stops ARE the quality words. It used to read "5MB", "10MB"
            // ... "100MB", "ORIGINAL QUALITY", which asked the user to solve for the thing they
            // wanted instead of picking it.
            qualitySlider.SetRange(0, FortniteVideoSoftware.App.ViewModels.QualityLadder.MaxIndex);
            var labels = new System.Collections.Generic.List<string>();
            foreach (var tier in FortniteVideoSoftware.App.ViewModels.QualityLadder.Tiers)
                labels.Add(tier.Name.ToUpperInvariant());
            qualitySlider.SetLabels(labels);
            qualitySlider.Value = FortniteVideoSoftware.App.ViewModels.QualityLadder.DefaultIndex;
            // QUALITY_04 / SIZEESTIMATE_01 — the estimate and tooltip bind to ExportViewModel.
            // The shared background estimator publishes both from the same snapshot.
            qualitySlider.ValueChanged += (s, v) =>
            {
                UpdateEstimatedQuality();
                SaveRecoveryState();
            };
            qualitySlider.ValueChangeCompleted += (s, v) =>
            {
                RuntimeLog.Info("UI", $"Quality dial resting on '{FortniteVideoSoftware.App.ViewModels.QualityLadder.NameOf(v)}' (tier {v}).");
            };

            // QUALITY_04 — prime the readout and the tooltip once at startup. Without this the
            // strip under the dial stays blank and the tooltip stays generic until the user
            // happens to TOUCH the dial — which is precisely the user who never touches it.
            UpdateEstimatedQuality();
        }


        var addMusicButton = this.FindControl<Button>("AddMusicButton");
        if (addMusicButton != null)
        {
            addMusicButton.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked ADD MUSIC button.");
                RuntimeLog.Info("UI", "User clicked ADD MUSIC button (launching Wizard).");

                // EDIT3_01 — was also a silent one-click wipe. The wizard now reopens on the
                // existing placement at PHASE 3, which is the screen the placement actually lives
                // on (song start, volumes, ducking, carving, looping). Phases 1 and 2 stay
                // reachable with BACK, so swapping the song itself is still one click away.
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
                        SaveRecoveryState();
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
                    _cuts,          // CUTS_02 — music is laid against the video's REAL length
                    _memePlacements); // MEME_06 — which memes make LONGER, not shorter
                wizard.IsPortraitPreview = this.FindControl<ToggleSwitch>("PortraitModeCheckbox")?.IsChecked == true;

                // EDIT3_01 — hand the existing placement over so the wizard opens on it. Must be
                // set before ShowDialog: the wizard consumes it in its Loaded handler.
                if (resumeExistingMusic) wizard.InitialState = _musicWizardResult;

                // PREVIEW_04 — let the wizard preview the balance the EXPORT will produce, instead
                // of two raw files at slider level. Without these the music previews 10-15 dB above
                // the game (loud commercial master vs quiet capture), the user compensates on the
                // slider, and the exported mix ends up with the music far too low.
                wizard.SourceMeasuredLufs = _sourceMeasuredLufs;
                wizard.GameBusTargetLufs = _applyLoudnessNormalization == true
                    ? FortniteVideoSoftware.Core.Media.AudioLoudnessProbe.TargetLufs
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

                    var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
                    var memeCb = this.FindControl<ComboBox>("MemeComboBox");
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

        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)this.FindControl<CheckBox>("MobileCheckbox") ?? this.FindControl<ToggleSwitch>("PortraitModeCheckbox");

        if (mobileCheckbox != null)
        {
            UpdatePortraitOverlay();
            mobileCheckbox.IsCheckedChanged += (s, e) =>
            {
                UpdatePortraitOverlay();
                ApplyMemeItemsToCombo(preserveSelection: true);
                SaveRecoveryState();
            };
        }

        var teammatesCb = this.FindControl<ToggleSwitch>("TeammatesCheckbox");
        if (teammatesCb != null) teammatesCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var spectatingCb = this.FindControl<ToggleSwitch>("SpectatingCheckbox");
        if (spectatingCb != null) spectatingCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var enableFadeCb = this.FindControl<ToggleSwitch>("EnableFadeCheckbox");
        if (enableFadeCb != null) enableFadeCb.IsCheckedChanged += (s, e) => SaveRecoveryState();

        var portraitTextInput = this.FindControl<TextBox>("PortraitTextInput");
        if (portraitTextInput != null) portraitTextInput.TextChanged += (s, e) => { UpdatePortraitOverlay(); ScheduleRecoveryStateSave(); };

        var addMemeCb = this.FindControl<ToggleSwitch>("AddMemeCheckbox");
        if (addMemeCb != null)
        {
            addMemeCb.IsCheckedChanged += (s, e) => {
                if (addMemeCb.IsChecked == true) {
                    var cb = this.FindControl<ComboBox>("MemeComboBox");
                    if (cb != null) cb.IsDropDownOpen = true;
                }
                SaveRecoveryState();
            };
        }
        
        var memeCb = this.FindControl<ComboBox>("MemeComboBox");
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

            SaveRecoveryState();
            if (addMemeCb?.IsChecked == true && memeCb.SelectedItem != null && _musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
            {
                _keepMusicDuringMeme = NativeDialog.ShowQuestion(
                    "Would you like the background music to keep on playing as the meme plays or not?\n\nYes! Keep the music playing in the background as the meme video plays.\nNO! Stop the music as the meme plays.",
                    "Background Music Behavior"
                );
            }
        };

        var volSliderForRecovery = this.FindControl<Slider>("VolumeSlider");
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
                    "Fortnite Video Software - Session Recovery"));

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

            var store = new FortniteVideoSoftware.Core.Ipc.StateTransferStore();
            var state = await store.LoadAsync();

            try
            {
                if (state.TryGetPropertyValue("returned_from_crop_tool", out var rfct) && rfct?.GetValue<bool>() == true)
                {
                    RuntimeLog.Info("HANDOFF", "Returned from Crop Tools. Crop overlay configuration reloaded from the active profile.");
                }
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

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

            var memeCb = this.FindControl<ComboBox>("MemeComboBox");
            if (memeCb?.SelectedItem is not MemeItem sel || sel.IsDownloadAction) return;

            var chosen = combo.SelectedIndex == 1
                ? Infrastructure.MemePlacement.Start
                : Infrastructure.MemePlacement.End;

            Infrastructure.MemePlacementStore.Set(sel.FullPath, chosen);
            SaveRecoveryState();

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

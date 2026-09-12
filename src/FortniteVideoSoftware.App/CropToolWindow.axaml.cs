using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IOPath = System.IO.Path;

namespace FortniteVideoSoftware.App;

public partial class CropToolWindow : Window, System.ComponentModel.INotifyDataErrorInfo
{
    public static readonly StyledProperty<string> RoleNameProperty =
        AvaloniaProperty.Register<CropToolWindow, string>(nameof(RoleName), defaultValue: "");

    public string RoleName
    {
        get => GetValue(RoleNameProperty);
        set => SetValue(RoleNameProperty, value);
    }

    public static readonly StyledProperty<string> NewMaskOverlayNameProperty =
        AvaloniaProperty.Register<CropToolWindow, string>(nameof(NewMaskOverlayName), defaultValue: "");

    public string NewMaskOverlayName
    {
        get => GetValue(NewMaskOverlayNameProperty);
        set => SetValue(NewMaskOverlayNameProperty, value);
    }

    public event System.EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => string.IsNullOrWhiteSpace(RoleName);

    public System.Collections.IEnumerable GetErrors(string? propertyName)
    {
        if (propertyName == nameof(RoleName) && string.IsNullOrWhiteSpace(RoleName))
            yield return "Element name cannot be empty.";
        else
            yield break;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RoleNameProperty)
        {
            ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(nameof(RoleName)));
        }
    }
    private const double PortraitWidth = CoordinateConstants.PortraitW;
    private const double PortraitHeight = CoordinateConstants.PortraitH;
    private const double ContentTop = CoordinateConstants.UIPaddingTop;
    private const double ContentBottom = CoordinateConstants.PortraitH - CoordinateConstants.UIPaddingBottom;
    private const double MinSelectionSize = 10;
    private const double MinItemSize = 20;
    private const double HandleSize = 24;
    private const double SnapThreshold = 8;

    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private readonly FortniteVideoSoftware.Core.Infrastructure.RecoveryManager _recovery = new FortniteVideoSoftware.Core.Infrastructure.RecoveryManager(ApplicationPaths.CreateDefault());
    private readonly string? _initialVideoPath;
    private readonly ObservableCollection<LayerEntry> _layers = new();
    private readonly List<CropEditorItem> _items = new();
    private readonly List<Control> _candidateControls = new();
    private readonly List<Control> _guideControls = new();
    private readonly List<string> _tempFiles = new();
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();

    private MpvVideoView? _videoHost;
    private Canvas? _sourceCanvas;
    private Canvas? _portraitCanvas;
    private Image? _snapshotImage;
    private Image? _composerBackgroundImage;
    // LAYERSPANE_01 - the LAYERS ListBox was removed from the AXAML. `_layers` is KEPT: it is the
    // z-order model that MoveSelectedLayer, RefreshLayerList and the save path all read, and it is
    // what the right-click menu re-sorts. Only the visual list went away. This field stays declared
    // and always null so the SelectItem / RefreshLayerList null-guards keep documenting that.
    private ListBox? _layerList;
    private Slider? _timelineSlider;
    private TextBlock? _currentTimeLabel;
    private TextBlock? _totalTimeLabel;
    private TextBlock? _statusLabel;
    private TextBlock? _goalLabel;
    private Canvas? _timelineCanvas;
    private TextBlock? _selectionInfo;

    // ZOOM_01 (F2) — the frozen-frame viewport. _snapshotZoomHost carries the LayoutTransform that
    // scales the 1:1 source surface; SourceCanvas itself is NEVER resized, so every existing
    // GetPosition(SourceCanvas) call keeps returning true source pixels.
    private ScrollViewer? _snapshotScroll;
    private LayoutTransformControl? _snapshotZoomHost;
    private TextBlock? _zoomLabel;

    // GATE_01 (F3) — blank-start gating.
    private Border? _profileGate;
    private TextBlock? _profileStateLabel;

    private Rectangle? _selectionRect;
    private SourceRect? _sourceSelection;
    private Point _sourceSelectionStart;
    private bool _isDrawingSourceSelection;

    private CropEditorItem? _selectedItem;
    private CropEditorItem? _activeEditItem;
    private ComposerEditMode _composerEditMode = ComposerEditMode.None;
    private Point _editPointerStart;
    private double _editStartX;
    private double _editStartY;
    private double _editStartWidth;
    private double _editStartHeight;
    private EditorSnapshot? _editStartSnapshot;

    private string? _videoPath;
    private string? _snapshotPath;
    private string _originalResolution = "1920x1080";
    private int _snapshotWidth = 1920;
    private int _snapshotHeight = 1080;
    private double _durationMs;
    private bool _isTimerUpdatingSlider;
    private bool _isSeeking;
    private double? _nextSeekTarget;
    private Avalonia.Threading.DispatcherTimer? _playheadBadgeTimer;
    private bool _isMpvStarted;
    private bool _dirty;
    private bool _restoringSnapshot;
    private bool _suppressLayerSelection;
    private bool _isSafeToClose;
    private DispatcherTimer? _timelineTimer;

    /// <summary>
    /// GATE_01 (F3) — the profile this session is editing, or null when none has been chosen yet.
    /// Null is the START state and it is load-bearing: while it is null nothing may be loaded,
    /// edited or saved, because SaveConfigAsync ends in
    /// MaskOverlayManager.SyncActiveProfileFromCurrentConfig(), which writes the live crop config
    /// straight over SettingsManager.ActiveMaskOverlay's file. Before this field existed the window
    /// opened already pointed at whatever profile was last active and one click on FINISH &amp; SAVE
    /// overwrote a shipped preset.
    /// </summary>
    private string? _activeProfile;

    /// <summary>GATE_01 — the initial video is held until a profile exists to load it against.</summary>
    private string? _pendingInitialVideoPath;

    // ZOOM_01 (F2) — fit mode recomputes on every viewport resize; a manual factor does not.
    private bool _snapshotFitMode = true;
    private double _snapshotZoomFactor = 1.0;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 4.0;

    /// <summary>
    /// AUTOZOOM_01 - set once the user drives the zoom themselves (the wheel, or any zoom button
    /// other than FIT). From then on AutoZoomToSelection re-centres but does not re-magnify.
    /// FIT clears it, which is the deliberate way back to automatic behaviour.
    /// </summary>
    private bool _userZoomed;

    private static readonly HudRole[] Roles =
    [
        new("loot", "Loot Area", 10, 680, 1370),
        new("stats", "Mini Map + Stats", 30, 730, 150),
        new("normal_hp", "Own Health Bar (HP)", 20, 30, 1620),
        new("boss_hp", "Boss HP (For When You Are The Boss Character)", 20, 30, 1620),
        new("team", "Teammates health Bars (HP)", 40, 30, 250),
        new("spectating", "Spectating Eye", 100, 30, 1300),
    ];

    private static readonly Dictionary<string, HudRole> RoleByKey = Roles.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ROLEPOPUP_01 - elements beyond the six built-ins: the ones "+ New element" creates, plus any
    /// key found in a profile that this build does not have a built-in for.
    ///
    /// <see cref="Roles"/> is a hardcoded Fortnite array and cannot grow, which used to mean a
    /// custom element could be SAVED and then never loaded back, because every read path iterated
    /// that array. Anything that enumerates elements must go through <see cref="AllRoles"/>.
    /// </summary>
    private readonly List<HudRole> _customRoles = new();

    private IEnumerable<HudRole> AllRoles => Roles.Concat(_customRoles);

    private bool TryGetRole(string key, out HudRole role)
    {
        if (RoleByKey.TryGetValue(key, out role!)) return true;
        foreach (HudRole custom in _customRoles)
        {
            if (string.Equals(custom.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                role = custom;
                return true;
            }
        }
        role = default!;
        return false;
    }

    /// <summary>
    /// ROLEPOPUP_01 - adds an element name to this session's list, once.
    /// DefaultX/Y of -1 means "no preferred portrait position", so AddCurrentSelection places it
    /// where the geometry says rather than at a made-up spot.
    /// </summary>
    /// <summary>
    /// ROLEPOPUP_01 / A3 - teaches this session every element key a profile document contains.
    ///
    /// Keys are stored as `own_ammo`; the popup shows `Own Ammo`. RegisterCustomRole derives the
    /// key back from the display name with the same lowercase+underscore rule, so the round trip
    /// is stable and a profile can be opened, edited and saved without renaming anything.
    /// Entries with no usable rectangle are skipped - a key zeroed by a delete
    /// (crops[key] = [0,0,0,0], see DELETESET_01) is a tombstone, not an element.
    /// </summary>
    private void AdoptRolesFromConfig(JsonObject section)
    {
        foreach (var pair in section)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            if (TryGetRole(pair.Key, out _)) continue;
            if (pair.Value is not JsonArray arr || arr.Count < 4) continue;
            if (ReadInt(arr[0], 0) <= 1 || ReadInt(arr[1], 0) <= 1) continue;

            RegisterCustomRole(PrettifyRoleKey(pair.Key));
        }
    }

    /// <summary>ROLEPOPUP_01 - `own_ammo` -&gt; `Own Ammo`, for display in the chooser.</summary>
    private static string PrettifyRoleKey(string key)
    {
        string[] words = key.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    private HudRole RegisterCustomRole(string displayName)
    {
        string key = displayName.Trim().ToLowerInvariant().Replace(" ", "_");
        if (TryGetRole(key, out HudRole existing)) return existing;

        var role = new HudRole(key, displayName.Trim(), 50, -1, -1);
        _customRoles.Add(role);
        RuntimeLog.Info("CROP", $"New HUD element registered for this profile: '{displayName}' (key={key}).");
        return role;
    }

    public CropToolWindow() : this(null)
    {
    }

    public CropToolWindow(string? initialVideoPath)
    {
        _initialVideoPath = string.IsNullOrWhiteSpace(initialVideoPath) ? null : initialVideoPath;

        InitializeComponent();

        // GRIP_01 — the bottom-right resize corner. These windows are borderless, so the OS
        // draws no resize frame: without this there is nothing to grab and nothing telling the
        // user the Crop Tools can be resized at all. One shared implementation — see
        // Controls/WindowResizeGrip.cs for why it is not per-window code.
        Controls.WindowResizeGrip.Attach(this, "Drag to resize the Crop Tools");
        _recovery.AcquireLock();
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "CropToolBounds", fitDisplayOnFirstRun: true);   // FIRSTFIT_01
        FindControls();
        AttachTitleBarDrag();
        WireEvents();
        InitializeHistory();

        // GATE_01 (F3) — start blank and locked. Step 0 is a real user-facing stage now.
        _pendingInitialVideoPath = _initialVideoPath;
        ApplyWizardChrome(cropping: false);   // WIZCOLLAPSE_01 - dots visible until cropping starts
        SetProfileGate(unlocked: false);
        SetWizardState(0, "Choose Profile", "Pick the profile you want to edit, up at the top.");

        Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.CropToolKey, Controls.CoachTours.CropTool);

        Loaded += async (_, _) =>
        {
            // MPV is started eagerly because spinning up the player takes long enough to be felt,
            // and starting it costs nothing while the screen is gated. What is NOT done here any
            // more is reading the crop config: RehydrateSavedLayersAsync and
            // LoadExistingPlaceholdersAsync used to run unconditionally on Loaded, which meant the
            // window silently opened holding the live contents of whatever profile was last active.
            // Both now run from OnProfileChosenAsync, after a deliberate choice. (GATE_01)
            await InitializeMpvAsync();
        };
    }


    /// <summary>
    /// TONE_01 — the HUD ghost fill, at the caller's alpha.
    ///
    /// This used to be <c>Color.FromArgb(alpha, 0, 255, 0)</c> — pure lime, hardcoded in two
    /// places, and completely immune to the theme. It was the harshest colour in the whole suite
    /// and the single most visible thing the red/green audit found. Reading AppSuccessColor means
    /// muting the token now mutes the ghosts too, in both themes, without touching this file again.
    /// </summary>
    private Color GhostFillColor(byte alpha)
    {
        var c = Infrastructure.ThemeResources.Colour(this, "AppSuccessColor", Color.FromRgb(63, 156, 107));
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void FindControls()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        _sourceCanvas = this.FindControl<Canvas>("SourceCanvas");
        _portraitCanvas = this.FindControl<Canvas>("PortraitCanvas");
        _snapshotImage = this.FindControl<Image>("SnapshotImage");
        _composerBackgroundImage = this.FindControl<Image>("ComposerBackgroundImage");
        _layerList = null;   // LAYERSPANE_01 - no such control any more; the guards below handle it.
        _timelineSlider = this.FindControl<Slider>("TimelineSlider");
        _currentTimeLabel = this.FindControl<TextBlock>("CurrentTimeLabel");
        _totalTimeLabel = this.FindControl<TextBlock>("TotalTimeLabel");
        _statusLabel = this.FindControl<TextBlock>("StatusLabel");
        _goalLabel = this.FindControl<TextBlock>("GoalLabel");
        _timelineCanvas = this.FindControl<Canvas>("CropTimelineScaleCanvas");
        _selectionInfo = this.FindControl<TextBlock>("SelectionInfo");
        _snapshotScroll = this.FindControl<ScrollViewer>("SnapshotScroll");                 // ZOOM_01
        _snapshotZoomHost = this.FindControl<LayoutTransformControl>("SnapshotZoomHost");   // ZOOM_01
        _zoomLabel = this.FindControl<TextBlock>("ZoomLabel");                              // ZOOM_01
        _profileGate = this.FindControl<Border>("ProfileGateOverlay");                      // GATE_01
        _profileStateLabel = this.FindControl<TextBlock>("ProfileStateLabel");              // GATE_01
    }

    private void WireEvents()
    {
        if (_sourceCanvas != null)
        {
            _sourceCanvas.PointerPressed += SourceCanvas_PointerPressed;
            _sourceCanvas.PointerMoved += SourceCanvas_PointerMoved;
            _sourceCanvas.PointerReleased += SourceCanvas_PointerReleased;
        }

        if (_portraitCanvas != null)
        {
            _portraitCanvas.PointerPressed += (_, e) =>
            {
                if (ReferenceEquals(e.Source, _portraitCanvas))
                {
                    SelectItem(null);
                }
            };
        }

        if (_layerList != null)
        {
            _layerList.ItemsSource = _layers;
            _layerList.SelectionChanged += (_, _) =>
            {
                if (_suppressLayerSelection)
                {
                    return;
                }

                if (_layerList.SelectedItem is LayerEntry entry)
                {
                    SelectItem(_items.FirstOrDefault(i => i.RoleKey == entry.RoleKey), updateLayerList: false);
                }
            };
        }

        this.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, OnVideoDragOver);
        this.AddHandler(Avalonia.Input.DragDrop.DropEvent, OnVideoDrop);

        ButtonClick("OpenVideoButton", async (_, _) => await OpenVideoAsync());
        ButtonClick("SnapshotButton", async (_, _) => await TakeSnapshotAsync());

        // MAGICWAND_01 (F5) — the handler is kept, the button is not shown. What this used to do
        // was not detection of any kind: ShowMagicWandCandidates draws six boxes at hardcoded
        // fractions of the frame (0.65, 0.02, 0.32, 0.28 …) and calls them "candidates". On any
        // HUD that does not match those exact fractions it is confidently wrong, which is worse
        // than absent. The real thing analyses the frame; until it exists the button stays hidden.
        // See ShowSnapshotPanel, which no longer un-hides it.
        ButtonClick("MagicWandButton", (_, _) => ShowMagicWandCandidates());

        // ZOOM_01 (F2)
        ButtonClick("ZoomFitButton", (_, _) => ApplySnapshotZoom(null));
        ButtonClick("ZoomActualButton", (_, _) => ApplySnapshotZoom(1.0));
        ButtonClick("ZoomOutButton", (_, _) => ApplySnapshotZoom(CurrentZoom() / 1.25));
        ButtonClick("ZoomInButton", (_, _) => ApplySnapshotZoom(CurrentZoom() * 1.25));

        // ZOOM_01 — fit is a RELATIONSHIP to the viewport, not a number, so it has to be
        // recomputed whenever the viewport changes. A manual zoom is a number and is left alone.
        if (_snapshotScroll != null)
        {
            _snapshotScroll.SizeChanged += (_, _) =>
            {
                if (_snapshotFitMode && _snapshotPath != null) ApplySnapshotZoom(null);
            };

            // WHEELZOOM_01 — the wheel zooms the frame; it does not scroll it.
            //
            // Tunnel, not Bubble: a ScrollViewer consumes PointerWheelChanged itself, so a
            // bubbling handler would only ever see the leftovers and the view would scroll
            // instead of zoom. Tunnelling gets the event on the way DOWN, before the
            // ScrollViewer's own handling, and Handled = true stops it there.
            //
            // On an image canvas the wheel means zoom to everyone who has used any image editor,
            // and it is the gesture that replaces hunting for scrollbars. The zoom is anchored
            // under the cursor (see below) so the thing you are pointing at does not run away.
            _snapshotScroll.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnSnapshotWheel,
                RoutingStrategies.Tunnel,
                handledEventsToo: false);
        }
        ButtonClick("PlayPauseButton", async (_, _) => await TogglePlayPauseAsync());
        ButtonClick("DeleteMenuButton", (_, _) =>
        {
            if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmCropToolDelete)
            {
                DeleteSelectedItem();
            }
            else
            {
                var btn = this.FindControl<Button>("DeleteMenuButton");
                var pnl = this.FindControl<StackPanel>("DeleteConfirmPanel");
                if (btn != null && pnl != null) { btn.IsVisible = false; pnl.IsVisible = true; }
            }
        });
        
        ButtonClick("ConfirmDeleteButton", (_, _) =>
        {
            DeleteSelectedItem();
            var btn = this.FindControl<Button>("DeleteMenuButton");
            var pnl = this.FindControl<StackPanel>("DeleteConfirmPanel");
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });

        ButtonClick("CancelDeleteButton", (_, _) =>
        {
            var btn = this.FindControl<Button>("DeleteMenuButton");
            var pnl = this.FindControl<StackPanel>("DeleteConfirmPanel");
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });
        
        ButtonClick("UndoButton", (_, _) => Undo());
        ButtonClick("RedoButton", (_, _) => Redo());
        ButtonClick("RaiseButton", (_, _) => MoveSelectedLayer(1));
        ButtonClick("LowerButton", (_, _) => MoveSelectedLayer(-1));
        
        ButtonClick("CropToolHelpButton", (_, _) => Controls.CoachOverlay.Replay(this));

        // ROLEPOPUP_01 - the inline "+ New element" row. Enter commits, so the whole naming flow
        // is type-and-press without reaching for the mouse; the ADD button is there for people who
        // do not expect Enter to mean anything.
        ButtonClick("RolePopupNewOk", async (_, _) => await CommitNewRoleAsync());
        var newNameBox = this.FindControl<TextBox>("RolePopupNewName");
        if (newNameBox != null)
        {
            newNameBox.KeyDown += async (_, ke) =>
            {
                if (ke.Key is Key.Enter or Key.Return)
                {
                    ke.Handled = true;
                    await CommitNewRoleAsync();
                }
            };
        }

        ButtonClick("ResetMenuButton", (_, _) =>
        {
            if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmCropToolReset)
            {
                ResetWorkingState();
            }
            else
            {
                var btn = this.FindControl<Button>("ResetMenuButton");
                var pnl = this.FindControl<StackPanel>("ResetConfirmPanel");
                if (btn != null && pnl != null) { btn.IsVisible = false; pnl.IsVisible = true; }
            }
        });
        
        ButtonClick("ConfirmResetButton", (_, _) =>
        {
            ResetWorkingState();
            var btn = this.FindControl<Button>("ResetMenuButton");
            var pnl = this.FindControl<StackPanel>("ResetConfirmPanel");
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });

        ButtonClick("CancelResetButton", (_, _) =>
        {
            var btn = this.FindControl<Button>("ResetMenuButton");
            var pnl = this.FindControl<StackPanel>("ResetConfirmPanel");
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });
        ButtonClick("ReturnButton", async (_, _) => await ReturnToMainAppAsync());
        ButtonClick("SaveButton", async (button, _) => await SaveAndReturnAsync(button));
        BuildMaskOverlayUi();

        // GHOSTKILL_01 — "Show Saved Crops" now drives the REAL items, because the ghosts are gone.
        //
        // What this used to toggle was _placeholderControls: non-interactive green rectangles drawn
        // by LoadExistingPlaceholdersAsync for saved crops. That method SKIPPED any role already in
        // _items — and RehydrateSavedLayersAsync had already loaded every saved crop as a real
        // editable item — so the ghost list was ALWAYS EMPTY and the checkbox toggled nothing at
        // all. Two representations of one thing, one of which could never appear: defect A7.
        //
        // Saved crops are simply items now, flagged FromSavedConfig. The checkbox hides or shows
        // them and the slider dims them so old and new are still tellable apart, but while shown
        // they drag, resize and save exactly like anything drawn this session.
        var showPlaceholders = this.FindControl<CheckBox>("ShowPlaceholders");
        if (showPlaceholders != null)
        {
            showPlaceholders.IsCheckedChanged += (_, _) => ApplySavedCropVisibility();
        }

        var opacitySlider = this.FindControl<Slider>("PlaceholderOpacitySlider");
        if (opacitySlider != null)
        {
            opacitySlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty) ApplySavedCropVisibility();
            };
        }

        if (_timelineSlider != null)
        {
            _timelineSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && e.NewValue is double newValue && !_isTimerUpdatingSlider)
                {
                    double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                    if (duration > 0 && _videoHost?.IpcClient != null)
                    {
                        double targetTime = (newValue / 100.0) * duration;
                        ShowPlayheadBadge(targetTime, newValue);
                    }
                }
            };
            
            _timelineSlider.PointerReleased += (s, e) =>
            {
                double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                if (duration > 0 && _videoHost?.IpcClient != null)
                {
                    double targetTime = (_timelineSlider.Value / 100.0) * duration;
                    _ = SeekInternal(targetTime);
                }
            };
        }

        var timelinePanel = this.FindControl<Border>("TimelinePanel");
        var timelineCanvas = this.FindControl<Canvas>("CropTimelineMarkersCanvas");

        Controls.TimelineKnob.Attach(timelineCanvas, _timelineSlider);

        if (timelinePanel != null && timelineCanvas != null && _timelineSlider != null)
        {
            bool isScrubbingTimeline = false;
            timelinePanel.PointerPressed += (s, e) => {
                if (e.GetCurrentPoint(timelinePanel).Properties.IsLeftButtonPressed) {
                    isScrubbingTimeline = true;
                    e.Pointer.Capture(timelinePanel);
                    SeekTimelineFromPointer(e, timelineCanvas, _timelineSlider);
                }
            };
            timelinePanel.PointerMoved += (s, e) => {
                if (isScrubbingTimeline && e.GetCurrentPoint(timelinePanel).Properties.IsLeftButtonPressed) {
                    SeekTimelineFromPointer(e, timelineCanvas, _timelineSlider);
                }
            };
            timelinePanel.PointerReleased += (s, e) => {
                isScrubbingTimeline = false;
                e.Pointer.Capture(null);
            };
        }

        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timelineTimer.Tick += (_, _) => UpdateTimelineUi();
        _timelineTimer.Start();
    }

    private void BuildMaskOverlayUi()
    {
        var combo = this.FindControl<ComboBox>("CropToolMaskOverlayCombo");
        if (combo != null)
        {
            var profiles = FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
            combo.ItemsSource = profiles;

            // GATE_01 (F3) - NO preselection. The line removed here was
            //     combo.SelectedItem = SettingsManager.Instance.ActiveMaskOverlay;
            // and it is the origin of this window's most dangerous behaviour: the window opened
            // already LIVE on a shipped preset, while FINISH & SAVE runs through
            // SyncActiveProfileFromCurrentConfig(), which writes the live crop config straight over
            // that preset's file. A user who opened Crop Tools only to look around could destroy
            // "Fortnite" in two clicks. The ComboBox's PlaceholderText now reads
            // "Choose a profile to edit..." until the user makes a deliberate choice.
            // Do NOT restore the preselection. ActiveMaskOverlay is still consulted at SAVE time,
            // which is the only moment it is actually needed.
            combo.SelectedItem = null;

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is not string selected) return;

                // Compare against _activeProfile - what is really LOADED - not against
                // SettingsManager.ActiveMaskOverlay, which is global state the Main App also
                // writes. This equality guard is also what absorbs the re-entrant pass caused by
                // the two `combo.SelectedItem = _activeProfile` rollbacks below.
                if (string.Equals(selected, _activeProfile, StringComparison.OrdinalIgnoreCase)) return;

                // NOMASK_01 - the reserved HUD-free profile cannot be selected HERE.
                // Switching to it inside Crop Tools would make every subsequent save call
                // SyncActiveProfileFromCurrentConfig against a profile that must stay empty.
                // The Main App blocks launching Crop Tools while it is active; this stops the
                // other direction. It is switched from Settings -> Mask Overlay instead.
                if (FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.IsNoMask(selected))
                {
                    combo.SelectedItem = _activeProfile;
                    SetStatus("\"" + FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.NoMaskProfileName +
                              "\" has no elements to edit. Switch to it from Settings \u2192 Mask Overlay in the main app.");
                    return;
                }

                if (_dirty && _items.Count > 0)
                {
                    bool discard = NativeDialog.ShowQuestion(
                        "You have unsaved mask changes.\n\nSwitching profiles will discard them.\n\nClick Yes to discard the changes and switch, or No to stay on the current profile.",
                        "Unsaved Changes");
                    if (!discard)
                    {
                        combo.SelectedItem = _activeProfile;
                        return;
                    }
                }

                _ = OnProfileChosenAsync(selected);
            };
        }

        var btn = this.FindControl<Button>("CreateMaskOverlayBtn");
        var txt = this.FindControl<TextBox>("NewMaskOverlayTextBox");
        if (btn != null && txt != null)
        {
            btn.Click += async (s, e) =>
            {
                var newName = txt.Text?.Trim();
                if (string.IsNullOrWhiteSpace(newName)) return;

                // GATE_01 (F3) - SAVE AS NEW makes a COPY OF THE SELECTED PROFILE under a new
                // name. With no profile selected there is nothing to copy from, and
                // MaskOverlayManager.CreateNewProfile would silently snapshot whatever the live
                // crop config happens to hold - the last profile the MAIN APP applied, which the
                // user never chose here and probably cannot name. The button is disabled in that
                // state (SetProfileGate); this is the matching guard for a programmatic click.
                if (_activeProfile == null)
                {
                    SetStatus("Choose the profile you want to copy first.");
                    return;
                }

                var safeName = FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.SanitizeProfileName(newName);
                if (safeName == null)
                {
                    SetStatus("Invalid profile name. Avoid characters like \\ / : * ? \" < > |.");
                    return;
                }

                // NOMASK_01 — the reserved name cannot be claimed. MaskOverlayManager.CreateNewProfile
                // already refuses it, but it refuses SILENTLY: without this the handler would carry on
                // to SaveConfigAsync and set combo.SelectedItem to a profile that was never created.
                if (FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.IsNoMask(safeName))
                {
                    SetStatus("\"" + FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.NoMaskProfileName +
                              "\" is a reserved built-in profile. Choose another name.");
                    return;
                }

                try
                {
                    FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.CreateNewProfile(safeName);
                    if (_items.Count > 0)
                    {
                        await SaveConfigAsync();
                    }

                    if (combo != null)
                    {
                        var updatedProfiles = FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
                        combo.ItemsSource = updatedProfiles;
                        combo.SelectedItem = safeName;
                    }
                    txt.Text = "";
                    SetStatusSuccess("New overlay created: " + safeName);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("CROP", $"Create profile failed: {ex.Message}");
                    SetStatus("Could not create the profile. See runtime log.");
                }
            };
        }
    }

    /// <summary>
    /// GATE_01 (F3) - the one switch between "nothing chosen yet" and "editing a profile".
    ///
    /// WHY A GATE AND NOT JUST A DEFAULT. This window's save path is
    /// SaveConfigAsync -> CropConfigStore.SaveAsync (live crops_coordinations.conf)
    ///                 -> MaskOverlayManager.SyncActiveProfileFromCurrentConfig()
    /// and that last call copies the live config straight over
    /// SettingsManager.ActiveMaskOverlay's profile file. There is no undo on disk beyond the five
    /// rotating .bak files. So "which profile am I editing" is not a convenience - it decides
    /// which file gets overwritten, and it must never be answered by a leftover global.
    ///
    /// While locked: the overlay covers the whole working row, and every control that could start
    /// work or write anything is disabled. The Active Profile bar (outer grid row 1) is NOT
    /// covered - that is the entire reason it was lifted out of the right-hand panel.
    /// </summary>
    private void SetProfileGate(bool unlocked)
    {
        if (_profileGate != null)
        {
            _profileGate.IsVisible = !unlocked;
            // A hidden Border still answers hit-tests in some layouts; belt and braces, because a
            // click that lands on a "closed" gate and reaches the canvas behind it is exactly the
            // destructive edit this whole mechanism exists to prevent.
            _profileGate.IsHitTestVisible = !unlocked;
        }

        // The profile picker itself and the walkthrough button stay live in BOTH states.
        SetEnabled("OpenVideoButton", unlocked);
        SetEnabled("CreateMaskOverlayBtn", unlocked);
        SetEnabled("NewMaskOverlayTextBox", unlocked);
        SetEnabled("ResetMenuButton", unlocked);
        SetEnabled("ShowPlaceholders", unlocked);
        SetEnabled("SnapToggle", unlocked);

        if (!unlocked)
        {
            // Controls that are gated AND state-driven are forced off here, then handed back to
            // their normal owners (LoadVideoAsync, RefreshActionButtons, ...) once unlocked. They
            // are deliberately NOT enabled by this method on the way up: a profile being chosen
            // does not mean a video is loaded or that there is anything to save.
            SetEnabled("PlayPauseButton", false);
            SetEnabled("SnapshotButton", false);
            SetEnabled("SaveButton", false);
            SetEnabled("UndoButton", false);
            SetEnabled("RedoButton", false);
            SetEnabled("DeleteMenuButton", false);
            SetEnabled("RaiseButton", false);
            SetEnabled("LowerButton", false);
        }

        if (_profileStateLabel != null)
        {
            _profileStateLabel.Text = unlocked
                ? "Editing: " + (_activeProfile ?? "")
                : "No profile selected";
        }

        // IDEA_7 / drag-and-drop: OnVideoDrop is registered on the window, so it would happily
        // accept a dropped clip through a locked gate. DragDrop.AllowDrop is the switch that is
        // actually checked before the drop is routed.
        Avalonia.Input.DragDrop.SetAllowDrop(this, unlocked);
    }

    /// <summary>
    /// GATE_01 (F3) - loads a profile the user deliberately picked.
    ///
    /// ORDER MATTERS. ApplyProfile writes the chosen profile into the live crop config, so the
    /// working state has to be cleared FIRST (otherwise items belonging to the previous profile
    /// survive into this one and get written back to the wrong file on the next save), and the
    /// rehydrate has to run AFTER (it reads the live config that ApplyProfile just wrote).
    /// </summary>
    private async Task OnProfileChosenAsync(string profileName)
    {
        try
        {
            FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.ApplyProfile(profileName);
            _activeProfile = profileName;

            ResetWorkingState();
            SetProfileGate(unlocked: true);

            await RehydrateSavedLayersAsync();
            ApplySavedCropVisibility();

            RefreshActionButtons();
            RuntimeLog.Info("CROP", $"Editing mask profile '{profileName}' ({_items.Count} saved element(s) loaded).");

            if (string.IsNullOrWhiteSpace(_videoPath))
            {
                SetWizardState(1, "Upload Video", $"Editing \"{profileName}\". Open a reference clip to start.");
            }

            // GATE_01 - a clip handed over by the Main App waits here until there is a profile to
            // load it against, instead of being loaded into a session that cannot legally save.
            if (!string.IsNullOrWhiteSpace(_pendingInitialVideoPath) && File.Exists(_pendingInitialVideoPath))
            {
                string pending = _pendingInitialVideoPath;
                _pendingInitialVideoPath = null;
                await LoadVideoAsync(pending, startPaused: false);   // AUTOPLAY_01
            }

            SetStatusSuccess("Profile loaded: " + profileName);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Could not open profile '{profileName}': {ex.Message}");
            SetStatus("That profile could not be opened. See runtime log.");
        }
    }

    /// <summary>ZOOM_01 (F2) - the scale currently applied to the frozen-frame surface.</summary>
    private double CurrentZoom()
    {
        if (_snapshotZoomHost?.LayoutTransform is ScaleTransform st && st.ScaleX > 0)
        {
            return st.ScaleX;
        }
        return _snapshotZoomFactor;
    }

    /// <summary>
    /// ZOOM_01 (F2) - sets the frozen frame's zoom. Pass null for FIT.
    ///
    /// This scales the RENDER, never the coordinate space. SourceCanvas keeps its true capture
    /// size (1920x1080, 2560x1440, ...) and Avalonia maps pointer positions back through the
    /// LayoutTransform for us, so SourceCanvas_PointerPressed/Moved/Released, ClampToSnapshot and
    /// ToSourceRect all keep working in real source pixels with no scaling maths of their own.
    /// Resizing the canvas instead would have meant rewriting every one of those call sites AND
    /// would have quantised selections to screen pixels.
    ///
    /// The selection rectangle's stroke is divided by the scale so that a 2px outline stays 2px on
    /// screen: at FIT on a 4K capture the scale can be ~0.2, and an unadjusted stroke would render
    /// sub-pixel and vanish - the box would look like it had not been drawn at all.
    /// </summary>
    private void ApplySnapshotZoom(double? factor)
    {
        if (factor == null)
        {
            // FIT is the user saying "show me everything again", so it also RELEASES the manual
            // zoom lock: after pressing FIT, drawing a box auto-zooms to it once more.
            ApplySnapshotZoomInternal(ComputeFitScale(), fitMode: true, markUserZoom: false);
            _userZoomed = false;
            return;
        }

        ApplySnapshotZoomInternal(factor.Value, fitMode: false, markUserZoom: true);
    }

    /// <summary>
    /// ZOOM_01 - the single place the zoom scale is written.
    /// </summary>
    /// <param name="markUserZoom">
    /// AUTOZOOM_01 - true when a HUMAN asked for this magnification (a zoom button, the wheel).
    /// Once set, AutoZoomToSelection stops changing the zoom and only re-centres: overriding a
    /// magnification the user deliberately chose is the fastest way to make a tool feel like it
    /// is fighting back. AutoZoomToSelection's own call passes false.
    /// </param>
    private void ApplySnapshotZoomInternal(double factor, bool fitMode, bool markUserZoom)
    {
        if (_snapshotZoomHost == null) return;

        _snapshotFitMode = fitMode;
        if (markUserZoom) _userZoomed = true;

        // The floor is the FIT scale, not the MinZoom constant: zooming out past "the whole frame
        // is visible" only ever loses the user. The old Python tool clamped identically
        // (crop_widgets.py wheelEvent: min_allowed_zoom = the fit scale). Min(fit, 1.0) keeps a
        // capture SMALLER than the viewport from being locked above 100%.
        double scale = fitMode ? factor : Math.Clamp(factor, Math.Min(ComputeFitScale(), 1.0), MaxZoom);

        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
        _snapshotZoomFactor = scale;

        // LIST_06: this WRITES a size derived from a measurement, so it must never read back the
        // thing it sizes. ComputeFitScale measures the ScrollViewer (sized by the window), never
        // the transformed content, and the write is skipped when nothing actually changed -
        // an identical assignment still invalidates layout, which would keep SizeChanged firing
        // forever and make the scrollbars jump under the pointer.
        if (_snapshotZoomHost.LayoutTransform is ScaleTransform existing)
        {
            if (Math.Abs(existing.ScaleX - scale) > 0.0005 || Math.Abs(existing.ScaleY - scale) > 0.0005)
            {
                existing.ScaleX = scale;
                existing.ScaleY = scale;
            }
        }
        else
        {
            _snapshotZoomHost.LayoutTransform = new ScaleTransform(scale, scale);
        }

        // CROPCANVAS_01 — re-lay the selection at the new scale. The rectangle's stroke AND the
        // four corner handles are sized from SCREEN constants divided by the scale, so they all go
        // wrong together if this is skipped: at Fit on a 4K capture the handles would be a couple
        // of screen pixels across and impossible to grab.
        if (_sourceSelection is { } liveSelection)
        {
            UpdateSelectionRect(new Rect(liveSelection.X, liveSelection.Y, liveSelection.Width, liveSelection.Height));
        }
        else if (_selectionRect != null)
        {
            _selectionRect.StrokeThickness = 2.0 / scale;
        }
        foreach (Control control in _candidateControls)
        {
            if (control is Rectangle candidate) candidate.StrokeThickness = 3.0 / scale;
        }

        if (_zoomLabel != null)
        {
            _zoomLabel.Text = _snapshotFitMode
                ? $"Fit ({scale * 100:F0}%)"
                : $"{scale * 100:F0}%";
        }
    }

    /// <summary>
    /// ZOOM_01 (F2) - the scale that makes the whole capture fit the viewport, with a small margin
    /// so the frame's edges are visibly inside the panel rather than flush against it (a selection
    /// drawn hard against the edge is impossible to grab otherwise).
    /// Capped at 1.0: a 1080p capture in a 4K window should show at 100%, not be blown up.
    /// </summary>
    private double ComputeFitScale()
    {
        double viewportW = _snapshotScroll?.Bounds.Width ?? 0;
        double viewportH = _snapshotScroll?.Bounds.Height ?? 0;
        if (viewportW < 20 || viewportH < 20 || _snapshotWidth <= 0 || _snapshotHeight <= 0)
        {
            return _snapshotZoomFactor > 0 ? _snapshotZoomFactor : 1.0;
        }

        const double Margin = 16;
        double fit = Math.Min(
            (viewportW - Margin) / _snapshotWidth,
            (viewportH - Margin) / _snapshotHeight);

        return Math.Clamp(fit, MinZoom, 1.0);
    }


    /// <summary>ISSUE_04 — stops the walkthrough timer when this window goes away.</summary>
    protected override void OnClosed(EventArgs e)
    {
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        StopAnts();                 // ANTS_01 - see StartAnts for why this is not optional.
        _timelineTimer?.Stop();
        base.OnClosed(e);
    }

    private void ButtonClick(string name, EventHandler<RoutedEventArgs> handler)
    {
        var button = this.FindControl<Button>(name);
        if (button != null)
        {
            button.Click += handler;
        }
    }

    private void InitializeHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _undoStack.Push(CaptureSnapshot());
        RefreshUndoRedoButtons();
    }

    private async Task InitializeMpvAsync()
    {
        if (_isMpvStarted || _videoHost == null)
        {
            return;
        }

        _isMpvStarted = true;
        string mpvPath = ResolveBinaryPath("mpv.exe", "frontend");
        RuntimeLog.Info("CROP", $"Using MPV: {IOPath.GetFileName(mpvPath)}");
        RuntimeLog.Debug("CROP", $"Using MPV path: {mpvPath}");
        await _videoHost.StartMpvProcessAsync(mpvPath);
    }

    /// <summary>
    /// IDEA_7 — extensions accepted by drag-and-drop. Deliberately the SAME list as the file
    /// picker's FileTypeFilter in <see cref="OpenVideoAsync"/>; if one changes, change both.
    /// </summary>
    private static readonly string[] DroppableVideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v"];

    private void OnVideoDragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        bool hasFiles = e.Data.Contains(Avalonia.Input.DataFormats.Files)
                        || e.Data.Contains(Avalonia.Input.DataFormats.FileNames)
                        || e.Data.GetFiles()?.Any() == true;

        e.DragEffects = hasFiles ? Avalonia.Input.DragDropEffects.Copy : Avalonia.Input.DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnVideoDrop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        try
        {
            var files = e.Data.GetFiles();
            if (files == null) return;

            foreach (var file in files)
            {
                string path = file.Path.LocalPath;
                string ext = IOPath.GetExtension(path);
                if (!DroppableVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                RuntimeLog.Info("CROP", $"Video dropped onto Crop Tools: {IOPath.GetFileName(path)}");
                await LoadVideoAsync(path, startPaused: false);   // AUTOPLAY_01
                return;
            }

            SetStatus("Drop an MP4, MKV, AVI, MOV, WEBM or M4V file.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Dropped file could not be loaded: {ex.Message}");
            SetStatus("That file could not be opened.");
        }
    }

    private async Task OpenVideoAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open Reference Video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video Files")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.webm", "*.m4v"]
                }
            ]
        };

        var paths = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault();
        try
        {
            string? startPath = null;
            if (System.IO.File.Exists(paths.SessionStateFile))
            {
                var state = FortniteVideoSoftware.Core.Infrastructure.AtomicJsonFile.ReadObject(paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CropToolUploadDirectory", out var node) && node != null)
                {
                    startPath = node.ToString();
                }
            }

            if (string.IsNullOrEmpty(startPath) || !System.IO.Directory.Exists(startPath))
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string[] probes = new[]
                {
                    System.IO.Path.Combine(localAppData, "Temp", "Highlights", "Fortnite"),
                    System.IO.Path.Combine(localAppData, "Temp", "Highlights"),
                    System.IO.Path.Combine(localAppData, "NVIDIA Corporation", "GeForce Experience", "Highlights"),
                    System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                    System.IO.Path.Combine(myVideos, "Fortnite"),
                    System.IO.Path.Combine(myVideos, "Highlights"),
                    System.IO.Path.Combine(myDocuments, "Highlights")
                };

                startPath = myVideos;
                foreach (var probe in probes)
                {
                    if (System.IO.Directory.Exists(probe))
                    {
                        startPath = probe;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
            {
                try { Environment.CurrentDirectory = startPath; } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
            }
        }
        catch (Exception ex) { RuntimeLog.Info("CROP", $"Could not read suggested start location: {ex.Message}"); }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(options);

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                await new StateTransferStore(paths)
                    .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject
                    {
                        ["CropToolUploadDirectory"] = directory
                    });
            }
        }
        catch (Exception ex) { RuntimeLog.Info("CROP", $"Could not save upload directory preference: {ex.Message}"); }

        await LoadVideoAsync(files[0].Path.LocalPath, startPaused: false);   // AUTOPLAY_01
    }
    private async Task LoadVideoAsync(string path, bool startPaused)
    {
        if (!File.Exists(path))
        {
            SetStatus("Video file does not exist.");
            return;
        }

        await InitializeMpvAsync();

        _videoPath = path;
        _snapshotPath = null;
        _durationMs = 0;
        ClearSourceSelection();
        ClearMagicWandCandidates();
        ShowVideoPanel();

        SetWizardState(2, "Find HUD Frame", "Loading video metadata...");
        SetEnabled("PlayPauseButton", true);
        SetEnabled("SnapshotButton", false);
        SetVisible("TimelinePanel", true);
        SetVisible("UploadHint", false);

        if (_videoHost?.IpcClient != null)
        {
            await _videoHost.IpcClient.LoadFileAsync(path);

            // AUTOPLAY_01 - muted, and playing.
            //
            // MUTED: this screen never exports audio and the user is scanning for a frame with a
            // clear HUD, not watching. Gameplay audio detonating the moment a file is picked is
            // startling and has no upside here. Set BEFORE unpausing, or the first frames play out
            // loud while the property is still in flight.
            await _videoHost.IpcClient.SetPropertyAsync("mute", "yes");

            // PLAYING: the Main App starts a clip playing and so does this now. `startPaused` is
            // still honoured because RE-freezing a frame (BACK TO VIDEO -> pick another moment)
            // must not restart playback under the user.
            await _videoHost.IpcClient.SetPropertyAsync("pause", startPaused ? "yes" : "no");
            UpdatePlayPauseIcon(startPaused);
        }

        try
        {
            var prober = new MediaProber(ResolveBinaryPath("ffprobe.exe", "backend"), path);
            _durationMs = Math.Max(0, await prober.GetDurationAsync() * 1000.0);
            _originalResolution = await prober.GetResolutionStringAsync();
            var (w, h) = CoordinateMath.GetResolutionInts(_originalResolution);
            _snapshotWidth = w;
            _snapshotHeight = h;

            double aspectRatio = h > 0 ? (double)w / h : 1.777;
            if (Math.Abs(aspectRatio - (16.0 / 9.0)) > 0.05)
            {
                NativeDialog.ShowError($"The selected video has a resolution of {w}x{h} (Aspect Ratio: {aspectRatio:F2}).\n\nThe crop tool requires a standard 16:9 resolution (e.g., 1920x1080, 2560x1440, 3840x2160) to generate accurate HUD overlay masks. Non-16:9 videos will result in misaligned overlays on standard videos.\n\nPlease upload a 16:9 video for crop configuration.", "Unsupported Aspect Ratio");
                SetWizardState(2, "Find HUD Frame", $"Error: Non 16:9 video ({w}x{h}) rejected.");
                return;
            }

            if (_timelineSlider != null)
            {
                _timelineSlider.Minimum = 0;
                _timelineSlider.Maximum = 100;
                _timelineSlider.Value = 0;
            }

            RuntimeLog.Info("CROP", $"Video loaded: {System.IO.Path.GetFileName(path)} | Resolution: {_originalResolution} | Duration: {_durationMs:F0}ms");
            RuntimeLog.Debug("CROP", $"Full path: {path}");
            SetEnabled("SnapshotButton", true);
            SetWizardState(2, "Find HUD Frame", $"Frame ready ({_originalResolution}).");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Video probe failed: {ex.Message}");
            SetEnabled("SnapshotButton", true);
            SetWizardState(2, "Find HUD Frame", "Metadata fallback is 1920x1080.");
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        if (_videoHost?.IpcClient == null)
        {
            return;
        }

        // BACKTOVIDEO_01 - while a frozen frame is on screen, PLAY means "back to the video".
        // Checked before the pause state, because what the user is looking at decides what the
        // button means: pressing play at a still frame cannot sensibly mean anything else.
        bool frozen = this.FindControl<Grid>("SnapshotPanel")?.IsVisible == true;
        if (frozen)
        {
            ShowVideoPanel();
            await _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            UpdatePlayPauseIcon(false);
            return;
        }

        bool currentlyPaused = _videoHost.IpcClient.IsPaused;
        await _videoHost.IpcClient.SetPropertyAsync("pause", currentlyPaused ? "no" : "yes");
        UpdatePlayPauseIcon(!currentlyPaused);
    }

    /// <summary>
    /// WIZCOLLAPSE_01 - folds the step tracker away once cropping starts.
    ///
    /// The four dots answer "where am I in this flow", which is a question you stop asking the
    /// moment you are dragging boxes on a frozen frame. Keeping them costs ~34px of height on the
    /// one screen where height is what lets you see the HUD you are cropping. GoalLabel and
    /// StatusLabel stay in both states - those say what to do NEXT, which never stops mattering.
    /// </summary>
    private void ApplyWizardChrome(bool cropping)
    {
        SetVisible("StepStrip", !cropping);
    }

    /// <summary>
    /// PLAYICON_01 - the ONE writer of the transport button's face, copied from the Main App
    /// (MainWindow.axaml.cs, the playIcon/pauseIcon pair).
    ///
    /// Two Paths with one visible at a time, rather than swapping the button's Content between
    /// the strings "PLAY" and "PAUSE". Swapping Content re-measures the button on every toggle,
    /// so it visibly changed width mid-playback, and a 110px button with a word in it was the
    /// only transport control in the suite that did not look like a transport control.
    /// </summary>
    private void UpdatePlayPauseIcon(bool isPaused)
    {
        var play = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pause = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (play == null || pause == null) return;
        play.IsVisible = isPaused;
        pause.IsVisible = !isPaused;
    }

    private async Task TakeSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            SetStatus("Open a video first.");
            return;
        }

        SetEnabled("SnapshotButton", false);
        SetContent("SnapshotButton", "CAPTURING...");
        SetWizardState(3, "Refine Box", "Capturing snapshot...");

        try
        {
            CleanupTempFiles();
            _tempFiles.Clear();

            _paths.EnsureWritableDirectories();
            string output = IOPath.Combine(_paths.TempDirectory, $"crop_snapshot_{Guid.NewGuid():N}.png");
            string tempOutput = output + ".tmp.png";
            double seconds = GetCurrentTimeMs() / 1000.0;

            await CaptureCurrentPreviewFrameAsync(tempOutput);

            if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length < 100)
            {
                throw new IOException("Snapshot file was not created.");
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }

            File.Move(tempOutput, output);
            _tempFiles.Add(output);
            RuntimeLog.Info("CROP", $"Snapshot captured: {IOPath.GetFileName(output)} at {seconds:F3}s from {IOPath.GetFileName(_videoPath)}");
            RuntimeLog.Debug("CROP", $"Snapshot path: {output}; source video path: {_videoPath}");
            await LoadSnapshotAsync(output);
            ShowSnapshotPanel();
            _ = RefreshRehydratedThumbnailsAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Snapshot failed: {ex.Message}");
            SetWizardState(2, "Find HUD Frame", "Snapshot failed. Try another frame.");
        }
        finally
        {
            SetEnabled("SnapshotButton", true);
            SetContent("SnapshotButton", "START CROPPING");
        }
    }

    private async Task LoadSnapshotAsync(string path)
    {
        _snapshotPath = path;

        bool wantComposerBg = _composerBackgroundImage != null;

        var (snapshotBitmap, composerBitmap, composerPreviewPath) = await Task.Run(() =>
        {
            using (SKBitmap bitmap = SKBitmap.Decode(path) ?? throw new IOException("Could not decode snapshot."))
            {
                _snapshotWidth = bitmap.Width;
                _snapshotHeight = bitmap.Height;
                _originalResolution = $"{_snapshotWidth}x{_snapshotHeight}";
            }

            Bitmap snap;
            using (var snapStream = File.OpenRead(path))
                snap = new Bitmap(snapStream);

            Bitmap? bg = null;
            string? previewPath = null;
            if (wantComposerBg)
            {
                previewPath = CreateComposerBackgroundPreview(path);
                using var bgStream = File.OpenRead(previewPath);
                bg = new Bitmap(bgStream);
            }

            return (snap, bg, previewPath);
        });

        if (composerPreviewPath != null)
        {
            _tempFiles.Add(composerPreviewPath);
        }

        if (_snapshotImage != null)
        {
            if (_snapshotImage.Source is IDisposable oldBitmap)
            {
                oldBitmap.Dispose();
            }
            _snapshotImage.Source = snapshotBitmap;
            _snapshotImage.Width = _snapshotWidth;
            _snapshotImage.Height = _snapshotHeight;
        }

        if (_sourceCanvas != null)
        {
            _sourceCanvas.Width = _snapshotWidth;
            _sourceCanvas.Height = _snapshotHeight;
        }

        if (composerBitmap != null && _composerBackgroundImage != null)
        {
            _composerBackgroundImage.Source = composerBitmap;
        }

        ClearSourceSelection();
        ClearMagicWandCandidates();

        // ZOOM_01 (F2) - fit the whole capture into the panel. Without this the frame renders at
        // 1:1 (SnapshotImage/SourceCanvas are given the real capture size just above), which on a
        // 1080p capture in a half-width panel means the user sees roughly a quarter of their game
        // screen and has to hunt for the HUD with scrollbars. Fit is the only sane default here;
        // 100% is one click away for pixel-exact work.
        ApplySnapshotZoom(null);

        SetWizardState(3, "Refine Box", $"Draw a HUD box on the {_originalResolution} snapshot.");
    }

    private async Task CaptureCurrentPreviewFrameAsync(string outputPath)
    {
        if (_videoHost?.IpcClient == null)
        {
            throw new InvalidOperationException("Video preview is not ready.");
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        await _videoHost.IpcClient.SendCommandAsync("screenshot-to-file", outputPath, "video");
        await WaitForFileAsync(outputPath, TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try
                {
                    if (new FileInfo(path).Length > 100)
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(50, CancellationToken.None);
        }

        throw new TimeoutException("Timed out waiting for the current preview frame snapshot.");
    }

    private string CreateComposerBackgroundPreview(string snapshotPath)
    {
        _paths.EnsureWritableDirectories();
        string output = IOPath.Combine(_paths.TempDirectory, $"crop_canvas_trick_{Guid.NewGuid():N}.png");

        using SKBitmap source = SKBitmap.Decode(snapshotPath) ?? throw new IOException("Could not decode snapshot.");
        using var internalBitmap = new SKBitmap(
            CoordinateConstants.InternalW,
            CoordinateConstants.InternalH,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var internalCanvas = new SKCanvas(internalBitmap))
        {
            internalCanvas.Clear(SKColors.Black);
            var (scaledW, scaledH, cropX, cropY, _) = CoordinateMath.ScalePlan($"{source.Width}x{source.Height}");
            var dst = new SKRect(-cropX, -cropY, -cropX + scaledW, -cropY + scaledH);
            internalCanvas.DrawBitmap(source, dst);
            internalCanvas.Flush();
        }

        using var finalBitmap = new SKBitmap(
            CoordinateConstants.PortraitW,
            CoordinateConstants.PortraitH,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var finalCanvas = new SKCanvas(finalBitmap))
        {
            finalCanvas.Clear(SKColors.Black);
            var contentDst = new SKRect(
                0,
                CoordinateConstants.UIPaddingTop,
                CoordinateConstants.PortraitW,
                CoordinateConstants.PortraitH - CoordinateConstants.UIPaddingBottom);
            finalCanvas.DrawBitmap(internalBitmap, contentDst);

            // COMPOSERDIM_01 - the red 64-alpha wash over the two letterbox bands is GONE.
            // It was marking "this area is padding", but the AXAML already paints those bands with
            // AppVideoSurfaceBrush over the top, so the wash was invisible where it was meant to be
            // read and only served to muddy the colour of everything underneath. Combined with the
            // old 0.22 background opacity it made the reference frame unreadable, which is the
            // complaint this addresses. The bands are still obvious - they are the empty strips.
            finalCanvas.Flush();
        }

        using SKImage image = SKImage.FromBitmap(finalBitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            data.SaveTo(fs);
        }
        return output;
    }

    /// <summary>
    /// BACKTOVIDEO_01 - back to the moving video.
    ///
    /// The BACK TO VIDEO button that used to call this is deleted. PLAY calls it now: pressing play
    /// while a frame is frozen means "show me the video again", which is the only thing play can
    /// sensibly mean at that moment. Two controls for one idea, sitting next to each other, was the
    /// reason the button read as pointless.
    /// </summary>
    private void ShowVideoPanel()
    {
        SetVisible("VideoPanel", true);
        SetVisible("SnapshotPanel", false);
        SetVisible("MagicWandButton", false);

        // BACKTOVIDEO_01 - START CROPPING belongs to the video, so it comes back with it. The pair
        // reads as one toggle: while the video moves you can freeze it, while it is frozen you can
        // only go back.
        SetVisible("SnapshotButton", true);
        SetVisible("ZoomStrip", false);                          // ZOOMBAR_01
        ApplyWizardChrome(cropping: false);                      // WIZCOLLAPSE_01

        if (!string.IsNullOrWhiteSpace(_videoPath))
        {
            SetWizardState(2, "Find HUD Frame", "Find a clear HUD frame and start cropping.");
        }
    }

    private void ShowSnapshotPanel()
    {
        SetVisible("VideoPanel", false);
        SetVisible("SnapshotPanel", true);
        // MAGICWAND_01 (F5): deliberately NOT SetVisible("MagicWandButton", true). See the wiring
        // in WireEvents for why - ShowMagicWandCandidates does no detection at all, it draws six
        // boxes at hardcoded fractions of the frame. Restore this line only together with a real
        // frame analyser.
        // BACKTOVIDEO_01 - hidden while a frame is frozen. There is nothing to freeze: you are
        // already looking at a still. PLAY is the way out and START CROPPING returns with the video.
        SetVisible("SnapshotButton", false);
        SetVisible("ZoomStrip", true);                            // ZOOMBAR_01
        ApplyWizardChrome(cropping: true);                        // WIZCOLLAPSE_01
        SetWizardState(3, "Refine Box", "Drag a box round one HUD piece, then pick what it is.");
    }

    // ==================================================================================
    // CROPCANVAS_01 — the frozen-frame selection surface.
    //
    // WHAT WAS HERE BEFORE, AND WHY IT HAD TO GO.
    // SourceCanvas_PointerPressed treated EVERY press as the start of a brand-new rubber
    // band. There was no hit-test, no handle, no move. A box drawn two pixels off could
    // only be fixed by drawing the whole thing again, on an image rendered at ~35% scale,
    // with scrollbars as the only way to move around. That is the whole of "the rubber
    // band is really hard to work with".
    //
    // The model below is the one the old Python tool used (developer_tools/crop_widgets.py,
    // DrawWidget.mousePressEvent), because it was right: a press is dispatched to one of
    // four outcomes, checked in this order, and only the last one draws anything new.
    //
    //   1. a corner handle      -> resize from that corner
    //   2. inside the selection -> move the whole box
    //   3. on a wand candidate  -> adopt that box
    //   4. anywhere else        -> start a new box
    //
    // Every coordinate in this region is a TRUE SOURCE PIXEL of the capture (1920x1080,
    // 2560x1440, ...). SourceCanvas is never resized — ZOOM_01's LayoutTransformControl
    // scales the rendering and Avalonia inverts that transform for us — so
    // e.GetPosition(_sourceCanvas) needs no scaling maths anywhere below. The ONLY places
    // the zoom scale appears are the ones that must stay a constant size ON SCREEN:
    // handle size, stroke width, and the grab tolerance.
    // ==================================================================================

    /// <summary>CROPCANVAS_01 — what a drag on the frozen frame is currently doing.</summary>
    private enum SourceDrag
    {
        None,
        Drawing,
        Moving,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight,
        Panning,
    }

    private SourceDrag _sourceDrag = SourceDrag.None;
    private SourceRect _sourceDragOrigin;
    private Point _sourceDragAnchor;
    private Point _panAnchorViewport;
    private readonly List<Rectangle> _selectionHandles = new();

    /// <summary>
    /// CROPCANVAS_01 — handle box and grab tolerance, in SCREEN pixels. Divided by the zoom
    /// scale wherever they are used, so a handle is the same physical size to grab whether the
    /// frame is at Fit (~0.3x on a 4K capture) or at 400%. A constant in source pixels would be
    /// invisible at Fit and enormous when zoomed in.
    /// </summary>
    private const double HandleScreenPx = 14;
    private const double GrabScreenPx = 11;

    private void SourceCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_snapshotPath == null || _sourceCanvas == null)
        {
            return;
        }

        var props = e.GetCurrentPoint(_sourceCanvas).Properties;

        // CANCELSEL_01 - right-click abandons the selection and puts the view back.
        // Checked FIRST, before the drag dispatch below, so it works whether the box is finished or
        // still being dragged out.
        if (props.IsRightButtonPressed)
        {
            CancelSourceSelection();
            e.Handled = true;
            return;
        }

        // PAN_01 — middle-drag pans, the same gesture the Python tool used and the same one
        // every image editor uses. Scrollbars alone are how the previous build earned
        // "scrolling sideways/up/down is extremely confusing": they are the one navigation
        // control you cannot reach without letting go of what you are doing.
        if (props.IsMiddleButtonPressed)
        {
            _sourceDrag = SourceDrag.Panning;
            _panAnchorViewport = e.GetPosition(_snapshotScroll);
            SetSourceCursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(_sourceCanvas);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));
        _sourceCanvas.Focus();

        // 1 + 2: act on the existing selection before considering a new one.
        if (_sourceSelection is { } current)
        {
            SourceDrag corner = HitTestSelectionCorner(p, current);
            if (corner != SourceDrag.None)
            {
                _sourceDrag = corner;
                _sourceDragOrigin = current;
                e.Pointer.Capture(_sourceCanvas);
                e.Handled = true;
                return;
            }

            if (p.X >= current.X && p.X <= current.X + current.Width &&
                p.Y >= current.Y && p.Y <= current.Y + current.Height)
            {
                _sourceDrag = SourceDrag.Moving;
                _sourceDragOrigin = current;
                _sourceDragAnchor = p;
                SetSourceCursor(StandardCursorType.SizeAll);
                e.Pointer.Capture(_sourceCanvas);
                e.Handled = true;
                return;
            }
        }

        // 3: a Magic Wand candidate. The wand is hidden (MAGICWAND_01) but the branch is kept
        // so the interaction is already correct when a real detector lands.
        if (e.Source is Control control && control.Tag is CandidateSpec candidate)
        {
            SetSourceSelection(candidate.Rect, candidate.RoleKey);
            AutoZoomToSelection();
            e.Handled = true;
            return;
        }

        // 4: nothing else applied — draw a new box.
        _sourceDrag = SourceDrag.Drawing;
        _sourceSelectionStart = p;
        _isDrawingSourceSelection = true;
        EnsureSelectionVisuals();
        SetHandlesVisible(false);
        UpdateSelectionRect(new Rect(p, new Size(1, 1)));
        e.Pointer.Capture(_sourceCanvas);
        e.Handled = true;
    }

    private void SourceCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sourceCanvas == null) return;

        if (_sourceDrag == SourceDrag.Panning)
        {
            Point now = e.GetPosition(_snapshotScroll);
            NudgeScrollBy(_panAnchorViewport.X - now.X, _panAnchorViewport.Y - now.Y);
            // The anchor is viewport-relative, so it stays valid after the offset moves.
            _panAnchorViewport = now;
            e.Handled = true;
            return;
        }

        Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));

        switch (_sourceDrag)
        {
            case SourceDrag.None:
                UpdateHoverCursor(p);
                return;

            case SourceDrag.Drawing:
                UpdateSelectionRect(NormalizeRect(_sourceSelectionStart, p));
                e.Handled = true;
                return;

            case SourceDrag.Moving:
            {
                // Translate by the pointer delta and clamp the WHOLE rect, so dragging into an
                // edge slides along it instead of shrinking the box.
                double nx = _sourceDragOrigin.X + (p.X - _sourceDragAnchor.X);
                double ny = _sourceDragOrigin.Y + (p.Y - _sourceDragAnchor.Y);
                nx = Math.Clamp(nx, 0, Math.Max(0, _snapshotWidth - _sourceDragOrigin.Width));
                ny = Math.Clamp(ny, 0, Math.Max(0, _snapshotHeight - _sourceDragOrigin.Height));
                SetSourceSelection(
                    new SourceRect((int)Math.Round(nx), (int)Math.Round(ny),
                                   _sourceDragOrigin.Width, _sourceDragOrigin.Height),
                    keepRoleName: true);
                e.Handled = true;
                return;
            }

            default:
            {
                Rect resized = ResizeFromCorner(_sourceDragOrigin, _sourceDrag, p);
                if (resized.Width >= MinSelectionSize && resized.Height >= MinSelectionSize)
                {
                    SetSourceSelection(ToSourceRect(resized), keepRoleName: true);
                }
                e.Handled = true;
                return;
            }
        }
    }

    private void SourceCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_sourceCanvas == null) return;

        SourceDrag finished = _sourceDrag;
        _sourceDrag = SourceDrag.None;
        _isDrawingSourceSelection = false;
        e.Pointer.Capture(null);

        if (finished == SourceDrag.None) return;

        if (finished == SourceDrag.Panning)
        {
            SetSourceCursor(StandardCursorType.Cross);
            e.Handled = true;
            return;
        }

        if (finished == SourceDrag.Drawing)
        {
            Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));
            SourceRect rect = ToSourceRect(NormalizeRect(_sourceSelectionStart, p));
            if (rect.Width < MinSelectionSize || rect.Height < MinSelectionSize)
            {
                ClearSourceSelection();
                e.Handled = true;
                return;
            }

            SetSourceSelection(rect, SuggestRole(rect).Key);

            // AUTOZOOM_01 — the move that makes this tool usable. See the method.
            AutoZoomToSelection();
            ShowRolePopup();                                   // ROLEPOPUP_01
        }
        else if (finished == SourceDrag.Moving)
        {
            SetSourceCursor(StandardCursorType.Cross);
            ShowRolePopup();                                   // ROLEPOPUP_01
        }
        else
        {
            // A resize also re-zooms: after dragging a corner the box is a different size, so
            // the "fills ~70% of the viewport" relationship has to be re-established or the
            // next adjustment is made at the wrong magnification.
            AutoZoomToSelection();
            ShowRolePopup();                                   // ROLEPOPUP_01
        }

        e.Handled = true;
    }

    /// <summary>
    /// CROPCANVAS_01 — which corner handle (if any) is under <paramref name="p"/>.
    /// The tolerance is in screen pixels converted to source pixels, so the grab area is the
    /// same physical size at every zoom level.
    /// </summary>
    private SourceDrag HitTestSelectionCorner(Point p, SourceRect r)
    {
        double tol = GrabScreenPx / Math.Max(0.01, CurrentZoom());
        bool left = Math.Abs(p.X - r.X) <= tol;
        bool right = Math.Abs(p.X - (r.X + r.Width)) <= tol;
        bool top = Math.Abs(p.Y - r.Y) <= tol;
        bool bottom = Math.Abs(p.Y - (r.Y + r.Height)) <= tol;

        if (top && left) return SourceDrag.ResizeTopLeft;
        if (top && right) return SourceDrag.ResizeTopRight;
        if (bottom && left) return SourceDrag.ResizeBottomLeft;
        if (bottom && right) return SourceDrag.ResizeBottomRight;
        return SourceDrag.None;
    }

    /// <summary>
    /// CANCELSEL_01 - abandon the current selection and undo what drawing it did to the view.
    ///
    /// Cancel has to mean "pretend that never happened", and AUTOZOOM_01 makes that more than
    /// clearing a rectangle: drawing a box MOVES THE VIEW, magnifying and re-centring on it. Without
    /// restoring the zoom and the scroll, escaping from a mis-drawn box left the user stranded at
    /// 300% somewhere they never asked to be, with no idea how they got there - which is worse than
    /// the mistake they were trying to undo.
    ///
    /// The pre-zoom state is captured in AutoZoomToSelection, immediately before it changes
    /// anything, and only when there is not already one saved - so a draw, a resize and another
    /// resize all rewind to where the FIRST one started, not to the middle of the sequence.
    /// </summary>
    private void CancelSourceSelection()
    {
        ClearSourceSelection();

        if (_preZoomState is { } saved)
        {
            ApplySnapshotZoomInternal(saved.Scale, fitMode: saved.FitMode, markUserZoom: false);
            _userZoomed = saved.UserZoomed;
            _snapshotZoomHost?.UpdateLayout();
            _snapshotScroll?.UpdateLayout();
            if (_snapshotScroll != null) _snapshotScroll.Offset = saved.Offset;
            _preZoomState = null;
        }

        SetStatus("Selection cancelled.");
    }

    /// <summary>CANCELSEL_01 - the view as it was before auto-zoom touched it.</summary>
    private readonly record struct PreZoomState(double Scale, bool FitMode, bool UserZoomed, Vector Offset);

    private PreZoomState? _preZoomState;

    /// <summary>CROPCANVAS_01 — the rect produced by dragging one corner to <paramref name="p"/>.</summary>
    private static Rect ResizeFromCorner(SourceRect origin, SourceDrag corner, Point p)
    {
        double l = origin.X, t = origin.Y, r = origin.X + origin.Width, b = origin.Y + origin.Height;
        switch (corner)
        {
            case SourceDrag.ResizeTopLeft: l = p.X; t = p.Y; break;
            case SourceDrag.ResizeTopRight: r = p.X; t = p.Y; break;
            case SourceDrag.ResizeBottomLeft: l = p.X; b = p.Y; break;
            case SourceDrag.ResizeBottomRight: r = p.X; b = p.Y; break;
        }
        // Normalised, so dragging a corner past its opposite flips the box instead of
        // producing a negative-size rect that would silently fail every downstream clamp.
        return new Rect(new Point(Math.Min(l, r), Math.Min(t, b)), new Point(Math.Max(l, r), Math.Max(t, b)));
    }

    /// <summary>
    /// CROPCANVAS_01 — the cursor is the only thing telling the user the box can be grabbed at
    /// all. Without this the selection looks like a drawing, not an object.
    /// </summary>
    private void UpdateHoverCursor(Point p)
    {
        if (_sourceCanvas == null) return;

        StandardCursorType wanted = StandardCursorType.Cross;
        if (_sourceSelection is { } r)
        {
            SourceDrag corner = HitTestSelectionCorner(p, r);
            wanted = corner switch
            {
                SourceDrag.ResizeTopLeft or SourceDrag.ResizeBottomRight => StandardCursorType.TopLeftCorner,
                SourceDrag.ResizeTopRight or SourceDrag.ResizeBottomLeft => StandardCursorType.TopRightCorner,
                _ => p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height
                        ? StandardCursorType.Hand
                        : StandardCursorType.Cross,
            };
        }

        SetSourceCursor(wanted);
    }

    /// <summary>
    /// CROPCANVAS_01 — the ONE writer of the frozen frame's cursor.
    ///
    /// Every cursor change goes through here for two reasons. Assigning a Cursor allocates and
    /// invalidates, and PointerMoved fires continuously, so the no-op guard matters. And the
    /// cursors forced during a drag (SizeAll while panning or moving) have to leave _hoverCursor
    /// telling the truth, or the first hover after the drag sees "no change" and leaves the wrong
    /// cursor on screen.
    /// </summary>
    private void SetSourceCursor(StandardCursorType wanted)
    {
        if (_sourceCanvas == null || _hoverCursor == wanted) return;
        _hoverCursor = wanted;
        _sourceCanvas.Cursor = new Cursor(wanted);
    }

    private StandardCursorType _hoverCursor = StandardCursorType.Cross;

    /// <summary>
    /// WHEELZOOM_01 — wheel over the frozen frame zooms, anchored under the pointer.
    ///
    /// "Anchored" means the source pixel under the cursor stays under the cursor. Without it,
    /// zooming in always drifts toward a corner and the user has to chase what they were looking
    /// at with the scrollbars — which is the behaviour this whole pass is removing.
    ///
    /// The correction is MEASURED, not calculated: the zoom host is centred in the ScrollViewer,
    /// so while the content is smaller than the viewport there is padding that no offset
    /// arithmetic accounts for. Asking Avalonia where the point actually landed and correcting by
    /// the difference is right in both cases.
    /// </summary>
    private void OnSnapshotWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_snapshotPath == null || _snapshotScroll == null || _sourceCanvas == null) return;
        if (Math.Abs(e.Delta.Y) < 0.01) return;

        // Captured BEFORE the scale changes: this is the source pixel the user is pointing at,
        // and where on screen they are pointing at it.
        Point anchorSource = e.GetPosition(_sourceCanvas);
        Point anchorViewport = e.GetPosition(_snapshotScroll);

        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        ApplySnapshotZoomInternal(CurrentZoom() * factor, fitMode: false, markUserZoom: true);

        _snapshotZoomHost?.UpdateLayout();
        _snapshotScroll.UpdateLayout();

        Point? landed = _sourceCanvas.TranslatePoint(anchorSource, _snapshotScroll);
        if (landed != null)
        {
            NudgeScrollBy(landed.Value.X - anchorViewport.X, landed.Value.Y - anchorViewport.Y);
        }

        e.Handled = true;
    }

    /// <summary>
    /// CROPCANVAS_01 — arrow-key nudge for the SOURCE selection.
    ///
    /// The window already nudged _selectedItem, which is a PORTRAIT item, so on the frozen frame
    /// the arrow keys did nothing at all. Getting the last two or three pixels of a HUD box right
    /// with a mouse is unreasonable; this is how that is meant to be done.
    /// Plain arrows move the box, Ctrl+arrows resize its bottom-right corner, Shift multiplies
    /// the step - the same shape as the portrait item's nudge, so there is one thing to learn.
    /// </summary>
    private bool NudgeSourceSelection(Key key, KeyModifiers modifiers)
    {
        if (_sourceSelection is not { } r) return false;

        double step = modifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        bool resize = modifiers.HasFlag(KeyModifiers.Control);

        double dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        double dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return false;

        SourceRect next;
        if (resize)
        {
            next = new SourceRect(
                r.X, r.Y,
                (int)Math.Max(MinSelectionSize, r.Width + dx),
                (int)Math.Max(MinSelectionSize, r.Height + dy));
        }
        else
        {
            next = new SourceRect(
                (int)Math.Clamp(r.X + dx, 0, Math.Max(0, _snapshotWidth - r.Width)),
                (int)Math.Clamp(r.Y + dy, 0, Math.Max(0, _snapshotHeight - r.Height)),
                r.Width, r.Height);
        }

        SetSourceSelection(ClampSourceRect(next), keepRoleName: true);
        return true;
    }

    /// <summary>
    /// AUTOZOOM_01 — after a box is drawn or resized, zoom so it fills ~70% of the viewport
    /// width and scroll it to the centre.
    ///
    /// This is the single interaction that makes the tool workable, and it is why the old
    /// Python tool felt precise (crop_widgets.py:_auto_zoom_to_selection). The user drags a
    /// ROUGH box at Fit, where the whole 1920- or 3840-wide frame is on screen at ~30%, and the
    /// app immediately magnifies their box so the edges can be nudged accurately. Without it,
    /// the user is being asked to place a pixel-accurate rectangle on a heavily shrunk image,
    /// which is the complaint this whole pass exists to answer.
    ///
    /// Suppressed once the user has driven the wheel themselves (_userZoomed): at that point
    /// the magnification is a deliberate choice and yanking it away is hostile. The view is
    /// still re-centred on the selection, which is always welcome.
    /// </summary>
    private void AutoZoomToSelection()
    {
        if (_sourceSelection is not { } sel || _snapshotScroll == null) return;

        double viewportW = _snapshotScroll.Viewport.Width;
        if (viewportW < 40) return;

        // CANCELSEL_01 - remember where the view was, once per selection, before touching it.
        _preZoomState ??= new PreZoomState(CurrentZoom(), _snapshotFitMode, _userZoomed, _snapshotScroll.Offset);

        if (!_userZoomed)
        {
            double target = viewportW * 0.7 / Math.Max(1, sel.Width);
            // Clamped at the bottom by the FIT scale rather than by 1.0: on a 4K capture in a
            // small panel, 1.0 would be a zoom IN disguised as a floor.
            target = Math.Clamp(target, ComputeFitScale(), MaxZoom);
            if (Math.Abs(target - CurrentZoom()) > 0.02)
            {
                ApplySnapshotZoomInternal(target, fitMode: false, markUserZoom: false);
            }
        }

        CenterOnSourcePoint(new Point(sel.X + sel.Width / 2.0, sel.Y + sel.Height / 2.0));
    }

    /// <summary>
    /// ZOOM_01 — scrolls so a given SOURCE pixel lands at the centre of the viewport.
    ///
    /// Deliberately measured rather than calculated. The zoom host is centred inside the
    /// ScrollViewer, so when the content is smaller than the viewport there is padding that no
    /// offset arithmetic accounts for. Asking Avalonia where the point actually IS
    /// (TranslatePoint) and correcting by the difference is correct in both cases and survives
    /// any future change to the alignment.
    /// </summary>
    private void CenterOnSourcePoint(Point sourcePoint)
    {
        if (_snapshotScroll == null || _sourceCanvas == null) return;

        // The scale change above has not been laid out yet; without this the measurement is of
        // the OLD geometry and the view lands in the wrong place.
        _snapshotZoomHost?.UpdateLayout();
        _snapshotScroll.UpdateLayout();

        Point? actual = _sourceCanvas.TranslatePoint(sourcePoint, _snapshotScroll);
        if (actual == null) return;

        NudgeScrollBy(
            actual.Value.X - _snapshotScroll.Viewport.Width / 2.0,
            actual.Value.Y - _snapshotScroll.Viewport.Height / 2.0);
    }

    /// <summary>ZOOM_01 — moves the scroll offset by a delta, clamped to the real extent.</summary>
    private void NudgeScrollBy(double dx, double dy)
    {
        if (_snapshotScroll == null) return;

        double maxX = Math.Max(0, _snapshotScroll.Extent.Width - _snapshotScroll.Viewport.Width);
        double maxY = Math.Max(0, _snapshotScroll.Extent.Height - _snapshotScroll.Viewport.Height);
        _snapshotScroll.Offset = new Vector(
            Math.Clamp(_snapshotScroll.Offset.X + dx, 0, maxX),
            Math.Clamp(_snapshotScroll.Offset.Y + dy, 0, maxY));
    }

    /// <summary>
    /// CROPCANVAS_01 — builds the selection rectangle and its four corner handles, once.
    /// </summary>
    private void EnsureSelectionVisuals()
    {
        if (_sourceCanvas == null || _selectionRect != null)
        {
            return;
        }

        // TONE_01 / UI-THEME: these were raw hex (#2ecc71 and a 45-alpha fill), which the Zero
        // Raw Hex mandate forbids and which ignored the theme. GhostFillColor already resolves
        // AppSuccessColor for the saved-crop ghosts; the live selection shares it, so the box
        // being drawn and the boxes already saved are visibly the same family of green.
        _selectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Infrastructure.ThemeResources.Colour(this, "AppSuccessColor", Color.FromRgb(63, 156, 107))),
            StrokeThickness = 2.0 / Math.Max(0.01, CurrentZoom()),
            Fill = new SolidColorBrush(GhostFillColor(45)),
            // ANTS_01 - a dashed outline, animated by ScrollAnts below. A static thin rectangle on
            // a busy game frame reads as part of the HUD; a crawling dash reads as a selection and
            // nothing else. Copied from the Python tool, which ran the same 100ms 8px cycle
            // (crop_widgets.py _update_ant_dash).
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
            IsHitTestVisible = false,
            ZIndex = 500
        };
        _sourceCanvas.Children.Add(_selectionRect);
        StartAnts();

        // ANTS_01 - TWO handles, top-left and bottom-right, matching the phone-preview items so
        // there is one grab language across both canvases. The other two corners are still
        // resizable: HitTestSelectionCorner tests all four arithmetically, so tr/bl work by feel
        // even though nothing is drawn there.
        for (int i = 0; i < 2; i++)
        {
            // IsHitTestVisible = false deliberately: the corners are hit-tested arithmetically in
            // HitTestSelectionCorner against a SCREEN-pixel tolerance. Letting these little
            // rectangles take the press instead would make the grab area shrink as you zoom out,
            // which is precisely when a handle is hardest to hit.
            var handle = new Rectangle
            {
                Fill = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red),
                Stroke = Brushes.White,
                StrokeThickness = 1.5 / Math.Max(0.01, CurrentZoom()),
                IsHitTestVisible = false,
                ZIndex = 520
            };
            _selectionHandles.Add(handle);
            _sourceCanvas.Children.Add(handle);
        }
    }

    /// <summary>
    /// ANTS_01 - crawls the selection's dash pattern.
    ///
    /// One shared timer for the single selection rectangle, started when the rectangle is created
    /// and stopped when it is cleared. It must be stopped: a DispatcherTimer holds a strong
    /// reference to its handler, so leaving it running keeps this window alive after it closes and
    /// keeps waking the dispatcher for a rectangle nobody can see.
    /// </summary>
    private DispatcherTimer? _antTimer;
    private double _antOffset;

    private void StartAnts()
    {
        if (_antTimer != null) return;

        _antTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _antTimer.Tick += (_, _) =>
        {
            if (_selectionRect == null) return;
            _antOffset = (_antOffset + 1) % 8;
            _selectionRect.StrokeDashOffset = _antOffset;
        };
        _antTimer.Start();
    }

    private void StopAnts()
    {
        _antTimer?.Stop();
        _antTimer = null;
    }

    private void SetHandlesVisible(bool visible)
    {
        foreach (Rectangle h in _selectionHandles)
        {
            h.IsVisible = visible;
        }
    }

    private void UpdateSelectionRect(Rect rect)
    {
        EnsureSelectionVisuals();
        if (_selectionRect == null)
        {
            return;
        }

        rect = rect.Intersect(new Rect(0, 0, _snapshotWidth, _snapshotHeight));
        Canvas.SetLeft(_selectionRect, rect.X);
        Canvas.SetTop(_selectionRect, rect.Y);
        _selectionRect.Width = Math.Max(1, rect.Width);
        _selectionRect.Height = Math.Max(1, rect.Height);

        double scale = Math.Max(0.01, CurrentZoom());
        _selectionRect.StrokeThickness = 2.0 / scale;

        // Handles are sized in SOURCE pixels but derived from a SCREEN constant, so they stay
        // the same physical size however far in or out the frame is zoomed.
        double h = HandleScreenPx / scale;
        // ANTS_01 - top-left and bottom-right only.
        var corners = new[]
        {
            new Point(rect.X, rect.Y),
            new Point(rect.X + rect.Width, rect.Y + rect.Height),
        };

        // ROLEPOPUP_01 - the popup is anchored to the box, so any change to the box's geometry or
        // to the zoom moves it. UpdateSelectionRect is the single funnel every such change goes
        // through, which is why the call belongs here rather than at a dozen call sites.
        if (this.FindControl<Border>("RolePopup")?.IsVisible == true)
        {
            PositionRolePopup();
        }

        for (int i = 0; i < _selectionHandles.Count && i < corners.Length; i++)
        {
            Rectangle handle = _selectionHandles[i];
            handle.Width = h;
            handle.Height = h;
            handle.StrokeThickness = 1.5 / scale;
            Canvas.SetLeft(handle, corners[i].X - h / 2);
            Canvas.SetTop(handle, corners[i].Y - h / 2);
        }
    }

    private void SetSourceSelection(SourceRect rect, string? roleKey = null, bool keepRoleName = false)
    {
        _sourceSelection = rect;
        UpdateSelectionRect(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        SetHandlesVisible(true);
        if (_selectionInfo != null)
        {
            _selectionInfo.Text = $"{rect.Width} x {rect.Height} at {rect.X}, {rect.Y}  \u2014  drag inside to move \u00b7 red corners resize \u00b7 arrows nudge \u00b7 Enter to name it";
        }

        // keepRoleName: a move or a resize must not re-guess the element. The user has already
        // chosen (or been offered) a name; silently swapping it because the box crossed the
        // middle of the frame mid-drag would be the kind of thing that makes a tool feel
        // possessed.
        if (keepRoleName)
        {
            return;
        }

        HudRole role = roleKey != null && TryGetRole(roleKey, out HudRole found)
            ? found
            : SuggestRole(rect);
        RoleName = role.DisplayName;
    }

    private void ClearSourceSelection()
    {
        _sourceSelection = null;
        _sourceDrag = SourceDrag.None;

        if (_sourceCanvas != null)
        {
            if (_selectionRect != null)
            {
                _sourceCanvas.Children.Remove(_selectionRect);
            }
            foreach (Rectangle handle in _selectionHandles)
            {
                _sourceCanvas.Children.Remove(handle);
            }
        }

        _selectionRect = null;
        _selectionHandles.Clear();
        StopAnts();   // ANTS_01 - no rectangle, no timer.
        HideRolePopup();
        if (_selectionInfo != null)
        {
            _selectionInfo.Text = "Drag a box around one HUD element, then pick what it is.";
        }
    }


    // ==================================================================================
    // ROLEPOPUP_01 — naming a HUD element happens AT the box.
    //
    // The old flow: drag a box on the frame, then move the eye and the mouse down to a bar at the
    // bottom of the panel, type a name into a free-text field, then find and click ADD SELECTION.
    // Three problems, all of them fatal to a non-technical user:
    //   * the confirm was nowhere near the thing being confirmed;
    //   * free text meant a typo silently created a brand-new element key that
    //     RehydrateSavedLayersAsync would never load back (the old A3 defect);
    //   * nothing on screen told the user which elements this profile even HAS.
    //
    // This is the old Python tool's RoleToolbar (crop_widgets.py) rebuilt as an in-panel Border.
    // Draw a box and the list appears beside it; click a name and the layer is created. Picking
    // the name IS the confirm — there is no second button.
    // ==================================================================================

    /// <summary>ROLEPOPUP_01 — true while the inline "+ New element" row is open.</summary>
    private bool _rolePopupNewOpen;

    /// <summary>
    /// ROLEPOPUP_01 — builds and shows the element chooser next to the current selection.
    ///
    /// Ordering copies <c>_apply_role_priority</c> from the Python tool: the element most likely to
    /// be the one just drawn goes FIRST, guessed from which quadrant of the frame the box sits in.
    /// It is only a guess, so it is only an ordering — nothing is auto-assigned. Elements already
    /// placed in this session are dimmed and sink to the bottom, because picking one REPLACES it,
    /// which is occasionally what you want and usually not.
    /// </summary>
    private void ShowRolePopup()
    {
        var popup = this.FindControl<Border>("RolePopup");
        var list = this.FindControl<StackPanel>("RolePopupList");
        if (popup == null || list == null || _sourceSelection is not { } sel) return;

        CloseRolePopupNewRow();
        list.Children.Clear();

        string? primary = QuadrantGuess(sel).Key;
        var placed = _items.Select(i => i.RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ordered = AllRoles
            .OrderBy(r => placed.Contains(r.Key) ? 1 : 0)
            .ThenBy(r => string.Equals(r.Key, primary, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        foreach (HudRole role in ordered)
        {
            bool already = placed.Contains(role.Key);
            bool isPrimary = !already && string.Equals(role.Key, primary, StringComparison.OrdinalIgnoreCase);

            var button = new Button
            {
                Content = role.DisplayName,
                Classes = { isPrimary ? "Primary" : "Secondary" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
                Padding = new Thickness(8, 5),
                Cursor = new Cursor(StandardCursorType.Hand),
                Opacity = already ? 0.55 : 1.0,
            };
            ToolTip.SetTip(button, already
                ? $"\"{role.DisplayName}\" is already placed. Picking it again replaces it with this box."
                : $"Label this box as \"{role.DisplayName}\".");

            string captured = role.DisplayName;
            button.Click += async (_, _) => await ConfirmSelectionAsAsync(captured);
            list.Children.Add(button);
        }

        // The "+ New element" entry. A plus sign, because that is the one symbol everybody already
        // reads as "make another one".
        var addNew = new Button
        {
            Content = "+  New element…",
            Classes = { "Secondary" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(8, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(addNew, "This HUD piece is not in the list yet. Give it a name and it is added to this profile.");
        addNew.Click += (_, _) => OpenRolePopupNewRow();
        list.Children.Add(addNew);

        popup.IsVisible = true;
        PositionRolePopup();
    }

    /// <summary>
    /// ROLEPOPUP_01 — places the popup beside the selection, inside the panel.
    ///
    /// The selection lives on a canvas that is scaled and scrolled, so its screen position cannot
    /// be computed from its source coordinates — it is MEASURED with TranslatePoint, the same way
    /// the zoom anchor and the auto-zoom centring are.
    ///
    /// Preference is to the RIGHT of the box, because a right-handed drag ends with the pointer at
    /// the box's right edge. It flips to the left when there is no room, and both axes are clamped
    /// so the popup can never leave the panel — the in-panel Border is exactly why none of the old
    /// Python multi-monitor screen-clamping code is needed here.
    /// </summary>
    private void PositionRolePopup()
    {
        var popup = this.FindControl<Border>("RolePopup");
        var host = this.FindControl<ScrollViewer>("SnapshotScroll");
        if (popup == null || host == null || _sourceCanvas == null || _sourceSelection is not { } sel) return;

        popup.UpdateLayout();
        double pw = popup.Bounds.Width > 0 ? popup.Bounds.Width : popup.MinWidth;
        double ph = popup.Bounds.Height > 0 ? popup.Bounds.Height : 200;

        Point? right = _sourceCanvas.TranslatePoint(new Point(sel.X + sel.Width, sel.Y), host);
        Point? left = _sourceCanvas.TranslatePoint(new Point(sel.X, sel.Y), host);
        if (right == null || left == null) return;

        const double Gap = 12;
        double x = right.Value.X + Gap;
        if (x + pw > host.Bounds.Width - 4)
        {
            x = left.Value.X - pw - Gap;
        }

        double y = right.Value.Y;

        x = Math.Clamp(x, 4, Math.Max(4, host.Bounds.Width - pw - 4));
        y = Math.Clamp(y, 4, Math.Max(4, host.Bounds.Height - ph - 4));

        // The popup is a sibling of the ScrollViewer in the same grid cell, so a margin measured
        // from the ScrollViewer's own top-left is the correct offset.
        popup.Margin = new Thickness(x, y, 0, 0);
    }

    private void HideRolePopup()
    {
        var popup = this.FindControl<Border>("RolePopup");
        if (popup != null) popup.IsVisible = false;
        CloseRolePopupNewRow();
    }

    private void OpenRolePopupNewRow()
    {
        var row = this.FindControl<StackPanel>("RolePopupNewRow");
        var box = this.FindControl<TextBox>("RolePopupNewName");
        if (row == null || box == null) return;

        _rolePopupNewOpen = true;
        row.IsVisible = true;
        box.Text = "";
        box.Focus();
        PositionRolePopup();
    }

    private void CloseRolePopupNewRow()
    {
        var row = this.FindControl<StackPanel>("RolePopupNewRow");
        if (row != null) row.IsVisible = false;
        _rolePopupNewOpen = false;
    }

    /// <summary>
    /// ROLEPOPUP_01 — commits the inline "+ New element" name.
    ///
    /// The name is registered in <see cref="_customRoles"/> BEFORE the layer is created, so it is
    /// in the popup's list for every later box in this session. Without that the user would have
    /// to retype it each time, which is how the old free-text field produced near-duplicate keys.
    /// </summary>
    private async Task CommitNewRoleAsync()
    {
        var box = this.FindControl<TextBox>("RolePopupNewName");
        string name = box?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Type a name for the new HUD element first.");
            return;
        }

        RegisterCustomRole(name);
        CloseRolePopupNewRow();
        await ConfirmSelectionAsAsync(name);
    }

    /// <summary>
    /// ROLEPOPUP_01 — one click in the popup = one finished layer.
    /// </summary>
    private async Task ConfirmSelectionAsAsync(string displayName)
    {
        RoleName = displayName;
        HideRolePopup();
        await AddCurrentSelection();
    }

    /// <summary>
    /// ROLEPOPUP_01 — which element the box is MOST LIKELY to be, from where it sits on the frame.
    /// Ported from the Python tool's _apply_role_priority. Purely an ordering hint for the popup;
    /// it never assigns anything on its own.
    /// </summary>
    private HudRole QuadrantGuess(SourceRect rect)
    {
        bool right = rect.X + rect.Width / 2.0 > _snapshotWidth / 2.0;
        bool bottom = rect.Y + rect.Height / 2.0 > _snapshotHeight / 2.0;

        string key = (bottom, right) switch
        {
            (false, false) => "team",
            (false, true) => "stats",
            (true, true) => "loot",
            (true, false) => "normal_hp",
        };

        return TryGetRole(key, out HudRole role) ? role : Roles[0];
    }
    private async Task AddCurrentSelection()
    {
        if (_sourceSelection == null || string.IsNullOrWhiteSpace(_snapshotPath))
        {
            return;
        }

        string roleName = RoleName?.Trim() ?? "";
        if (string.IsNullOrEmpty(roleName))
        {
            SetStatus("Please enter a name for the HUD element.");
            return;
        }

        string roleKey = roleName.ToLowerInvariant().Replace(" ", "_");
        // ROLEPOPUP_01: TryGetRole, not RoleByKey - a name created by "+ New element" lives in
        // _customRoles, and looking only at the built-in six would mint a fresh HudRole here on
        // every use and lose the one the user named.
        HudRole role = TryGetRole(roleKey, out HudRole existingRole)
            ? existingRole
            : RegisterCustomRole(roleName);

        SourceRect sourceRect = ClampSourceRect(_sourceSelection.Value);
        if (sourceRect.Width < MinSelectionSize || sourceRect.Height < MinSelectionSize)
        {
            SetStatus("Selection is too small.");
            return;
        }

        try
        {
            var contentRect = CoordinateMath.TransformToContentAreaInt(
                (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
                _originalResolution);
            if (contentRect.w < 2 || contentRect.h < 2)
            {
                SetStatus("Selection does not map to a visible portrait area.");
                return;
            }

            string snapshotPath = _snapshotPath;
            string cropPath = await CropSnapshotRegionForExportPreviewAsync(snapshotPath, sourceRect, role.Key);
            _tempFiles.Add(cropPath);

            // NODUPES_01 — one element, one entry, always. Picking an element that is already on
            // the composer REPLACES it head to head rather than adding a second copy.
            //
            // The comparison is OrdinalIgnoreCase, not ==. Keys are lowercased when they are minted
            // from a display name, but keys ADOPTED from a profile document (AdoptRolesFromConfig)
            // are whatever that file contains, and a hand-edited or older config can hold "Loot" or
            // "LOOT". Case-sensitive == would miss those and quietly leave two items writing to one
            // config key, where the last one to save wins and the other silently disappears.
            foreach (CropEditorItem duplicate in _items
                         .Where(i => string.Equals(i.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                RuntimeLog.Info("CROP", $"Replacing existing '{duplicate.DisplayName}' with the new selection.");
                RemoveItem(duplicate);
            }

            var initialSize = QuantizeItemSize(sourceRect, contentRect.w, role.Key);
            int width = initialSize.width;
            int height = initialSize.height;

            int initialX = role.DefaultX >= 0 ? (int)role.DefaultX : contentRect.x;
            int initialY = role.DefaultY >= 0 ? (int)role.DefaultY : contentRect.y + CoordinateConstants.UIPaddingTop;

            (int x, int y) = ClampOverlay(initialX, initialY, width, height);

            int z = role.DefaultZ;
            if (_items.Any(i => i.Z == z))
            {
                z = _items.Max(i => i.Z) + 1;
            }

            var item = CreateItem(new ItemSnapshot(role.Key, role.DisplayName, sourceRect, cropPath, x, y, width, height, z));
            _items.Add(item);
            _deletedRoleKeys.Remove(role.Key);
            SelectItem(item);
            RefreshLayerList();
            MarkDirty();
            PushHistory();
            RuntimeLog.Info("CROP", $"Added HUD element: {role.DisplayName} (role={role.Key}, source={sourceRect.Width}x{sourceRect.Height} at ({sourceRect.X},{sourceRect.Y}), z={z})");
            ClearSourceSelection();
            ClearMagicWandCandidates();

            // CANCELSEL_01 - this box is committed, so the rewind point retires with it. Without
            // this, cancelling the NEXT box would rewind the view to where THIS one started.
            _preZoomState = null;

            SetWizardState(4, "Portrait Composer", $"Adjust {role.DisplayName}, then finish and save.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Add selection failed: {ex.Message}");
            SetStatus("Could not add that selection.");
        }
    }

    private async Task<string> CropSnapshotRegionForExportPreviewAsync(string snapshotPath, SourceRect sourceRect, string roleKey)
    {
        var contentRect = CoordinateMath.TransformToContentAreaInt(
            (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
            _originalResolution,
            HudConfig.CropDriftType(roleKey));
        var exportRect = CoordinateMath.InverseTransformFromContentAreaInt(
            (contentRect.x, contentRect.y, contentRect.w, contentRect.h),
            _originalResolution,
            HudConfig.CropDriftType(roleKey));
        return await CropSnapshotRegionAsync(snapshotPath, new SourceRect(exportRect.x, exportRect.y, exportRect.w, exportRect.h)).ConfigureAwait(false);
    }

    private async Task<string> CropSnapshotRegionAsync(string snapshotPath, SourceRect rect)
    {
        _paths.EnsureWritableDirectories();
        string output = IOPath.Combine(_paths.TempDirectory, $"crop_item_{Guid.NewGuid():N}.png");

        using var data = await Task.Run(() =>
        {
            using SKBitmap source = SKBitmap.Decode(snapshotPath) ?? throw new IOException("Could not decode snapshot.");
            SourceRect clamped = ClampSourceRect(rect, source.Width, source.Height);

            using var target = new SKBitmap(clamped.Width, clamped.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(target);
            canvas.Clear(SKColors.Transparent);

            var src = new SKRect(clamped.X, clamped.Y, clamped.X + clamped.Width, clamped.Y + clamped.Height);
            var dst = new SKRect(0, 0, clamped.Width, clamped.Height);
            canvas.DrawBitmap(source, src, dst);
            canvas.Flush();

            using SKImage image = SKImage.FromBitmap(target);
            return image.Encode(SKEncodedImageFormat.Png, 100);
        }).ConfigureAwait(false);

        using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
        {
            data.SaveTo(fs);
            await fs.FlushAsync().ConfigureAwait(false);
        }

        return output;
    }

    private CropEditorItem CreateItem(ItemSnapshot snapshot)
    {
        var root = new Canvas
        {
            Width = snapshot.Width,
            Height = snapshot.Height,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Focusable = true,
            Tag = null,
            ZIndex = snapshot.Z,

            // ITEMHIT_01 — THE reason a placed element could not be picked up or moved.
            //
            // In Avalonia a Panel with Background = null does not take part in hit testing at all:
            // there is nothing painted, so there is nothing to hit. Every child inside this Canvas
            // is IsHitTestVisible = false (the image, the outline, the label) and the only hittable
            // parts are the two red handles, which UpdateItemVisual keeps HIDDEN unless the item is
            // already selected. A freshly placed element was therefore completely inert: no click,
            // no select, no drag, and no way to reach the handles that would have let you select it.
            //
            // It was masked for as long as the LAYERS list existed, because clicking a row there was
            // what called SelectItem. Removing that list (LAYERSPANE_01) took away the last route in
            // and turned a latent bug into a dead feature.
            //
            // Brushes.Transparent is NOT the same as null here: a transparent brush paints nothing
            // but IS hit-testable, which is exactly what is wanted. Do not "tidy" this to null.
            Background = Brushes.Transparent,
        };
        Avalonia.Automation.AutomationProperties.SetName(root, $"{snapshot.DisplayName} crop item");

        var image = new Image
        {
            Width = snapshot.Width,
            Height = snapshot.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };

        if (File.Exists(snapshot.CropImagePath))
        {
            using var fs = File.OpenRead(snapshot.CropImagePath);
            image.Source = new Bitmap(fs);
        }

        var border = new Rectangle
        {
            Width = snapshot.Width + 4,
            Height = snapshot.Height + 4,
            Stroke = Brushes.Black,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(border, -2);
        Canvas.SetTop(border, -2);

        // HANDLECURSOR_01 — the two resize handles carried StandardCursorType.SizeAll, which is the
        // SAME cursor the item root uses for "drag me". Hovering a corner therefore looked identical
        // to hovering the middle, so nothing told the user the corners resize. Diagonal arrows, the
        // universal resize affordance, and they point along the axis each corner actually moves.
        var tlHandle = CreateHandle(StandardCursorType.TopLeftCorner);
        var brHandle = CreateHandle(StandardCursorType.BottomRightCorner);

        var labelText = new TextBlock
        {
            Text = "",
            Foreground = Brushes.White,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(18),
            FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3),
            Child = labelText,
            IsHitTestVisible = false
        };

        root.Children.Add(image);
        root.Children.Add(border);
        root.Children.Add(tlHandle);
        root.Children.Add(brHandle);
        root.Children.Add(label);

        var item = new CropEditorItem
        {
            RoleKey = snapshot.RoleKey,
            DisplayName = snapshot.DisplayName,
            SourceRect = snapshot.SourceRect,
            CropImagePath = snapshot.CropImagePath,
            X = snapshot.X,
            Y = snapshot.Y,
            Width = snapshot.Width,
            Height = snapshot.Height,
            Z = snapshot.Z,
            Root = root,
            Image = image,
            Border = border,
            TopLeftHandle = tlHandle,
            BottomRightHandle = brHandle,
            LabelHost = label,
            LabelText = labelText
        };

        root.Tag = item;
        tlHandle.Tag = ResizeHandle.TopLeft;
        brHandle.Tag = ResizeHandle.BottomRight;

        root.PointerPressed += Item_PointerPressed;
        root.PointerMoved += Item_PointerMoved;
        root.PointerReleased += Item_PointerReleased;

        // ITEMMENU_01 - right-click an element for ordering and delete, the way the old Python
        // tool did (portrait_view.py contextMenuEvent). The footer buttons still exist and do the
        // same things; this is the version you reach without moving the mouse off the element you
        // are already working on, which is what the removed LAYERS list was being used for.
        root.ContextMenu = BuildItemContextMenu();

        if (_portraitCanvas != null)
        {
            _portraitCanvas.Children.Add(root);
        }

        ApplyItemLayout(item);
        UpdateItemVisual(item);
        return item;
    }

    /// <summary>
    /// ITEMMENU_01 - the per-element right-click menu.
    ///
    /// Built fresh per item rather than shared, because Avalonia's ContextMenu carries its own
    /// placement target: one instance attached to several items would open against whichever it
    /// was last attached to. The commands act on _selectedItem, and opening the menu selects the
    /// item first (the right-click is routed through Item_PointerPressed), so "the one I
    /// right-clicked" and "the selected one" are always the same element.
    /// </summary>
    private ContextMenu BuildItemContextMenu()
    {
        var bringForward = new MenuItem { Header = "Bring Forward" };
        bringForward.Click += (_, _) => MoveSelectedLayer(1);

        var sendBackward = new MenuItem { Header = "Send Backward" };
        sendBackward.Click += (_, _) => MoveSelectedLayer(-1);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteSelectedItem();

        return new ContextMenu
        {
            ItemsSource = new List<object>
            {
                bringForward,
                sendBackward,
                new Separator(),
                delete,
            }
        };
    }

    private Rectangle CreateHandle(StandardCursorType cursor)
    {
        return new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red),   // TONE_01
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Cursor = new Cursor(cursor),
            IsVisible = false,
            ZIndex = 20
        };
    }

    private void Item_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Canvas root || root.Tag is not CropEditorItem item || _portraitCanvas == null)
        {
            return;
        }

        SelectItem(item);
        root.Focus();
        _activeEditItem = item;
        _editStartSnapshot = CaptureSnapshot();
        _editPointerStart = e.GetPosition(_portraitCanvas);
        _editStartX = item.X;
        _editStartY = item.Y;
        _editStartWidth = item.Width;
        _editStartHeight = item.Height;

        if (e.Source is Control source && source.Tag is ResizeHandle handle)
        {
            _composerEditMode = handle == ResizeHandle.TopLeft ? ComposerEditMode.ResizeTopLeft : ComposerEditMode.ResizeBottomRight;
        }
        else
        {
            _composerEditMode = ComposerEditMode.Drag;
        }

        if (this.FindControl<Grid>("RuleOfThirdsGrid") is Grid grid)
            grid.Opacity = 1;

        e.Pointer.Capture(root);
        e.Handled = true;
    }

    private void Item_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeEditItem == null || _composerEditMode == ComposerEditMode.None || _portraitCanvas == null)
        {
            return;
        }

        Point p = e.GetPosition(_portraitCanvas);
        double dx = p.X - _editPointerStart.X;
        double dy = p.Y - _editPointerStart.Y;

        if (_composerEditMode == ComposerEditMode.Drag)
        {
            double x = _editStartX + dx;
            double y = _editStartY + dy;
            (x, y) = SnapPosition(_activeEditItem, x, y, _activeEditItem.Width, _activeEditItem.Height);
            (int ix, int iy) = ClampOverlay(x, y, _activeEditItem.Width, _activeEditItem.Height);
            _activeEditItem.X = ix;
            _activeEditItem.Y = iy;
        }
        else if (_composerEditMode == ComposerEditMode.ResizeBottomRight)
        {
            ResizeFromBottomRight(_activeEditItem, dx);
        }
        else if (_composerEditMode == ComposerEditMode.ResizeTopLeft)
        {
            ResizeFromTopLeft(_activeEditItem, dx);
        }

        ApplyItemLayout(_activeEditItem);
        _dirty = true;
        RefreshActionButtons();
        e.Handled = true;
    }

    private void Item_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeEditItem == null)
        {
            return;
        }

        e.Pointer.Capture(null);
        ClearGuides();

        if (this.FindControl<Grid>("RuleOfThirdsGrid") is Grid grid)
            grid.Opacity = 0;

        EditorSnapshot current = CaptureSnapshot();
        if (_editStartSnapshot != null && !SnapshotsEqual(_editStartSnapshot, current))
        {
            MarkDirty();
            PushHistory(current);
            RefreshLayerList();
        }

        _activeEditItem = null;
        _composerEditMode = ComposerEditMode.None;
        _editStartSnapshot = null;
        e.Handled = true;
    }

    private void ResizeFromBottomRight(CropEditorItem item, double dx)
    {
        double aspect = (double)_editStartHeight / Math.Max(1, _editStartWidth);
        double width = Math.Max(MinItemSize, _editStartWidth + dx);
        double height = width * aspect;

        if (_editStartX + width > PortraitWidth)
        {
            width = PortraitWidth - _editStartX;
            height = width * aspect;
        }

        if (_editStartY + height > ContentBottom)
        {
            height = ContentBottom - _editStartY;
            width = height / aspect;
        }

        var quantized = QuantizeItemSize(item.SourceRect, width, item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
        (item.X, item.Y) = ClampOverlay(_editStartX, _editStartY, item.Width, item.Height);
    }

    private void ResizeFromTopLeft(CropEditorItem item, double dx)
    {
        double aspect = (double)_editStartHeight / Math.Max(1, _editStartWidth);
        int anchorRight = (int)(_editStartX + _editStartWidth);
        int anchorBottom = (int)(_editStartY + _editStartHeight);

        double width = Math.Max(MinItemSize, _editStartWidth - dx);
        double height = width * aspect;
        double x = anchorRight - width;
        double y = anchorBottom - height;

        if (x < 0)
        {
            width = anchorRight;
            height = width * aspect;
        }

        if (y < ContentTop)
        {
            height = anchorBottom - ContentTop;
            width = height / aspect;
        }

        var quantized = QuantizeItemSize(item.SourceRect, width, item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
        item.X = Math.Max(0, anchorRight - item.Width);
        item.Y = Math.Max((int)ContentTop, anchorBottom - item.Height);
        (item.X, item.Y) = ClampOverlay(item.X, item.Y, item.Width, item.Height);
    }

    private void ApplyItemLayout(CropEditorItem item)
    {
        var quantized = QuantizeItemSize(item.SourceRect, item.Width, item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
        (item.X, item.Y) = ClampOverlay(item.X, item.Y, item.Width, item.Height);

        item.Root.Width = item.Width;
        item.Root.Height = item.Height;
        item.Root.ZIndex = item.Z;
        Canvas.SetLeft(item.Root, item.X);
        Canvas.SetTop(item.Root, item.Y);

        item.Image.Width = item.Width;
        item.Image.Height = item.Height;
        item.Border.Width = item.Width + 4;
        item.Border.Height = item.Height + 4;
        Canvas.SetLeft(item.Border, -2);
        Canvas.SetTop(item.Border, -2);

        Canvas.SetLeft(item.TopLeftHandle, -HandleSize / 2);
        Canvas.SetTop(item.TopLeftHandle, -HandleSize / 2);
        Canvas.SetLeft(item.BottomRightHandle, item.Width - HandleSize / 2);
        Canvas.SetTop(item.BottomRightHandle, item.Height - HandleSize / 2);

        double labelWidth = Math.Clamp(Math.Max(96, item.Width), 96, PortraitWidth - 20);
        double labelLeft = (item.Width - labelWidth) / 2;
        labelLeft = Math.Clamp(labelLeft, -item.X + 10, PortraitWidth - item.X - labelWidth - 10);
        item.LabelHost.Width = labelWidth;
        Canvas.SetLeft(item.LabelHost, labelLeft);
        Canvas.SetTop(item.LabelHost, item.Y > PortraitHeight / 2 ? -34 : item.Height + 10);
        item.LabelText.Text = item.DisplayName.ToUpperInvariant();
    }

    private (int width, int height, Frac scale) QuantizeItemSize(SourceRect sourceRect, double desiredWidth, string? roleKey = null)
    {
        var contentRect = CoordinateMath.TransformToContentAreaInt(
            (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
            _originalResolution,
            HudConfig.CropDriftType(roleKey ?? ""));

        int contentW = Math.Max(2, contentRect.w);
        int contentH = Math.Max(2, contentRect.h);

        long maxDesW = (long)Math.Round(Math.Max(MinItemSize, desiredWidth));
        var quantizedScale = new Frac(maxDesW, contentW);

        var (width, height) = CoordinateMath.QuantizeBackendSize(contentW, contentH, quantizedScale);

        return (width, height, quantizedScale);
    }

    private void SelectItem(CropEditorItem? item, bool updateLayerList = true)
    {
        _selectedItem = item;
        foreach (CropEditorItem editorItem in _items)
        {
            UpdateItemVisual(editorItem);
        }

        if (updateLayerList && _layerList != null)
        {
            _suppressLayerSelection = true;
            _layerList.SelectedItem = _layers.FirstOrDefault(l => item != null && l.RoleKey == item.RoleKey);
            _suppressLayerSelection = false;
        }

        RefreshActionButtons();
    }

    private void UpdateItemVisual(CropEditorItem item)
    {
        bool selected = ReferenceEquals(item, _selectedItem);
        item.Border.Stroke = selected ? Brushes.Gold : Brushes.Black;
        item.Border.StrokeThickness = 2;
        item.TopLeftHandle.IsVisible = selected;
        item.BottomRightHandle.IsVisible = selected;
        item.LabelHost.Background = selected
            ? new SolidColorBrush(Color.FromArgb(220, 113, 63, 18))
            : new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));
    }

    /// <summary>
    /// ISSUE_2 — role keys the user explicitly deleted this session.
    ///
    /// SaveConfig MERGES into the config on disk rather than replacing it, and that must stay that
    /// way: the document can hold elements this session never touched, and pruning every key not in
    /// _items would wipe them. This set is therefore the only signal that a removal was deliberate
    /// rather than merely absent.
    ///
    /// DELETESET_01 — it is cleared by ResetWorkingState, which is what runs on a profile switch.
    /// A tombstone belongs to the profile that created it.
    /// </summary>
    private readonly HashSet<string> _deletedRoleKeys = new(StringComparer.Ordinal);

    private void DeleteSelectedItem()
    {
        if (_selectedItem == null)
        {
            return;
        }

        _deletedRoleKeys.Add(_selectedItem.RoleKey);
        RemoveItem(_selectedItem);
        SelectItem(null);
        RefreshLayerList();
        MarkDirty();
        PushHistory();
    }

    private void RemoveItem(CropEditorItem item)
    {
        if (_portraitCanvas != null)
        {
            _portraitCanvas.Children.Remove(item.Root);
        }

        try
        {
            (item.Image.Source as IDisposable)?.Dispose();
            item.Image.Source = null;
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        _items.Remove(item);
    }

    private void MoveSelectedLayer(int delta)
    {
        if (_selectedItem == null)
        {
            return;
        }

        _selectedItem.Z += delta;
        if (_selectedItem.Z < 1)
        {
            _selectedItem.Z = 1;
        }

        ApplyItemLayout(_selectedItem);
        RefreshLayerList();
        MarkDirty();
        PushHistory();
    }

    private void RefreshLayerList()
    {
        string? selectedKey = _selectedItem?.RoleKey;
        _layers.Clear();
        foreach (CropEditorItem item in _items.OrderByDescending(i => i.Z).ThenBy(i => i.DisplayName))
        {
            _layers.Add(new LayerEntry(item.RoleKey, item.DisplayName, item.Z));
        }

        if (_layerList != null && selectedKey != null)
        {
            _layerList.SelectedItem = _layers.FirstOrDefault(l => l.RoleKey == selectedKey);
        }

        // LAYERSPANE_01 - EmptyLayersText belonged to the removed list. Resolving to null here is
        // expected, not a bug; the guard below keeps the call harmless.
        var emptyLayers = this.FindControl<TextBlock>("EmptyLayersText");
        if (emptyLayers != null) emptyLayers.IsVisible = _layers.Count == 0;

        RefreshActionButtons();
    }

    /// <summary>
    /// IDEA_1 — turns previously saved layers back into REAL, draggable items.
    ///
    /// Before this existed the editor was write-only: saved layers appeared as read-only green
    /// ghosts and the only way to change one was to delete it and redraw the whole box. _items was
    /// filled solely by AddSelection and by undo restore.
    ///
    /// THE DRIFT TRAP THIS AVOIDS. The saved "crops_1080p" rect is content-space. Converting it
    /// back to source pixels uses CoordinateMath.InverseTransformFromContentAreaInt, and saving
    /// converts forward again with TransformToContentAreaInt — and BOTH round strictly outward by
    /// design, so composing them grows the box up to 2px per axis, every single cycle. That is why
    /// the source rect is now persisted separately (crops_source) and read back verbatim here.
    /// The inverse transform is used ONLY as the one-time migration for a pre-v4 file, which costs
    /// exactly the single outward snap that already happens today.
    ///
    /// Safe to call more than once: a role already present in _items is skipped.
    /// </summary>
    private async Task RehydrateSavedLayersAsync()
    {
        if (_portraitCanvas == null) return;

        try
        {
            JsonObject config = await new CropConfigStore(_paths).LoadAsync();
            JsonObject crops = EnsureObject(config, "crops_1080p");
            JsonObject scales = EnsureObject(config, "scales");
            JsonObject overlays = EnsureObject(config, "overlays");
            JsonObject zOrders = EnsureObject(config, "z_orders");
            JsonObject sourceCrops = EnsureObject(config, CropConfigDefaults.SourceCropsSection);

            // ROLEPOPUP_01 / A3 - register every element key the PROFILE knows about before
            // iterating. This loop used to be `foreach (HudRole role in Roles)` over the six
            // hardcoded Fortnite names, which meant an element the user had named themselves was
            // written to the config on save and then silently invisible on every later open: the
            // reader simply never asked about that key. Anything enumerating elements goes through
            // AllRoles, and AllRoles only knows what has been registered.
            AdoptRolesFromConfig(crops);
            AdoptRolesFromConfig(sourceCrops);

            foreach (HudRole role in AllRoles.ToList())
            {
                if (_items.Any(i => string.Equals(i.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase))) continue;
                if (_deletedRoleKeys.Contains(role.Key)) continue;

                if (crops[role.Key] is not JsonArray crop || crop.Count < 4) continue;

                int cropW = ReadInt(crop[0], 0);
                int cropH = ReadInt(crop[1], 0);
                if (cropW <= 1 || cropH <= 1) continue;

                SourceRect sourceRect;
                if (sourceCrops[role.Key] is JsonArray src && src.Count >= 4)
                {
                    sourceRect = new SourceRect(
                        ReadInt(src[2], 0), ReadInt(src[3], 0),
                        Math.Max(2, ReadInt(src[0], 2)), Math.Max(2, ReadInt(src[1], 2)));
                }
                else
                {
                    var derived = CoordinateMath.InverseTransformFromContentAreaInt(
                        (ReadInt(crop[2], 0), ReadInt(crop[3], 0), cropW, cropH),
                        _originalResolution,
                        HudConfig.CropDriftType(role.Key));
                    sourceRect = new SourceRect(derived.x, derived.y, derived.w, derived.h);
                    RuntimeLog.Info("CROP", $"Migrated '{role.Key}' to a stored source rect (pre-v4 config).");
                }

                sourceRect = ClampSourceRect(sourceRect);

                Frac scale = ReadFrac(scales[role.Key], Frac.One);
                var (w, h) = CoordinateMath.QuantizeBackendSize(cropW, cropH, scale);

                double ox = role.DefaultX, oy = role.DefaultY;
                if (overlays[role.Key] is JsonObject ov)
                {
                    ox = ReadDouble(ov["x"], role.DefaultX);
                    oy = ReadDouble(ov["y"], role.DefaultY);
                }

                int z = ReadInt(zOrders[role.Key], role.DefaultZ);

                string cropImagePath = string.Empty;
                if (_snapshotPath != null)
                {
                    try { cropImagePath = await CropSnapshotRegionAsync(_snapshotPath, sourceRect); }
                    catch (Exception ex) { RuntimeLog.Info("CROP", $"Thumbnail for '{role.Key}' could not be built: {ex.Message}"); }
                    if (!string.IsNullOrEmpty(cropImagePath)) _tempFiles.Add(cropImagePath);
                }

                var item = CreateItem(new ItemSnapshot(
                    role.Key, role.DisplayName, sourceRect, cropImagePath,
                    (int)Math.Round(ox), (int)Math.Round(oy), w, h, z));

                item.FromSavedConfig = true;   // GHOSTKILL_01
                _items.Add(item);
            }

            RefreshLayerList();
            RefreshActionButtons();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Saved layers could not be reopened for editing: {ex.Message}");
        }
    }

    /// <summary>
    /// IDEA_1 — fills in the picture for any item rehydrated before a video was loaded.
    /// Called after a snapshot is available. Items that already have an image are left alone.
    /// </summary>
    private async Task RefreshRehydratedThumbnailsAsync()
    {
        if (_snapshotPath == null) return;

        foreach (CropEditorItem item in _items.ToList())
        {
            if (!string.IsNullOrEmpty(item.CropImagePath) && File.Exists(item.CropImagePath)) continue;

            try
            {
                string path = await CropSnapshotRegionAsync(_snapshotPath, item.SourceRect);
                if (string.IsNullOrEmpty(path)) continue;

                _tempFiles.Add(path);
                item.CropImagePath = path;

                using var fs = File.OpenRead(path);
                item.Image.Source = new Bitmap(fs);
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("CROP", $"Thumbnail refresh failed for '{item.RoleKey}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// GHOSTKILL_01 - shows, hides and dims the elements that came from the saved profile.
    ///
    /// This replaces LoadExistingPlaceholdersAsync and UpdatePlaceholderOpacity, both deleted. They
    /// drew a second, non-interactive copy of every saved crop as a green rectangle, and then
    /// skipped any role already present in _items - which, since RehydrateSavedLayersAsync loads
    /// every saved crop as a real item, meant the ghost list was always empty. The checkbox above
    /// them toggled nothing, which is exactly what was reported.
    ///
    /// Opacity is applied to the saved items only, so a dimmed element still reads as "this was
    /// already here" while remaining a fully live object you can drag, resize and re-save.
    /// </summary>
    private void ApplySavedCropVisibility()
    {
        bool show = this.FindControl<CheckBox>("ShowPlaceholders")?.IsChecked == true;
        double alpha = Math.Clamp(this.FindControl<Slider>("PlaceholderOpacitySlider")?.Value ?? 95, 20, 255) / 255.0;

        foreach (CropEditorItem item in _items)
        {
            if (!item.FromSavedConfig) continue;

            item.Root.IsVisible = show;
            item.Root.Opacity = alpha;
        }

        // A hidden element must not stay selected: the footer and the ordering arrows would still
        // act on something invisible, which is how a user "loses" an element they cannot see.
        if (!show && _selectedItem?.FromSavedConfig == true)
        {
            SelectItem(null);
        }
    }

    private void ShowMagicWandCandidates()
    {
        if (_sourceCanvas == null || _snapshotPath == null)
        {
            SetStatus("Take a snapshot first.");
            return;
        }

        ClearMagicWandCandidates();

        CandidateSpec[] candidates =
        [
            CandidateFromRatio("stats", 0.65, 0.02, 0.32, 0.28),
            CandidateFromRatio("normal_hp", 0.02, 0.72, 0.36, 0.12),
            CandidateFromRatio("loot", 0.58, 0.72, 0.38, 0.18),
            CandidateFromRatio("team", 0.02, 0.06, 0.25, 0.30),
            CandidateFromRatio("spectating", 0.47, 0.05, 0.08, 0.08),
            CandidateFromRatio("boss_hp", 0.34, 0.04, 0.32, 0.08),
        ];

        foreach (CandidateSpec candidate in candidates)
        {
            var rect = new Rectangle
            {
                Width = candidate.Rect.Width,
                Height = candidate.Rect.Height,
                Stroke = new SolidColorBrush(Color.Parse("#e91e63")),
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(24, 233, 30, 99)),
                Tag = candidate,
                Cursor = new Cursor(StandardCursorType.Hand),
                ZIndex = 450
            };

            Canvas.SetLeft(rect, candidate.Rect.X);
            Canvas.SetTop(rect, candidate.Rect.Y);
            _sourceCanvas.Children.Add(rect);
            _candidateControls.Add(rect);
        }

        SetWizardState(3, "Refine Box", "Magic Wand candidates shown. Click a box or draw manually.");
    }

    private CandidateSpec CandidateFromRatio(string roleKey, double x, double y, double w, double h)
    {
        var rect = new SourceRect(
            CoordinateMath.ScaleRound(Frac.FromDouble(_snapshotWidth * x)),
            CoordinateMath.ScaleRound(Frac.FromDouble(_snapshotHeight * y)),
            CoordinateMath.ScaleRound(Frac.FromDouble(_snapshotWidth * w)),
            CoordinateMath.ScaleRound(Frac.FromDouble(_snapshotHeight * h)));
        return new CandidateSpec(roleKey, ClampSourceRect(rect));
    }

    private void ClearMagicWandCandidates()
    {
        if (_sourceCanvas == null)
        {
            _candidateControls.Clear();
            return;
        }

        foreach (Control control in _candidateControls)
        {
            _sourceCanvas.Children.Remove(control);
        }
        _candidateControls.Clear();
    }

    private async Task SaveAndReturnAsync(object? sender)
    {
        // GATE_01 (F3): the save path ends in SyncActiveProfileFromCurrentConfig(), which writes
        // over a profile FILE. Reaching it with no chosen profile means overwriting whichever
        // profile some other part of the suite left active.
        if (_activeProfile == null)
        {
            SetStatus("Choose a profile before saving.");
            return;
        }

        // SAVECONFIRM_01 (F4) - the one confirmation this window was missing.
        // FINISH & SAVE does not write "somewhere safe": it replaces
        // MaskProfiles\<profile>.json wholesale, and the shipped presets (Fortnite, Battlefield,
        // Counter Strike, ...) are ordinary files in that folder with no special protection. The
        // only recovery is CropConfigStore's five rotating .bak files, which the user has no UI
        // for. So the profile being overwritten is named IN THE PROMPT - not "your changes" but
        // the actual file name - because the whole class of accident here is saving into the
        // wrong profile, and a generic "Save changes?" cannot catch that.
        // destructive:true paints YES red and puts Enter on NO (DIALOG_02), so a reflex keypress
        // does not overwrite a preset.
        bool confirmed = await Controls.ConfirmDialogWindow.AskAsync(
            this,
            $"This will overwrite the \"{_activeProfile}\" profile with the {_items.Count} element(s) on the phone preview.\n\n" +
            "The version currently saved in that profile will be replaced.",
            "Overwrite \"" + _activeProfile + "\"?",
            yesText: "OVERWRITE",
            noText: "KEEP IT",
            destructive: true);

        if (!confirmed)
        {
            SetStatus("Nothing was saved. \"" + _activeProfile + "\" is unchanged.");
            return;
        }

        Button? button = sender as Button;
        if (button != null)
        {
            button.IsEnabled = false;
        }

        var thinkingOverlay = this.FindControl<Grid>("ThinkingOverlay");
        if (thinkingOverlay != null) thinkingOverlay.IsVisible = true;
        
        await Task.Yield();

        bool saved = await SaveConfigAsync();

        if (thinkingOverlay != null) thinkingOverlay.IsVisible = false;

        if (saved)
        {
            var summaryOverlay = this.FindControl<Grid>("SummaryOverlay");
            if (summaryOverlay != null)
            {
                var summaryContent = this.FindControl<StackPanel>("SummaryContent");
                if (summaryContent != null)
                {
                    summaryContent.Children.Clear();
                    var headerModBorder = new Border { Background = SolidColorBrush.Parse("#1e10b981"), Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
                    headerModBorder.Child = new TextBlock { Text = "  MODIFIED ELEMENTS", Foreground = SolidColorBrush.Parse("#10b981"), FontWeight = FontWeight.Bold, FontSize = Infrastructure.ThemeManager.ScaledFontSize(14) };
                    summaryContent.Children.Add(headerModBorder);
                    foreach(var item in _items)
                    {
                        summaryContent.Children.Add(new TextBlock { Text = $"  ✓  {item.DisplayName}", Foreground = SolidColorBrush.Parse("#94a3b8"), FontSize = Infrastructure.ThemeManager.ScaledFontSize(16) });
                    }
                    
                    var existingKeys = _items.Select(x => x.RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var untouched = AllRoles.Where(r => !existingKeys.Contains(r.Key)).ToList();
                    
                    if (untouched.Count > 0)
                    {
                        summaryContent.Children.Add(new Border { Height = 20 });
                        var headerUnBorder = new Border { Background = SolidColorBrush.Parse("#1e9ca3af"), Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
                        headerUnBorder.Child = new TextBlock { Text = "  UNTOUCHED (DEFAULTS)", Foreground = SolidColorBrush.Parse("#9ca3af"), FontWeight = FontWeight.Bold, FontSize = Infrastructure.ThemeManager.ScaledFontSize(14) };
                        summaryContent.Children.Add(headerUnBorder);
                        foreach(var u in untouched)
                        {
                            summaryContent.Children.Add(new TextBlock { Text = $"  •  {u.DisplayName}", Foreground = SolidColorBrush.Parse("#9ca3af"), FontSize = Infrastructure.ThemeManager.ScaledFontSize(15) });
                        }
                    }
                }
                
                summaryOverlay.IsVisible = true;

                var returnNowButton = this.FindControl<Button>("ReturnNowButton");
                var returnTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnReturnNow(object? s, RoutedEventArgs e) => returnTcs.TrySetResult(true);
                void OnOverlayPointer(object? s, Avalonia.Input.PointerPressedEventArgs e) => returnTcs.TrySetResult(true);
                void OnOverlayKey(object? s, Avalonia.Input.KeyEventArgs e)
                {
                    if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space or Avalonia.Input.Key.Escape)
                    {
                        returnTcs.TrySetResult(true);
                    }
                }

                if (returnNowButton != null) returnNowButton.Click += OnReturnNow;
                summaryOverlay.PointerPressed += OnOverlayPointer;
                summaryOverlay.KeyDown += OnOverlayKey;
                summaryOverlay.Focus();

                try
                {
                    using var cts = new CancellationTokenSource(1000);
                    cts.Token.Register(() => returnTcs.TrySetResult(false));
                    await returnTcs.Task;
                }
                finally
                {
                    if (returnNowButton != null) returnNowButton.Click -= OnReturnNow;
                    summaryOverlay.PointerPressed -= OnOverlayPointer;
                    summaryOverlay.KeyDown -= OnOverlayKey;
                }
            }
            await ReturnToMainAppAsync();
            return;
        }

        if (button != null)
        {
            button.IsEnabled = true;
            button.Content = "FINISH & SAVE";
        }
        RefreshActionButtons();
    }

    private async Task<bool> SaveConfigAsync()
    {
        if (_items.Count == 0)
        {
            SetStatus("No HUD elements are currently placed.");
            return false;
        }

        try
        {
            RuntimeLog.Info("CROP", "Saving crop coordinates.");
            var store = new CropConfigStore(_paths);
            
            // Recover before changing backups. SaveAsync owns the single, locked rotation.
            JsonObject config = await store.LoadAsync();

            JsonObject crops = EnsureObject(config, "crops_1080p");
            JsonObject scales = EnsureObject(config, "scales");
            JsonObject overlays = EnsureObject(config, "overlays");
            JsonObject zOrders = EnsureObject(config, "z_orders");

            JsonObject sourceCrops = EnsureObject(config, CropConfigDefaults.SourceCropsSection);

            foreach (CropEditorItem item in _items)
            {
                var quantized = QuantizeItemSize(item.SourceRect, item.Width, item.RoleKey);
                item.Width = quantized.width;
                item.Height = quantized.height;
                ApplyItemLayout(item);

                var transformed = CoordinateMath.TransformToContentAreaInt(
                    (item.SourceRect.X, item.SourceRect.Y, item.SourceRect.Width, item.SourceRect.Height),
                    _originalResolution,
                    HudConfig.CropDriftType(item.RoleKey));

                var clampedCrop = CoordinateMath.ClampContentCrop((Math.Max(2, transformed.w), Math.Max(2, transformed.h), transformed.x, transformed.y));
                int cropW = clampedCrop.w;
                int cropH = clampedCrop.h;
                Frac scale = quantized.scale;
                (int ox, int oy) = ClampOverlay(item.X, item.Y, item.Width, item.Height);

                crops[item.RoleKey] = new JsonArray(cropW, cropH, clampedCrop.x, clampedCrop.y);
                sourceCrops[item.RoleKey] = new JsonArray(
                    item.SourceRect.Width, item.SourceRect.Height, item.SourceRect.X, item.SourceRect.Y);
                scales[item.RoleKey] = scale.ToString();
                overlays[item.RoleKey] = new JsonObject
                {
                    ["x"] = ox,
                    ["y"] = oy
                };
                zOrders[item.RoleKey] = item.Z;
                RuntimeLog.Info("CROP", $"  Save item: {item.RoleKey} crop=[{cropW}x{cropH}+{clampedCrop.x}+{clampedCrop.y}] scale={scale} overlay=({ox},{oy}) z={item.Z}");
            }

            foreach (string deletedKey in _deletedRoleKeys)
            {
                if (_items.Any(i => i.RoleKey == deletedKey)) continue;
                if (crops[deletedKey] is null) continue;

                crops[deletedKey] = new JsonArray(0, 0, 0, 0);
                RuntimeLog.Info("CROP", $"  Save item: {deletedKey} removed (crop cleared to 0x0).");
            }

            RuntimeLog.Info("CROP", $"Saving {_items.Count} item(s) to config (schema v{CropConfigDefaults.SchemaVersion}).");
            config["schema_version"] = CropConfigDefaults.SchemaVersion;
            config["coordinate_space"] = CropConfigDefaults.CoordinateSpace;

            config = HudConfig.Sanitize(config);
            await store.SaveAsync(config);

            FortniteVideoSoftware.App.Infrastructure.MaskOverlayManager.SyncActiveProfileFromCurrentConfig();

            _dirty = false;
            RefreshActionButtons();
            RuntimeLog.Success("CROP", "Saved crop coordinates successfully.");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", ex);
            SetStatus("Save failed. See runtime log.");
            return false;
        }
    }

    private async Task ReturnToMainAppAsync()
    {
        try
        {
            var store = new StateTransferStore(_paths);
            await store.SendHandoffAsync(new FortniteVideoSoftware.Core.Ipc.HandoffPayload
            {
                SourceProcess = "CropTool",
                TargetProcess = "MainWindow",
                ReturnedFromCropTool = true,
                SelectedClipPath = _videoPath
            });
            await store.UpdatePropertiesAsync(new JsonObject
            {
                ["returned_from_crop_tool"] = true
            });
            _recovery.ReleaseLockOnly();
            RuntimeLog.Info("CROP", "Returning to Main app.");
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FortniteVideoSoftware.exe";

            // RELAUNCHARG_01 — "run-ui" is NOT decoration. This line used to start the exe with NO
            // arguments, and an argument-less launch is how this suite says "I am a standalone
            // installer": DeploymentFootprint.IsStandaloneInstallerHost is
            //     args.Length == 0 && !IsRunningFromInstallPath()
            // so Program.cs line 7 handed the new process to DeploymentLifecycle, which opened an
            // INSTALL LAUNCHER session and asked Windows for Administrator. Pressing FINISH & SAVE
            // in the Crop Tools raised a UAC prompt, and declining it killed the relaunch.
            //
            // It is invisible in an installed build, because IsRunningFromInstallPath() is true
            // there and the first condition never fires — it only bites in a dev build or a copy
            // run from anywhere else, which is exactly where it was found.
            //
            // The outbound direction always got this right: CompanionAppService starts the
            // companion with "--crop-tool". This is the return leg being made symmetrical.
            // Program.cs handles "run-ui" explicitly, and any non-empty argv settles the question.
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "run-ui") { UseShellExecute = false });
            if (p != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (int i = 0; i < 40; i++)
                        {
                            if (p.HasExited) break;
                            p.Refresh();
                            if (p.MainWindowHandle != IntPtr.Zero) break;
                            await Task.Delay(50);
                        }
                    }
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Close());
                });
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Close());
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", "Error returning to main window: " + ex.Message);
            Close();
        }
    }

    private void ResetWorkingState()
    {
        ClearSourceSelection();
        ClearMagicWandCandidates();

        foreach (CropEditorItem item in _items.ToList())
        {
            RemoveItem(item);
        }

        // DELETESET_01 - the tombstone list has to die with the working state.
        // _deletedRoleKeys is not a UI nicety: RehydrateSavedLayersAsync SKIPS any role in it, and
        // SaveConfigAsync writes crops[key] = [0,0,0,0] for every role in it. It was never cleared
        // here, and ResetWorkingState is what runs on a PROFILE SWITCH (see OnProfileChosenAsync),
        // so deleting "loot" while editing Fortnite and then switching to Battlefield meant
        // Battlefield's loot box was hidden on load and then ZEROED on the next save - a silent
        // cross-profile deletion of data the user never touched. Tombstones belong to the profile
        // that created them and must not outlive it.
        _deletedRoleKeys.Clear();

        SelectItem(null);
        RefreshLayerList();
        _dirty = false;
        InitializeHistory();
        RefreshActionButtons();
        SetStatus("Working crop items cleared.");
    }

    private void Undo()
    {
        if (_undoStack.Count <= 1)
        {
            return;
        }

        EditorSnapshot current = _undoStack.Pop();
        _redoStack.Push(current);
        RestoreSnapshot(_undoStack.Peek());
        _dirty = true;
        RefreshUndoRedoButtons();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        EditorSnapshot snapshot = _redoStack.Pop();
        _undoStack.Push(snapshot);
        RestoreSnapshot(snapshot);
        _dirty = true;
        RefreshUndoRedoButtons();
    }

    private void PushHistory()
    {
        PushHistory(CaptureSnapshot());
    }

    private void PushHistory(EditorSnapshot snapshot)
    {
        if (_restoringSnapshot)
        {
            return;
        }

        if (_undoStack.Count == 0 || !SnapshotsEqual(_undoStack.Peek(), snapshot))
        {
            _undoStack.Push(snapshot);
            _redoStack.Clear();
        }

        RefreshUndoRedoButtons();
    }

    private EditorSnapshot CaptureSnapshot()
    {
        return new EditorSnapshot(_items
            .OrderBy(i => i.RoleKey, StringComparer.Ordinal)
            .Select(i => new ItemSnapshot(i.RoleKey, i.DisplayName, i.SourceRect, i.CropImagePath, i.X, i.Y, i.Width, i.Height, i.Z))
            .ToList());
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        _restoringSnapshot = true;
        try
        {
            foreach (CropEditorItem item in _items.ToList())
            {
                RemoveItem(item);
            }

            foreach (ItemSnapshot itemSnapshot in snapshot.Items)
            {
                _items.Add(CreateItem(itemSnapshot));
                _deletedRoleKeys.Remove(itemSnapshot.RoleKey);
            }

            SelectItem(null);
            RefreshLayerList();
            MarkDirty(pushHistory: false);
        }
        finally
        {
            _restoringSnapshot = false;
        }
    }

    private static bool SnapshotsEqual(EditorSnapshot a, EditorSnapshot b)
    {
        if (a.Items.Count != b.Items.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Items.Count; i++)
        {
            ItemSnapshot x = a.Items[i];
            ItemSnapshot y = b.Items[i];
            if (x.RoleKey != y.RoleKey ||
                x.CropImagePath != y.CropImagePath ||
                !x.SourceRect.Equals(y.SourceRect) ||
                x.X != y.X ||
                x.Y != y.Y ||
                x.Width != y.Width ||
                x.Height != y.Height ||
                x.Z != y.Z)
            {
                return false;
            }
        }

        return true;
    }

    private void MarkDirty(bool pushHistory = false)
    {
        _dirty = true;
        if (pushHistory)
        {
            PushHistory();
        }
        RefreshActionButtons();
    }

    private void RefreshActionButtons()
    {
        // GATE_01 (F3): with no profile chosen there is no file to write to, so SAVE stays off
        // whatever the item state says. This is the second lock on the same door - SetProfileGate
        // disables the button too - because RefreshActionButtons is called from a dozen places and
        // any one of them re-enabling SAVE would re-open the overwrite hole.
        bool profileChosen = _activeProfile != null;

        SetEnabled("SaveButton", profileChosen && _items.Count > 0 && _dirty);

        // DELETEBTN_01: this line used to read SetEnabled("DeleteSelectedButton", ...). There has
        // never been a control by that name in CropToolWindow.axaml - the button is DeleteMenuButton
        // - and SetEnabled resolves through FindControl, which returns null and returns silently for
        // a name that does not exist. So the call did nothing, DELETE kept the IsEnabled="False" it
        // is declared with, and the only way to remove a layer was RESET (which wipes all of them).
        // It failed silently in exactly the way 04_UI_UX_AVALONIA_SPEC.md#UI-THEME describes for
        // the QualityLabel dead readout: the feature looked MISSING rather than broken.
        SetEnabled("DeleteMenuButton", profileChosen && _selectedItem != null);

        SetEnabled("RaiseButton", profileChosen && _selectedItem != null);
        SetEnabled("LowerButton", profileChosen && _selectedItem != null);
        RefreshUndoRedoButtons();
    }

    private void RefreshUndoRedoButtons()
    {
        SetEnabled("UndoButton", _undoStack.Count > 1);
        SetEnabled("RedoButton", _redoStack.Count > 0);
    }

    private async Task SeekInternal(double time)
    {
        if (_isSeeking)
        {
            _nextSeekTarget = time;
            return;
        }
        _isSeeking = true;
        try
        {
            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
            }
        }
        finally
        {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue)
            {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                _ = SeekInternal(target);
            }
        }
    }

    private void SeekTimelineFromPointer(PointerEventArgs e, Canvas timelineCanvas, Slider timelineSlider)
    {
        if (e.Handled) return;

        double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
        double width = timelineCanvas.Bounds.Width;
        if (duration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double sliderValue = (x / width) * 100.0;
        double targetTime = (sliderValue / 100.0) * duration;

        try
        {
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = sliderValue;
        }
        finally
        {
            _isTimerUpdatingSlider = false;
        }

        _ = SeekInternal(targetTime);
        ShowPlayheadBadge(targetTime, sliderValue);
        e.Handled = true;
    }

    private void ShowPlayheadBadge(double timeSeconds, double sliderValuePercentage)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("PlayheadBadge");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("PlayheadBadgeText");
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("CropTimelineMarkersCanvas");

        if (badge != null && text != null && canvas != null)
        {
            text.Text = FormatTime(timeSeconds * 1000.0);
            double canvasWidth = canvas.Bounds.Width;
            double x = (sliderValuePercentage / 100.0) * canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(badge, x - 25);
            badge.Opacity = 1.0;

            if (_playheadBadgeTimer == null)
            {
                _playheadBadgeTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _playheadBadgeTimer.Tick += (s, ev) =>
                {
                    _playheadBadgeTimer.Stop();
                    badge.Opacity = 0.0;
                };
            }
            _playheadBadgeTimer.Stop();
            _playheadBadgeTimer.Start();
        }
    }

    private void UpdateTimelineUi()
    {
        if (_timelineSlider == null)
        {
            return;
        }

        double currentMs = GetCurrentTimeMs();
        if (_durationMs <= 0)
        {
            _durationMs = Math.Max(0, (_videoHost?.IpcClient?.Duration ?? 0) * 1000.0);
        }

        _isTimerUpdatingSlider = true;
        try
        {
            if (_durationMs > 0)
            {
                double targetPercentage = (currentMs / _durationMs) * 100.0;
                if (Math.Abs(_timelineSlider.Value - targetPercentage) > 0.5 && !_isSeeking)
                {
                    _timelineSlider.Value = Math.Max(0, Math.Min(targetPercentage, 100.0));
                }
            }
        }
        finally
        {
            _isTimerUpdatingSlider = false;
        }

        if (_currentTimeLabel != null)
        {
            _currentTimeLabel.Text = FormatTime(currentMs);
        }
        if (_totalTimeLabel != null)
        {
            _totalTimeLabel.Text = FormatTime(_durationMs);
        }

        if (_videoHost?.IpcClient != null)
        {
            UpdatePlayPauseIcon(_videoHost.IpcClient.IsPaused);
        }

        DrawTimelineRuler();
    }

    /// <summary>
    /// TICKRULER_01 - the tick ruler, ported from the Main App (MainWindow.Canvas.cs,
    /// UpdateTimelineMarkers).
    ///
    /// This window already declared both canvases the Main App uses - CropTimelineScaleCanvas for
    /// the time labels and CropTimelineMarkersCanvas for the hairlines - and FindControls has
    /// always looked the first one up. NOTHING EVER DREW INTO EITHER. The result was a bare blue
    /// line with a dot on it: no sense of scale, no way to judge where 30 seconds is, which is
    /// what "naked/skinless" was describing.
    ///
    /// The interval ladder is copied exactly from the Main App so the two timelines read the same
    /// way: a mark every 5s on a short clip, widening to 10/30/60/300s as the clip gets longer, so
    /// the ruler never turns into a solid band of ink.
    ///
    /// Redrawn from the 100ms UI tick, so it is guarded three ways: nothing to draw without a
    /// duration, nothing to draw before layout has given the canvas a width, and - the one that
    /// matters - it returns immediately unless the width or the duration actually CHANGED. Without
    /// that guard this would rebuild several dozen visuals ten times a second forever.
    /// </summary>
    private double _rulerDrawnWidth = -1;
    private double _rulerDrawnDuration = -1;

    private void DrawTimelineRuler()
    {
        var markers = this.FindControl<Canvas>("CropTimelineMarkersCanvas");
        if (markers == null || _timelineCanvas == null) return;

        double duration = _durationMs / 1000.0;
        double width = markers.Bounds.Width;
        if (duration <= 0 || width <= 10) return;

        if (Math.Abs(width - _rulerDrawnWidth) < 0.5 && Math.Abs(duration - _rulerDrawnDuration) < 0.01)
        {
            return;
        }
        _rulerDrawnWidth = width;
        _rulerDrawnDuration = duration;

        // The playhead badge is a XAML child of the markers canvas, so clearing the canvas would
        // destroy it. Take it out first and put it back.
        var badge = this.FindControl<Border>("PlayheadBadge");
        if (badge != null) markers.Children.Remove(badge);
        markers.Children.Clear();
        if (badge != null) markers.Children.Add(badge);
        _timelineCanvas.Children.Clear();

        double interval = 5;
        if (duration > 3600) interval = 300;
        else if (duration > 1800) interval = 60;
        else if (duration > 300) interval = 30;
        else if (duration > 60) interval = 10;

        double height = Math.Max(1, markers.Bounds.Height);
        var tickBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        var labelBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));

        for (double t = 0; t <= duration; t += interval)
        {
            double x = (t / duration) * width;

            var tick = new Rectangle
            {
                Fill = tickBrush,
                Width = 1,
                Height = height,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tick, x);
            markers.Children.Add(tick);

            // The first and last labels are skipped: 0:00 and the duration are already printed by
            // the clocks either side of the slider, and drawing them again collides with those.
            if (t <= 0.001 || duration - t <= 0.001) continue;

            var label = new TextBlock
            {
                Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                Foreground = labelBrush,
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                IsHitTestVisible = false
            };
            // TIMELINESLIM_02 - CropTimelineScaleCanvas is an 11px overlay sharing the slider's cell
            // now, top-aligned, so the labels land in the empty band above the rail. SetTop(0) keeps
            // them inside it; anything larger would collide with the thumb.
            Canvas.SetLeft(label, Math.Max(0, Math.Min(width - 36, x + 2)));
            Canvas.SetTop(label, 0);
            _timelineCanvas.Children.Add(label);
        }
    }

    private double GetCurrentTimeMs()
    {
        if (_videoHost?.IpcClient?.CurrentTime > 0)
        {
            return _videoHost.IpcClient.CurrentTime * 1000.0;
        }
        if (_timelineSlider != null && _durationMs > 0)
        {
            return (_timelineSlider.Value / 100.0) * _durationMs;
        }

        return 0;
    }

    private (double x, double y) SnapPosition(CropEditorItem item, double x, double y, double width, double height)
    {
        ClearGuides();
        if (this.FindControl<CheckBox>("SnapToggle")?.IsChecked != true)
        {
            return (x, y);
        }

        var xTargets = new List<(double value, string label)>
        {
            (0, "Canvas Left"),
            (CoordinateMath.ScaleRound(Frac.FromDouble(PortraitWidth / 2.0)), "Canvas Center"),
            (PortraitWidth, "Canvas Right")
        };
        var yTargets = new List<(double value, string label)>
        {
            (ContentTop, "Content Top"),
            (CoordinateMath.ScaleRound(Frac.FromDouble(PortraitHeight / 2.0)), "Canvas Center"),
            (ContentBottom, "Content Bottom")
        };

        foreach (CropEditorItem other in _items.Where(i => !ReferenceEquals(i, item)))
        {
            xTargets.Add((other.X, other.DisplayName + " Left"));
            xTargets.Add((other.X + CoordinateMath.ScaleRound(Frac.FromDouble(other.Width / 2.0)), other.DisplayName + " Center"));
            xTargets.Add((other.X + other.Width, other.DisplayName + " Right"));

            yTargets.Add((other.Y, other.DisplayName + " Top"));
            yTargets.Add((other.Y + CoordinateMath.ScaleRound(Frac.FromDouble(other.Height / 2.0)), other.DisplayName + " Center"));
            yTargets.Add((other.Y + other.Height, other.DisplayName + " Bottom"));
        }

        (double snappedX, double? guideX) = SnapAxis(x, width, xTargets);
        (double snappedY, double? guideY) = SnapAxis(y, height, yTargets);

        if (guideX.HasValue)
        {
            DrawGuide(vertical: true, guideX.Value);
        }
        if (guideY.HasValue)
        {
            DrawGuide(vertical: false, guideY.Value);
        }

        return (snappedX, snappedY);
    }

    private static (double pos, double? guide) SnapAxis(double pos, double size, List<(double value, string label)> targets)
    {
        double start = pos;
        double halfSize = CoordinateMath.ScaleRound(Frac.FromDouble(size / 2.0));
        double center = pos + halfSize;
        double end = pos + size;
        double bestDistance = SnapThreshold + 1;
        double bestPos = pos;
        double? bestGuide = null;

        foreach ((double target, _) in targets)
        {
            Check(start, target, target);
            Check(center, target, target - halfSize);
            Check(end, target, target - size);
        }

        return (bestPos, bestGuide);

        void Check(double current, double guide, double candidatePos)
        {
            double distance = Math.Abs(current - guide);
            if (distance < bestDistance && distance <= SnapThreshold)
            {
                bestDistance = distance;
                bestPos = candidatePos;
                bestGuide = guide;
            }
        }
    }

    private void DrawGuide(bool vertical, double value)
    {
        if (_portraitCanvas == null)
        {
            return;
        }

        var line = new Line
        {
            StartPoint = vertical ? new Point(value, 0) : new Point(0, value),
            EndPoint = vertical ? new Point(value, PortraitHeight) : new Point(PortraitWidth, value),
            Stroke = new SolidColorBrush(Color.Parse("#7dd3fc")),
            StrokeThickness = 2,
            IsHitTestVisible = false,
            ZIndex = 1200
        };

        _portraitCanvas.Children.Add(line);
        _guideControls.Add(line);
    }

    private void ClearGuides()
    {
        if (_portraitCanvas == null)
        {
            _guideControls.Clear();
            return;
        }

        foreach (Control control in _guideControls)
        {
            _portraitCanvas.Children.Remove(control);
        }
        _guideControls.Clear();
    }

    private (int x, int y) ClampOverlay(double x, double y, double width, double height)
    {
        return CoordinateMath.ClampOverlayPosition(x, y, width, height);
    }

    private Point ClampToSnapshot(Point point)
    {
        return new Point(
            Math.Max(0, Math.Min(point.X, _snapshotWidth)),
            Math.Max(0, Math.Min(point.Y, _snapshotHeight)));
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    private SourceRect ToSourceRect(Rect rect)
    {
        int x = CoordinateMath.ScaleRound(Frac.FromDouble(rect.X));
        int y = CoordinateMath.ScaleRound(Frac.FromDouble(rect.Y));
        int right = CoordinateMath.ScaleRound(Frac.FromDouble(rect.Right));
        int bottom = CoordinateMath.ScaleRound(Frac.FromDouble(rect.Bottom));
        return ClampSourceRect(new SourceRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y)));
    }

    private SourceRect ClampSourceRect(SourceRect rect)
    {
        return ClampSourceRect(rect, _snapshotWidth, _snapshotHeight);
    }

    private static SourceRect ClampSourceRect(SourceRect rect, int width, int height)
    {
        int x = Math.Max(0, Math.Min(rect.X, Math.Max(0, width - 1)));
        int y = Math.Max(0, Math.Min(rect.Y, Math.Max(0, height - 1)));
        int w = Math.Max(1, Math.Min(rect.Width, width - x));
        int h = Math.Max(1, Math.Min(rect.Height, height - y));
        return new SourceRect(x, y, w, h);
    }

    private HudRole SuggestRole(SourceRect rect)
    {
        double cx = rect.X + rect.Width / 2.0;
        double cy = rect.Y + rect.Height / 2.0;
        bool right = cx > _snapshotWidth / 2.0;
        bool bottom = cy > _snapshotHeight / 2.0;

        return new HudRole("custom_element", "Custom Element", 50, -1, -1);
    }

    private static JsonObject EnsureObject(JsonObject config, string section)
    {
        if (config[section] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        config[section] = obj;
        return obj;
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        try
        {
            return node?.GetValue<int>() ?? fallback;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("CROP", $"JSON int parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    private static Frac ReadFrac(JsonNode? node, Frac fallback)
    {
        try
        {
            if (node == null) return fallback;
            if (node.AsValue().TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                return Frac.FromString(s);
            if (node.AsValue().TryGetValue(out double d))
                return Frac.FromDouble(d);
            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static double ReadDouble(JsonNode? node, double fallback)
    {
        try
        {
            return node?.GetValue<double>() ?? fallback;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("CROP", $"JSON double parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    private static string FormatTime(double millis)
    {
        if (!double.IsFinite(millis) || millis < 0)
        {
            millis = 0;
        }

        TimeSpan ts = TimeSpan.FromMilliseconds(millis);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The internal wizard states, which are NOT the same thing as the header dots:
    ///   0 Choose Profile   1 Upload Video   2 Find HUD Frame   3 Refine Box   4 Portrait Composer
    /// <see cref="UpdateStepDots"/> owns the mapping onto the four header dots. Step 0 is new with
    /// GATE_01 (F3) - before the gate existed the window opened straight on step 1 because it had
    /// already silently picked a profile for the user.
    /// </summary>
    private void SetWizardState(int step, string goal, string status)
    {
        if (_goalLabel != null)
        {
            _goalLabel.Text = step <= 0 ? goal : $"Step {step}: {goal}";
        }
        UpdateStepDots(step);
        SetStatus(status);
    }

    /// <summary>
    /// ISSUE_03: drives the wizard tracker in the header. Nothing used to call into
    /// Step1Dot/Step2Dot/Step3Dot at all, so the indicator sat frozen on "step 1" for the whole
    /// session while <see cref="SetWizardState"/> silently tracked the real progress next to it.
    ///
    /// F6: the header carries FOUR dots now - Profile / Upload / Crop / Save - because GATE_01
    /// put a real user-facing stage in front of Upload. The internal wizard still has five states,
    /// so steps 2 and 3 (Find HUD Frame / Refine Box) both map onto the single "Crop" dot:
    ///
    ///   step 0        -> dot 0  Profile
    ///   step 1        -> dot 1  Upload
    ///   steps 2 and 3 -> dot 2  Crop
    ///   step 4        -> dot 3  Save
    ///
    /// Keep this table and the AXAML dot count in step; a fifth dot with no mapping would simply
    /// never light up, which is the failure the three-dot version shipped with.
    /// </summary>
    private void UpdateStepDots(int step)
    {
        int stage = step <= 0 ? 0
                  : step == 1 ? 1
                  : step >= 4 ? 3
                  : 2;

        var dots = new[]
        {
            (Dot: this.FindControl<Border>("Step1Dot"),
             Icon: this.FindControl<TextBlock>("Step1Icon"),
             Label: this.FindControl<TextBlock>("Step1Label"), Numeral: "1"),
            (Dot: this.FindControl<Border>("Step2Dot"),
             Icon: this.FindControl<TextBlock>("Step2Icon"),
             Label: this.FindControl<TextBlock>("Step2Label"), Numeral: "2"),
            (Dot: this.FindControl<Border>("Step3Dot"),
             Icon: this.FindControl<TextBlock>("Step3Icon"),
             Label: this.FindControl<TextBlock>("Step3Label"), Numeral: "3"),
            (Dot: this.FindControl<Border>("Step4Dot"),
             Icon: this.FindControl<TextBlock>("Step4Icon"),
             Label: this.FindControl<TextBlock>("Step4Label"), Numeral: "4"),
        };

        for (int i = 0; i < dots.Length; i++)
        {
            var (dot, icon, label, numeral) = dots[i];
            if (dot == null || icon == null || label == null) continue;

            string dotClass = i < stage ? "WizDotDone" : i == stage ? "WizDotActive" : "WizDotPending";
            string iconClass = i <= stage ? "WizIconOn" : "WizIconPending";
            string labelClass = i < stage ? "WizLabelDone" : i == stage ? "WizLabelActive" : "WizLabelPending";

            SwapClass(dot, dotClass, "WizDotPending", "WizDotActive", "WizDotDone");
            SwapClass(icon, iconClass, "WizIconPending", "WizIconOn");
            SwapClass(label, labelClass, "WizLabelPending", "WizLabelActive", "WizLabelDone");

            icon.Text = i < stage ? "\u2713" : numeral;
        }
    }

    /// <summary>Applies exactly one of a mutually exclusive class group to a control.</summary>
    private static void SwapClass(StyledElement target, string keep, params string[] group)
    {
        foreach (var c in group)
        {
            if (c == keep) continue;
            target.Classes.Remove(c);
        }
        if (!target.Classes.Contains(keep)) target.Classes.Add(keep);
    }

    /// <summary>
    /// ISSUE_09 — writes the persistent status line AND floats the suite-wide notice.
    ///
    /// The line is deliberately kept: it holds the last message on screen indefinitely, which is
    /// what you want while you are reading a rejection ("Selection is too small") and deciding what
    /// to do. What it could not do is CATCH THE EYE — a user watching the canvas never noticed a
    /// sentence changing at the bottom of the window, which is why this screen felt unresponsive.
    /// The notice supplies the attention; the line supplies the memory.
    ///
    /// Every call site in this window is a discrete event, so all of them float. FloatingNotice's
    /// own dedupe absorbs the repeats from <see cref="SetWizardState"/>.
    /// </summary>
    private void SetStatus(string text)
    {
        if (_statusLabel != null)
        {
            _statusLabel.Text = text;
        }
        Controls.FloatingNotice.Show(this, text);
    }

    /// <summary>ISSUE_09 — same, in the "that worked" colour.</summary>
    private void SetStatusSuccess(string text)
    {
        if (_statusLabel != null)
        {
            _statusLabel.Text = text;
        }
        Controls.FloatingNotice.Success(this, text);
    }

    private void SetEnabled(string name, bool enabled)
    {
        if (this.FindControl<Control>(name) is { } control)
        {
            control.IsEnabled = enabled;
        }
    }

    private void SetVisible(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control)
        {
            control.IsVisible = visible;
        }
    }

    private void SetContent(string name, object content)
    {
        if (this.FindControl<Button>(name) is { } button)
        {
            button.Content = content;
        }
    }

    private static string ResolveBinaryPath(string fileName, string preferredSubdirectory)
    {
        string processDir = IOPath.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = IOPath.GetFullPath(IOPath.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));

        string[] candidates =
        [
            IOPath.Combine(processDir, preferredSubdirectory, fileName),
            IOPath.Combine(processDir, "backend", fileName),
            IOPath.Combine(processDir, "frontend", fileName),
            IOPath.Combine(processDir, fileName),
            sourceRootCandidate,
            IOPath.Combine(Environment.CurrentDirectory, "binaries", fileName),
            IOPath.Combine(Environment.CurrentDirectory, preferredSubdirectory, fileName),
            fileName
        ];

        return candidates.FirstOrDefault(File.Exists) ?? fileName;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // KEYFOCUS_01 — while a text input owns focus (the Element Name box, the new overlay
        // profile name, …) the keyboard belongs to it: no delete, no undo, no arrow-nudging
        // while typing. Return without touching e.Handled so the control keeps the key.
        if (FortniteVideoSoftware.App.Infrastructure.KeyboardFocusPolicy.HotkeysSuspended(TopLevel.GetTopLevel(this)))
        {
            base.OnKeyDown(e);
            return;
        }

        // ROLEPOPUP_01 - Enter opens the chooser for the current selection. This is the keyboard
        // path that replaces the deleted ADD SELECTION button, and it is what makes an arrow-key
        // refinement finishable without touching the mouse again.
        if (e.Key is Key.Enter or Key.Return
            && _sourceSelection != null
            && this.FindControl<Grid>("SnapshotPanel")?.IsVisible == true)
        {
            ShowRolePopup();
            e.Handled = true;
            return;
        }

        // CANCELSEL_01 - Escape backs out one level at a time: first the "+ New element" row if it
        // is open, then the selection itself. Two Escapes to go from typing a name to a clean frame,
        // and never more than one step per press, so a reflex double-tap cannot throw away more than
        // the user meant.
        if (e.Key == Key.Escape && _rolePopupNewOpen)
        {
            CloseRolePopupNewRow();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape
            && _sourceSelection != null
            && this.FindControl<Grid>("SnapshotPanel")?.IsVisible == true)
        {
            CancelSourceSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            DeleteSelectedItem();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control)) ||
            (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Undo();
            e.Handled = true;
            return;
        }

        // CROPCANVAS_01 — the frozen frame gets the arrows FIRST, and only while it is the panel
        // on screen with a live selection. Ordering matters: both panels want the arrow keys, and
        // whichever one the user is actually looking at must win. SnapshotPanel is only visible
        // during the draw/refine step, so this cannot steal nudges from the portrait composer.
        if (_sourceSelection != null
            && this.FindControl<Grid>("SnapshotPanel")?.IsVisible == true
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (NudgeSourceSelection(e.Key, e.KeyModifiers))
            {
                // ROLEPOPUP_01: a nudge does NOT re-open the chooser. Arrow keys are for the last
                // two or three pixels, and a menu flying up on every keypress would make that
                // unusable. The popup is hidden while nudging and Enter brings it back.
                HideRolePopup();
                e.Handled = true;
                return;
            }
        }

        if (_selectedItem != null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                double resizeStep = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 2;
                double delta = e.Key is Key.Left or Key.Up ? -resizeStep : resizeStep;
                ResizeFromBottomRight(_selectedItem, delta);
                ApplyItemLayout(_selectedItem);
                MarkDirty();
                PushHistory();
                e.Handled = true;
                return;
            }

            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            (_selectedItem.X, _selectedItem.Y) = ClampOverlay(_selectedItem.X + dx, _selectedItem.Y + dy, _selectedItem.Width, _selectedItem.Height);
            ApplyItemLayout(_selectedItem);
            MarkDirty();
            PushHistory();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        try { _playheadBadgeTimer?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _timelineTimer?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "CropToolBounds");
        Hide();

        try
        {
            _timelineTimer?.Stop();
            _timelineTimer = null;

            if (_playheadBadgeTimer != null)
            {
                _playheadBadgeTimer.Stop();
                _playheadBadgeTimer = null;
            }

            if (_videoHost != null)
            {
                if (_videoHost.IpcClient != null)
                {
                    await _videoHost.IpcClient.SendCommandAsync("stop");
                }
                _videoHost.Dispose();
                _videoHost = null;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Error during close: {ex.Message}");
        }
        finally
        {
            CleanupTempFiles();
            _isSafeToClose = true;
            Close();
        }
    }

    private void CleanupTempFiles()
    {
        foreach (string path in _tempFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("CROP", $"Temp file cleanup skipped: {ex.Message}");
            }
        }
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == Avalonia.Controls.WindowState.Maximized 
                    ? Avalonia.Controls.WindowState.Normal 
                    : Avalonia.Controls.WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            };
        }
    }

    private sealed class HudRole
    {
        public HudRole(string key, string displayName, int defaultZ, double defaultX, double defaultY)
        {
            Key = key;
            DisplayName = displayName;
            DefaultZ = defaultZ;
            DefaultX = defaultX;
            DefaultY = defaultY;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public int DefaultZ { get; }
        public double DefaultX { get; }
        public double DefaultY { get; }

        public override string ToString() => DisplayName;
    }

    private sealed record LayerEntry(string RoleKey, string DisplayName, int Z)
    {
        public override string ToString() => $"{DisplayName}  z:{Z}";
    }

    private readonly record struct SourceRect(int X, int Y, int Width, int Height);

    private sealed record CandidateSpec(string RoleKey, SourceRect Rect);

    private sealed record ItemSnapshot(
        string RoleKey,
        string DisplayName,
        SourceRect SourceRect,
        string CropImagePath,
        int X,
        int Y,
        int Width,
        int Height,
        int Z);

    private sealed record EditorSnapshot(List<ItemSnapshot> Items);

    private sealed class CropEditorItem
    {
        public required string RoleKey { get; init; }
        public required string DisplayName { get; init; }
        public required SourceRect SourceRect { get; set; }
        /// <summary>IDEA_1: settable so RefreshRehydratedThumbnailsAsync can fill in the picture for
        /// a layer that was reopened for editing before any video had been loaded.</summary>
        public required string CropImagePath { get; set; }
        public required int X { get; set; }
        public required int Y { get; set; }
        public required int Width { get; set; }
        public required int Height { get; set; }
        public required int Z { get; set; }
        public required Canvas Root { get; init; }
        public required Image Image { get; init; }
        public required Rectangle Border { get; init; }
        public required Rectangle TopLeftHandle { get; init; }
        public required Rectangle BottomRightHandle { get; init; }
        public required Border LabelHost { get; init; }
        public required TextBlock LabelText { get; init; }

        /// <summary>
        /// GHOSTKILL_01 - true when this element was loaded from the profile rather than drawn in
        /// this session. The only thing it changes is whether "Show Saved Crops" and the opacity
        /// slider apply to it; it is otherwise a completely ordinary, fully editable item. It is
        /// deliberately NOT part of ItemSnapshot: undo/redo restores geometry, and where an element
        /// originally came from is not geometry - round-tripping it through a snapshot would let an
        /// undo quietly re-flag a saved element as new, or the reverse.
        /// </summary>
        public bool FromSavedConfig { get; set; }
    }

    private enum ComposerEditMode
    {
        None,
        Drag,
        ResizeTopLeft,
        ResizeBottomRight
    }

    private enum ResizeHandle
    {
        TopLeft,
        BottomRight
    
}
}

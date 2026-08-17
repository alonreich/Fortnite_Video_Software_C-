using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using FortniteVideoSoftware.App.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls;

public partial class SettingsWindow : Window
{
    private Dictionary<string, Key> _pendingKeys = new();
    private Dictionary<string, Button> _keyButtons = new();
    private string? _waitingForKeyAction;
    private bool _isSafeToClose = false;

    private DefaultValues _pendingDefaults = new();
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private string _pendingMusicFolder = "";

    private string _pendingMaskOverlay = "";
    /// <summary>G03 — pending value of <see cref="AppSettings.VideoEncoderOverride"/>.</summary>
    private string _pendingVideoEncoder = "Auto";
    private ThemeMode _pendingThemeMode = ThemeMode.FollowOS;
    private FontScale _pendingFontScale = FontScale.Normal;
    private AudioFixPrompt _pendingLoudnessPrompt = AudioFixPrompt.Ask;
    private AudioFixPrompt _pendingPeakPrompt = AudioFixPrompt.Ask;

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = this;
        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "SettingsBounds");

        _pendingDefaults = new DefaultValues
        {
            DefaultSpeed = SettingsManager.Instance.Defaults.DefaultSpeed,
            SpeedBehavior = SettingsManager.Instance.Defaults.SpeedBehavior,
            PortraitMode = SettingsManager.Instance.Defaults.PortraitMode,
            PortraitBehavior = SettingsManager.Instance.Defaults.PortraitBehavior,
            BossHp = SettingsManager.Instance.Defaults.BossHp,
            BossHpBehavior = SettingsManager.Instance.Defaults.BossHpBehavior,
            ShowTeammates = SettingsManager.Instance.Defaults.ShowTeammates,
            ShowTeammatesBehavior = SettingsManager.Instance.Defaults.ShowTeammatesBehavior,
            EnableFade = SettingsManager.Instance.Defaults.EnableFade,
            EnableFadeBehavior = SettingsManager.Instance.Defaults.EnableFadeBehavior,
            QualityIndex = SettingsManager.Instance.Defaults.QualityIndex,
            QualityBehavior = SettingsManager.Instance.Defaults.QualityBehavior,
            AutoVoiceNormalization = SettingsManager.Instance.Defaults.AutoVoiceNormalization,
            AutoSpikeFlattening = SettingsManager.Instance.Defaults.AutoSpikeFlattening,
            AudioProtection = SettingsManager.Instance.Defaults.AudioProtection,   // AUDIO_09
            RememberMusicVolumes = SettingsManager.Instance.Defaults.RememberMusicVolumes,
            DefaultZoomSlow = SettingsManager.Instance.Defaults.DefaultZoomSlow,
            DefaultFreezeDurationS = SettingsManager.Instance.Defaults.DefaultFreezeDurationS
        };

        _pendingThemeMode = SettingsManager.Instance.ThemeMode;
        _pendingFontScale = SettingsManager.Instance.FontScale;
        _pendingLoudnessPrompt = SettingsManager.Instance.LoudnessNormalizationPrompt;
        _pendingPeakPrompt = SettingsManager.Instance.PeakFlatteningPrompt;

        LoadCurrentKeybinds();
        BuildKeyBindsUi();
        BuildDefaultsUi();
        BuildMaskOverlayUi();
        BuildVideoEncoderUi();
        BuildAppearanceUi();
        BuildMemeFolderUi();

        ConfirmVideoMergerRemove = SettingsManager.Instance.ConfirmVideoMergerRemove;
        ConfirmVideoMergerClearAll = SettingsManager.Instance.ConfirmVideoMergerClearAll;
        ConfirmCropToolReset = SettingsManager.Instance.ConfirmCropToolReset;
        ConfirmCropToolDelete = SettingsManager.Instance.ConfirmCropToolDelete;

        // AUDIO_06
        UiSoundsEnabled = SettingsManager.Instance.UiSoundsEnabled;
        UiSoundVolume = SettingsManager.Instance.UiSoundVolume;
        BuildUiSoundUi();

        this.FindControl<Button>("SaveBtn")!.Click += (s, e) => SaveAndClose();
        this.FindControl<Button>("CancelBtn")!.Click += (s, e) => Close();

        this.KeyDown += OnWindowKeyDown;
        AttachTitleBarDrag();
    }

    public bool RememberMusicVolumes
    {
        get => _pendingDefaults.RememberMusicVolumes;
        set => _pendingDefaults.RememberMusicVolumes = value;
    }
    public bool AutoVoiceNormalization
    {
        get => _pendingDefaults.AutoVoiceNormalization;
        set => _pendingDefaults.AutoVoiceNormalization = value;
    }
    /// <summary>AUDIO_09 — master switch for sidechain ducking AND EQ carving.</summary>
    public bool AudioProtection
    {
        get => _pendingDefaults.AudioProtection;
        set => _pendingDefaults.AudioProtection = value;
    }

    public bool AutoSpikeFlattening
    {
        get => _pendingDefaults.AutoSpikeFlattening;
        set => _pendingDefaults.AutoSpikeFlattening = value;
    }
    public string DefaultMusicFolder
    {
        get => _pendingMusicFolder;
        set {
            if (!string.IsNullOrWhiteSpace(value) && !System.IO.Directory.Exists(value))
                throw new ArgumentException("Folder does not exist.");
            _pendingMusicFolder = value ?? "";
        }
    }

    public bool ConfirmVideoMergerRemove { get; set; }
    public bool ConfirmVideoMergerClearAll { get; set; }
    public bool ConfirmCropToolReset { get; set; }
    public bool ConfirmCropToolDelete { get; set; }

    /// <summary>AUDIO_06 — pending value for the UI sound master switch; committed by APPLY.</summary>
    public bool UiSoundsEnabled { get; set; } = true;

    /// <summary>AUDIO_06 — pending value for the UI sound level (0-100); committed by APPLY.</summary>
    public int UiSoundVolume { get; set; } = 70;

    /// <summary>
    /// AUDIO_06: keeps the "NN%" readout beside the slider in step with the thumb. The slider's
    /// VALUE is bound TwoWay in XAML like every other setting on this window; this only drives
    /// the label, which has no binding source of its own.
    /// </summary>
    private void BuildUiSoundUi()
    {
        var slider = this.FindControl<Slider>("UiSoundVolumeSlider");
        var label = this.FindControl<TextBlock>("UiSoundVolumeLabel");
        if (slider == null || label == null) return;

        void Render() => label.Text = $"{(int)Math.Round(slider.Value)}%";

        Render();
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) Render();
        };
    }

    private void LoadCurrentKeybinds()
    {
        var kb = SettingsManager.Instance.KeyBinds;
        _pendingKeys["PlayPause"] = kb.PlayPause;
        _pendingKeys["MarkStart"] = kb.MarkStart;
        _pendingKeys["MarkEnd"] = kb.MarkEnd;
        _pendingKeys["SeekForward"] = kb.SeekForward;
        _pendingKeys["SeekBackward"] = kb.SeekBackward;
        _pendingKeys["VolumeUp"] = kb.VolumeUp;
        _pendingKeys["VolumeDown"] = kb.VolumeDown;
        _pendingKeys["FineSeekForward"] = kb.FineSeekForward;
        _pendingKeys["FineSeekBackward"] = kb.FineSeekBackward;
        _pendingKeys["AggressiveVolumeUp"] = kb.AggressiveVolumeUp;
        _pendingKeys["AggressiveVolumeDown"] = kb.AggressiveVolumeDown;
    }

    private void BuildAppearanceUi()
    {
        var fontCombo = this.FindControl<ComboBox>("FontScaleComboBox");
        if (fontCombo != null)
        {
            fontCombo.ItemsSource = new List<string>
            {
                "Extra Small",
                "Small",
                "Medium",
                "Normal",
                "Large",
                "Extra Large"
            };
            fontCombo.SelectedIndex = (int)_pendingFontScale;

            fontCombo.SelectionChanged += (s, e) =>
            {
                _pendingFontScale = (FontScale)fontCombo.SelectedIndex;
                ThemeManager.ApplyFontScale(_pendingFontScale);
            };
        }

        var themeCombo = this.FindControl<ComboBox>("ThemeComboBox");
        if (themeCombo != null)
        {
            themeCombo.ItemsSource = new List<string>
            {
                "Follow OS Theme",
                "Dark Theme",
                "Light Theme"
            };
            themeCombo.SelectedIndex = (int)_pendingThemeMode;

            themeCombo.SelectionChanged += (s, e) =>
            {
                _pendingThemeMode = (ThemeMode)themeCombo.SelectedIndex;
                ThemeManager.ApplyTheme(_pendingThemeMode);
            };
        }

        BuildAudioWarningUi();
    }

    /// <summary>
    /// Settings → Audio. Mirrors whatever the user chose in the upload warning dialogs, so a
    /// "never ask me again" is never a one-way door — this is the screen the dialog points at.
    ///
    /// The option order matches <see cref="AudioFixPrompt"/> exactly (Ask / AlwaysApply /
    /// NeverApply), so the combo index IS the enum value. Keep them in step if either changes.
    /// </summary>
    private void BuildAudioWarningUi()
    {
        var loudness = this.FindControl<ComboBox>("LoudnessPromptComboBox");
        if (loudness != null)
        {
            loudness.ItemsSource = new List<string>
            {
                "Ask me each time",
                "Always even out the volume (do not ask)",
                "Never change my volume (do not ask)"
            };
            loudness.SelectedIndex = (int)_pendingLoudnessPrompt;
            loudness.SelectionChanged += (_, _) =>
            {
                if (loudness.SelectedIndex >= 0)
                    _pendingLoudnessPrompt = (AudioFixPrompt)loudness.SelectedIndex;
            };
        }

        var peaks = this.FindControl<ComboBox>("PeakPromptComboBox");
        if (peaks != null)
        {
            peaks.ItemsSource = new List<string>
            {
                "Ask me each time",
                "Always soften sudden loud moments (do not ask)",
                "Never soften them (do not ask)"
            };
            peaks.SelectedIndex = (int)_pendingPeakPrompt;
            peaks.SelectionChanged += (_, _) =>
            {
                if (peaks.SelectedIndex >= 0)
                    _pendingPeakPrompt = (AudioFixPrompt)peaks.SelectedIndex;
            };
        }
    }

    private void BuildKeyBindsUi()
    {
        var panel = this.FindControl<StackPanel>("KeyBindsPanel");
        if (panel == null) return;

        var labels = new Dictionary<string, string>
        {
            { "PlayPause", "Play / Pause Video" },
            { "MarkStart", "Mark Start of Clip" },
            { "MarkEnd", "Mark End of Clip" },
            { "SeekForward", "Seek Forward" },
            { "SeekBackward", "Seek Backward" },
            { "FineSeekForward", "Fine Seek Forward (Ctrl + ...)" },
            { "FineSeekBackward", "Fine Seek Backward (Ctrl + ...)" },
            { "VolumeUp", "Volume Up" },
            { "VolumeDown", "Volume Down" },
            { "AggressiveVolumeUp", "Aggressive Volume Up (Ctrl + ...)" },
            { "AggressiveVolumeDown", "Aggressive Volume Down (Ctrl + ...)" }
        };

        foreach (var kvp in labels)
        {
            var actionId = kvp.Key;
            var description = kvp.Value;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 120") };
            
            var text = new TextBlock 
            { 
                Text = description, 
                Foreground = this.TryFindResource("AppTextPrimaryBrush", out var brush) ? brush as IBrush : Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            var btn = new Button 
            { 
                Content = _pendingKeys[actionId].ToString(),
                Width = 120,
                Classes = { "Primary" }
            };
            
            btn.Click += (s, e) => 
            {
                if (_waitingForKeyAction != null)
                {
                    _keyButtons[_waitingForKeyAction].Content = _pendingKeys[_waitingForKeyAction].ToString();
                }
                _waitingForKeyAction = actionId;
                btn.Content = "Press any key...";
            };

            _keyButtons[actionId] = btn;

            Grid.SetColumn(text, 0);
            Grid.SetColumn(btn, 1);
            grid.Children.Add(text);
            grid.Children.Add(btn);

            panel.Children.Add(grid);
        }

        var resetBtn = this.FindControl<Button>("ResetKeyBindsBtn");
        if (resetBtn != null)
        {
            resetBtn.Click += (s, e) =>
            {
                ResetPendingKeyBinds();
            };
        }
    }

    private void ResetPendingKeyBinds()
    {
        var def = new KeyBinds();
        foreach (var kvp in _pendingKeys.Keys.ToList())
        {
            var prop = typeof(KeyBinds).GetProperty(kvp);
            if (prop != null)
            {
                _pendingKeys[kvp] = (Key)prop.GetValue(def)!;
                if (_keyButtons.TryGetValue(kvp, out var btnRef))
                {
                    btnRef.Content = _pendingKeys[kvp].ToString();
                }
            }
        }
    }

    /// <summary>
    /// G03 — Performance tab. Maps the friendly labels shown to the user onto the exact strings
    /// <c>EncoderManager.HardwareByStrategy</c> understands. Do not change the right-hand values.
    /// </summary>
    private static readonly (string Label, string Value)[] VideoEncoderChoices =
    [
        ("Automatic (recommended)", "Auto"),
        ("NVIDIA graphics card (NVENC)", "NVIDIA"),
        ("AMD graphics card (AMF)", "AMD"),
        ("Intel built-in graphics (QuickSync)", "INTEL"),
        ("Processor only — slowest (CPU)", "CPU"),
    ];

    private void BuildVideoEncoderUi()
    {
        _pendingVideoEncoder = string.IsNullOrWhiteSpace(SettingsManager.Instance.VideoEncoderOverride)
            ? "Auto"
            : SettingsManager.Instance.VideoEncoderOverride;

        var combo = this.FindControl<ComboBox>("VideoEncoderComboBox");
        var status = this.FindControl<TextBlock>("VideoEncoderStatusText");
        if (combo == null) return;

        var labels = VideoEncoderChoices.Select(c => c.Label).ToList();
        combo.ItemsSource = labels;

        int idx = Array.FindIndex(VideoEncoderChoices, c =>
            c.Value.Equals(_pendingVideoEncoder, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = idx >= 0 ? idx : 0;

        combo.SelectionChanged += (_, _) =>
        {
            int i = combo.SelectedIndex;
            if (i >= 0 && i < VideoEncoderChoices.Length)
                _pendingVideoEncoder = VideoEncoderChoices[i].Value;
        };

        // Show what FFmpeg can ACTUALLY do on this machine, probed live — so the user is never
        // guessing, and so a broken boot scan is immediately visible here.
        if (status != null)
        {
            _ = Task.Run(() =>
            {
                string text;
                try
                {
                    var mgr = new EncoderManager(null, null);
                    text = mgr.AvailableEncoders.Count == 0
                        ? "Detected on this computer: no graphics-card encoder found."
                        : "Detected on this computer: " + string.Join(", ", mgr.AvailableEncoders
                            .Select(e => e switch
                            {
                                "h264_nvenc" => "NVIDIA",
                                "h264_amf" => "AMD",
                                "h264_qsv" => "Intel",
                                _ => e
                            }));
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.ToString());
                    text = "Could not check this computer's graphics encoders.";
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = text);
            });
        }
    }

    private void BuildMaskOverlayUi()
    {
        _pendingMaskOverlay = SettingsManager.Instance.ActiveMaskOverlay;
        
        var combo = this.FindControl<ComboBox>("MaskOverlayProfileComboBox");
        if (combo != null)
        {
            var profiles = MaskOverlayManager.GetAvailableProfiles();
            combo.ItemsSource = profiles;
            combo.SelectedItem = _pendingMaskOverlay;

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is string selected)
                {
                    _pendingMaskOverlay = selected;
                }
            };
        }
    }

    private void BuildDefaultsUi()
    {
        var panel = this.FindControl<StackPanel>("DefaultsPanel");
        if (panel == null) return;

        var speedNum = new NumericUpDown
        {
            Minimum = 0.1m, Maximum = 4.0m, Increment = 0.1m,
            Value = (decimal)_pendingDefaults.DefaultSpeed,
            FormatString = "0.0",
            Width = 100, HorizontalAlignment = HorizontalAlignment.Right
        };
        speedNum.ValueChanged += (_, _) => _pendingDefaults.DefaultSpeed = (double)(speedNum.Value ?? (decimal)1.1);
        panel.Children.Add(MakeValueBehaviorRow("Default Speed", _pendingDefaults.SpeedBehavior, v => _pendingDefaults.SpeedBehavior = v, speedNum));

        var qCombo = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Right };
        var qItems = new List<string>();
        for (int i = 0; i < 20; i++) qItems.Add($"{5 + i * 5}MB");
        qItems.Add("ORIGINAL QUALITY");
        qCombo.ItemsSource = qItems;
        qCombo.SelectedIndex = Math.Clamp(_pendingDefaults.QualityIndex, 0, 20);
        qCombo.SelectionChanged += (_, _) => _pendingDefaults.QualityIndex = qCombo.SelectedIndex;
        panel.Children.Add(MakeValueBehaviorRow("Default Output File Size", _pendingDefaults.QualityBehavior, v => _pendingDefaults.QualityBehavior = v, qCombo));

        panel.Children.Add(MakeBehaviorCheckboxRow("Portrait Mode (9:16)", _pendingDefaults.PortraitBehavior, v => _pendingDefaults.PortraitBehavior = v));
        panel.Children.Add(MakeBehaviorCheckboxRow("Boss HP", _pendingDefaults.BossHpBehavior, v => _pendingDefaults.BossHpBehavior = v));
        panel.Children.Add(MakeBehaviorCheckboxRow("Show Teammates", _pendingDefaults.ShowTeammatesBehavior, v => _pendingDefaults.ShowTeammatesBehavior = v));
        panel.Children.Add(MakeBehaviorCheckboxRow("Enable Fade-In/Out", _pendingDefaults.EnableFadeBehavior, v => _pendingDefaults.EnableFadeBehavior = v));

        panel.Children.Add(new TextBlock
        {
            Text = "Speed Editor",
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(0, 16, 0, 2)
        });

        var zoomCombo = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Right };
        zoomCombo.ItemsSource = new List<string> { "Instant (hard cut)", "Slow (gradual)" };
        zoomCombo.SelectedIndex = _pendingDefaults.DefaultZoomSlow ? 1 : 0;
        zoomCombo.SelectionChanged += (_, _) => _pendingDefaults.DefaultZoomSlow = zoomCombo.SelectedIndex == 1;
        panel.Children.Add(MakeSimpleRow("Default Zoom-In Type", zoomCombo));

        var freezeVals = new[] { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };
        var freezeCombo = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Right };
        freezeCombo.ItemsSource = new List<string> { "0.5s", "1.0s", "1.5s", "2.0s", "2.5s", "3.0s" };
        int fi = Array.FindIndex(freezeVals, v => Math.Abs(v - _pendingDefaults.DefaultFreezeDurationS) < 0.01);
        freezeCombo.SelectedIndex = fi >= 0 ? fi : 1;
        freezeCombo.SelectionChanged += (_, _) =>
        {
            if (freezeCombo.SelectedIndex >= 0 && freezeCombo.SelectedIndex < freezeVals.Length)
                _pendingDefaults.DefaultFreezeDurationS = freezeVals[freezeCombo.SelectedIndex];
        };
        panel.Children.Add(MakeSimpleRow("Default Freeze Duration", freezeCombo));

        try
        {
            if (System.IO.File.Exists(_paths.SessionStateFile))
            {
                // ISSUE_1: reflection-free parse. JsonSerializer.Deserialize<JsonObject> needs the
                // reflection resolver that NativeAOT + TrimMode=full removes from the shipped EXE.
                var state = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(_paths.SessionStateFile))?.AsObject();
                if (state != null && state.ContainsKey("CustomMusicDirectory"))
                {
                    _pendingMusicFolder = state["CustomMusicDirectory"]?.GetValue<string>() ?? "";
                }
            }
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

        var txtFolder = this.FindControl<TextBox>("DefaultMusicFolderTextBox");

        var btnFolder = this.FindControl<Button>("BrowseMusicFolderBtn");
        if (btnFolder != null)
        {
            btnFolder.Click += async (s, e) =>
            {
                string startPath = string.IsNullOrWhiteSpace(_pendingMusicFolder) ? Infrastructure.MemeDirectory.GetMusicRoot() : _pendingMusicFolder;   // SANDBOX_01
                var folder = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
                
                var result = await this.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Default Music Folder",
                    SuggestedStartLocation = folder,
                    AllowMultiple = false
                });

                if (result != null && result.Count > 0)
                {
                    DefaultMusicFolder = result[0].Path.LocalPath;
                    if (txtFolder != null) txtFolder.Text = DefaultMusicFolder;
                }
            };
        }
    }

    /// <summary>Meme System §3: path display + Open Folder + Change Folder (with
    /// UnauthorizedAccessException revert guard). Applies IMMEDIATELY (not on APPLY):
    /// the directory change saves, notifies the MainWindow to re-scan, and reverts on failure.</summary>
    private void BuildMemeFolderUi()
    {
        var pathBox = this.FindControl<TextBox>("MemeFolderTextBox");
        var statusText = this.FindControl<TextBlock>("MemeFolderStatusText");
        if (pathBox != null) pathBox.Text = MemeDirectory.GetActive();

        var openBtn = this.FindControl<Button>("OpenMemeFolderBtn");
        if (openBtn != null) openBtn.Click += (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = MemeDirectory.GetActive(),
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { RuntimeLog.Fail("Memes", $"Open meme folder failed: {ex.Message}"); }
        };

        var changeBtn = this.FindControl<Button>("ChangeMemeFolderBtn");
        if (changeBtn != null) changeBtn.Click += async (s, e) =>
        {
            var start = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(MemeDirectory.GetActive()));
            var result = await this.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Meme Folder",
                SuggestedStartLocation = start,
                AllowMultiple = false
            });
            if (result == null || result.Count == 0) return;

            string newPath = result[0].Path.LocalPath;
            string lastKnownGood = SettingsManager.Instance.MemeDirectoryPath;
            try
            {
                _ = System.IO.Directory.GetFiles(newPath);

                SettingsManager.Instance.MemeDirectoryPath = newPath;
                SettingsManager.Save();
                if (pathBox != null) pathBox.Text = newPath;
                if (statusText != null) statusText.Text = "";
                RuntimeLog.Info("Memes", $"Meme directory changed to: {System.IO.Path.GetFileName(newPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))}");
                RuntimeLog.Debug("Memes", $"Meme directory changed to: {newPath}");
                MemeDirectory.NotifyChanged();
            }
            catch (UnauthorizedAccessException ex)
            {
                SettingsManager.Instance.MemeDirectoryPath = lastKnownGood;
                if (pathBox != null) pathBox.Text = MemeDirectory.GetActive();
                if (statusText != null) statusText.Text = "⚠ That folder can't be read (access denied). Keeping the previous folder.";
                RuntimeLog.Fail("Memes", $"Meme directory change blocked (UnauthorizedAccess): {System.IO.Path.GetFileName(newPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))} — {ex.Message}");
                RuntimeLog.Debug("Memes", $"Meme directory change blocked full path: {newPath}");
            }
            catch (Exception ex)
            {
                SettingsManager.Instance.MemeDirectoryPath = lastKnownGood;
                if (pathBox != null) pathBox.Text = MemeDirectory.GetActive();
                if (statusText != null) statusText.Text = "⚠ That folder can't be used. Keeping the previous folder.";
                RuntimeLog.Fail("Memes", $"Meme directory change failed: {System.IO.Path.GetFileName(newPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))} — {ex.Message}");
                RuntimeLog.Debug("Memes", $"Meme directory change failed full path: {newPath}");
            }
        };
    }

    private Grid MakeSimpleRow(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 170"), Margin = new Thickness(0, 0, 0, 5) };
        var text = new TextBlock { Text = label, Foreground = this.TryFindResource("AppTextPrimaryBrush", out var brush) ? brush as IBrush : Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0); Grid.SetColumn(control, 1);
        grid.Children.Add(text); grid.Children.Add(control);
        return grid;
    }

    private Grid MakeBehaviorCheckboxRow(string label, CheckboxDefaultBehavior initialBehavior, Action<CheckboxDefaultBehavior> onToggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 160"), Margin = new Thickness(0, 0, 0, 5) };
        var text = new TextBlock { Text = label, Foreground = this.TryFindResource("AppTextPrimaryBrush", out var brush) ? brush as IBrush : Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Right };
        combo.ItemsSource = new List<string> { "Always Off", "Always On", "Remember Last Choice" };
        combo.SelectedIndex = (int)initialBehavior;
        combo.SelectionChanged += (_, _) => onToggle((CheckboxDefaultBehavior)combo.SelectedIndex);
        
        Grid.SetColumn(text, 0); Grid.SetColumn(combo, 1);
        grid.Children.Add(text); grid.Children.Add(combo);
        return grid;
    }

    private Grid MakeValueBehaviorRow(string label, ValueDefaultBehavior initialBehavior, Action<ValueDefaultBehavior> onToggle, Control valueControl)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 170, Auto"), Margin = new Thickness(0, 0, 0, 5) };
        var text = new TextBlock { Text = label, Foreground = this.TryFindResource("AppTextPrimaryBrush", out var brush) ? brush as IBrush : Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,0,10,0) };
        combo.ItemsSource = new List<string> { "Fixed Value", "Remember Last Choice" };
        combo.SelectedIndex = (int)initialBehavior;
        
        Grid.SetColumn(text, 0); Grid.SetColumn(combo, 1); Grid.SetColumn(valueControl, 2);
        grid.Children.Add(text); grid.Children.Add(combo); grid.Children.Add(valueControl);
        
        Action updateEnable = () => valueControl.IsEnabled = combo.SelectedIndex == 0;
        updateEnable();
        
        combo.SelectionChanged += (_, _) => {
            onToggle((ValueDefaultBehavior)combo.SelectedIndex);
            updateEnable();
        };
        return grid;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_waitingForKeyAction != null)
        {
            _pendingKeys[_waitingForKeyAction] = e.Key;
            _keyButtons[_waitingForKeyAction].Content = e.Key.ToString();
            _waitingForKeyAction = null;
            e.Handled = true;
        }
    }

    private void SaveAndClose()
    {
        var kb = SettingsManager.Instance.KeyBinds;
        kb.PlayPause = _pendingKeys["PlayPause"];
        kb.MarkStart = _pendingKeys["MarkStart"];
        kb.MarkEnd = _pendingKeys["MarkEnd"];
        kb.SeekForward = _pendingKeys["SeekForward"];
        kb.SeekBackward = _pendingKeys["SeekBackward"];
        kb.VolumeUp = _pendingKeys["VolumeUp"];
        kb.VolumeDown = _pendingKeys["VolumeDown"];
        kb.FineSeekForward = _pendingKeys["FineSeekForward"];
        kb.FineSeekBackward = _pendingKeys["FineSeekBackward"];
        kb.AggressiveVolumeUp = _pendingKeys["AggressiveVolumeUp"];
        kb.AggressiveVolumeDown = _pendingKeys["AggressiveVolumeDown"];

        SettingsManager.Instance.Defaults = _pendingDefaults;

        SettingsManager.Instance.ConfirmVideoMergerRemove = ConfirmVideoMergerRemove;
        SettingsManager.Instance.ConfirmVideoMergerClearAll = ConfirmVideoMergerClearAll;
        SettingsManager.Instance.ConfirmCropToolReset = ConfirmCropToolReset;
        SettingsManager.Instance.ConfirmCropToolDelete = ConfirmCropToolDelete;

        // AUDIO_06 — takes effect immediately; UiSoundEffect reads these on every play.
        SettingsManager.Instance.UiSoundsEnabled = UiSoundsEnabled;
        SettingsManager.Instance.UiSoundVolume = Math.Clamp(UiSoundVolume, 0, 100);

        SettingsManager.Instance.ThemeMode = _pendingThemeMode;
        SettingsManager.Instance.FontScale = _pendingFontScale;
        SettingsManager.Instance.LoudnessNormalizationPrompt = _pendingLoudnessPrompt;
        SettingsManager.Instance.PeakFlatteningPrompt = _pendingPeakPrompt;
        ThemeManager.ApplyTheme(_pendingThemeMode);
        ThemeManager.ApplyFontScale(_pendingFontScale);

        if (_pendingMaskOverlay != SettingsManager.Instance.ActiveMaskOverlay)
        {
            MaskOverlayManager.ApplyProfile(_pendingMaskOverlay);
        }

        // G03 — encoder override. Read by MainWindow.ResolveHardwareMode() and
        // VideoMergerWindow on the NEXT export; nothing needs to restart.
        if (_pendingVideoEncoder != SettingsManager.Instance.VideoEncoderOverride)
        {
            RuntimeLog.Info("Settings", $"Video encoder override changed: {SettingsManager.Instance.VideoEncoderOverride} -> {_pendingVideoEncoder}");
            SettingsManager.Instance.VideoEncoderOverride = _pendingVideoEncoder;
        }

        SettingsManager.Save();
        
        try
        {
            new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                {
                    ["CustomMusicDirectory"] = _pendingMusicFolder
                });
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }

        Close(true);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "SettingsBounds");

        this.Hide();

        try
        {

        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SETTINGS", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            _isSafeToClose = true;
            this.Close();
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
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
                }
            };
        }
    }
}

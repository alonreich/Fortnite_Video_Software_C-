using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.App.Infrastructure;
using System;
using System.Collections.Generic;

using System;
using System.Collections.Generic;
using System.Linq;

namespace FortniteVideoSoftware.App.Controls;

public partial class SettingsWindow : Window
{
    private Dictionary<string, Key> _pendingKeys = new();
    private Dictionary<string, Button> _keyButtons = new();
    private string? _waitingForKeyAction;
    private bool _isSafeToClose = false;

    // Pending default values (edited in the Defaults tab, applied on SAVE)
    private DefaultValues _pendingDefaults = new();
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private string _pendingMusicFolder = "";

    public SettingsWindow()
    {
        InitializeComponent();
        FortniteVideoSoftware.App.Infrastructure.WindowManager.RegisterWindow(this);

        // Snapshot current defaults so CANCEL discards changes
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
            NoFade = SettingsManager.Instance.Defaults.NoFade,
            NoFadeBehavior = SettingsManager.Instance.Defaults.NoFadeBehavior,
            QualityIndex = SettingsManager.Instance.Defaults.QualityIndex,
            QualityBehavior = SettingsManager.Instance.Defaults.QualityBehavior
        };

        LoadCurrentKeybinds();
        BuildKeyBindsUi();
        BuildDefaultsUi();

        this.FindControl<Button>("SaveBtn")!.Click += (s, e) => SaveAndClose();
        this.FindControl<Button>("CancelBtn")!.Click += (s, e) => Close();

        this.KeyDown += OnWindowKeyDown;
        AttachTitleBarDrag();
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
                Foreground = Brushes.White,
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

        private void BuildDefaultsUi()
    {
        var panel = this.FindControl<StackPanel>("DefaultsPanel");
        if (panel == null) return;

        // ── Default Speed (0.1 – 4.0) ──
        var speedNum = new NumericUpDown
        {
            Minimum = 0.1m, Maximum = 4.0m, Increment = 0.1m,
            Value = (decimal)_pendingDefaults.DefaultSpeed,
            FormatString = "0.0",
            Width = 100, HorizontalAlignment = HorizontalAlignment.Right
        };
        speedNum.ValueChanged += (_, _) => _pendingDefaults.DefaultSpeed = (double)(speedNum.Value ?? (decimal)1.1);
        panel.Children.Add(MakeValueBehaviorRow("Default Speed", _pendingDefaults.SpeedBehavior, v => _pendingDefaults.SpeedBehavior = v, speedNum));

        // ── Default Output File Size (index 0-20) ──
        var qCombo = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Right };
        var qItems = new List<string>();
        for (int i = 0; i < 20; i++) qItems.Add($"{5 + i * 5}MB");
        qItems.Add("ORIGINAL QUALITY");
        qCombo.ItemsSource = qItems;
        qCombo.SelectedIndex = Math.Clamp(_pendingDefaults.QualityIndex, 0, 20);
        qCombo.SelectionChanged += (_, _) => _pendingDefaults.QualityIndex = qCombo.SelectedIndex;
        panel.Children.Add(MakeValueBehaviorRow("Default Output File Size", _pendingDefaults.QualityBehavior, v => _pendingDefaults.QualityBehavior = v, qCombo));

        // ── Checkbox Defaults ──
        panel.Children.Add(MakeBehaviorCheckboxRow("Portrait Mode (9:16)", _pendingDefaults.PortraitBehavior, v => _pendingDefaults.PortraitBehavior = v));
        panel.Children.Add(MakeBehaviorCheckboxRow("Boss HP", _pendingDefaults.BossHpBehavior, v => _pendingDefaults.BossHpBehavior = v));
        panel.Children.Add(MakeBehaviorCheckboxRow("Show Teammates", _pendingDefaults.ShowTeammatesBehavior, v => _pendingDefaults.ShowTeammatesBehavior = v));
        panel.Children.Add(MakeBehaviorCheckboxRow("Disable Fade-In/Out", _pendingDefaults.NoFadeBehavior, v => _pendingDefaults.NoFadeBehavior = v));

        // ── Default Music Folder ──
        try
        {
            if (System.IO.File.Exists(_paths.SessionStateFile))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(_paths.SessionStateFile));
                if (state != null && state.ContainsKey("CustomMusicDirectory"))
                {
                    _pendingMusicFolder = state["CustomMusicDirectory"]?.GetValue<string>() ?? "";
                }
            }
        }
        catch { }

        var txtFolder = this.FindControl<TextBox>("DefaultMusicFolderTextBox");
        if (txtFolder != null)
        {
            txtFolder.Text = _pendingMusicFolder;
            txtFolder.TextChanged += (_, _) => _pendingMusicFolder = txtFolder.Text ?? "";
        }

        var btnFolder = this.FindControl<Button>("BrowseMusicFolderBtn");
        if (btnFolder != null)
        {
            btnFolder.Click += async (s, e) =>
            {
                string startPath = string.IsNullOrWhiteSpace(_pendingMusicFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) : _pendingMusicFolder;
                var folder = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
                
                var result = await this.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Default Music Folder",
                    SuggestedStartLocation = folder,
                    AllowMultiple = false
                });

                if (result != null && result.Count > 0)
                {
                    _pendingMusicFolder = result[0].Path.LocalPath;
                    if (txtFolder != null) txtFolder.Text = _pendingMusicFolder;
                }
            };
        }
    }

    private Grid MakeBehaviorCheckboxRow(string label, CheckboxDefaultBehavior initialBehavior, Action<CheckboxDefaultBehavior> onToggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 160"), Margin = new Thickness(0, 0, 0, 5) };
        var text = new TextBlock { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
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
        var text = new TextBlock { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
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

        // Apply default values
        SettingsManager.Instance.Defaults = _pendingDefaults;

        SettingsManager.Save();
        
        try
        {
            new FortniteVideoSoftware.Core.Ipc.StateTransferStore(_paths)
                .UpdatePropertiesSync(new System.Text.Json.Nodes.JsonObject
                {
                    ["CustomMusicDirectory"] = _pendingMusicFolder
                });
        }
        catch { }

        Close(true); // Return true to indicate settings were changed
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // If the background work is done, allow the window to close normally
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        // STOP the synchronous UI-blocking close
        e.Cancel = true;
        FortniteVideoSoftware.App.Infrastructure.WindowManager.SaveAll();

        // Hide the window instantly so the app feels incredibly fast and responsive
        this.Hide();

        try
        {
            // Perform the heavy Mutex locking and file I/O ASYNCHRONOUSLY

        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SETTINGS", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            // Mark as safe and programmatically re-trigger the close
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
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }
}





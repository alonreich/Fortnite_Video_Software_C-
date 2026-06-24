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

namespace FortniteVideoSoftware.App.Controls;

public partial class SettingsWindow : Window
{
    private Dictionary<string, Key> _pendingKeys = new();
    private Dictionary<string, Button> _keyButtons = new();
    private string? _waitingForKeyAction;

    // Pending default values (edited in the Defaults tab, applied on SAVE)
    private DefaultValues _pendingDefaults = new();

    public SettingsWindow()
    {
        InitializeComponent();

        if (Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light)
        {
            var mainBorder = this.FindControl<Border>("MainBorder");
            var titleBarBorder = this.FindControl<Border>("TitleBarBorder");
            
            if (mainBorder != null) mainBorder.BorderBrush = Brush.Parse("#334155");
            if (titleBarBorder != null) titleBarBorder.Background = Brush.Parse("#0f172a");
        }

        // Snapshot current defaults so CANCEL discards changes
        _pendingDefaults = new DefaultValues
        {
            DefaultSpeed = SettingsManager.Instance.Defaults.DefaultSpeed,
            PortraitMode = SettingsManager.Instance.Defaults.PortraitMode,
            BossHp = SettingsManager.Instance.Defaults.BossHp,
            ShowTeammates = SettingsManager.Instance.Defaults.ShowTeammates,
            NoFade = SettingsManager.Instance.Defaults.NoFade,
            QualityIndex = SettingsManager.Instance.Defaults.QualityIndex,
            Volume = SettingsManager.Instance.Defaults.Volume
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
            { "VolumeUp", "Volume Up" },
            { "VolumeDown", "Volume Down" }
        };

        foreach (var kvp in labels)
        {
            var actionId = kvp.Key;
            var description = kvp.Value;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 150") };
            
            var text = new TextBlock 
            { 
                Text = description, 
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            var btn = new Button 
            { 
                Content = _pendingKeys[actionId].ToString(),
                Width = 150,
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
    }

    private void BuildDefaultsUi()
    {
        var panel = this.FindControl<StackPanel>("DefaultsPanel");
        if (panel == null) return;

        // ── Default Speed (0.1 – 4.0) ──
        var speedGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 120") };
        var speedLabel = new TextBlock { Text = "Default Speed", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var speedNum = new NumericUpDown
        {
            Minimum = 0.1m, Maximum = 4.0m, Increment = 0.1m,
            Value = (decimal)_pendingDefaults.DefaultSpeed,
            FormatString = "0.0",
            Width = 120, HorizontalAlignment = HorizontalAlignment.Right
        };
        speedNum.ValueChanged += (_, _) => _pendingDefaults.DefaultSpeed = (double)speedNum.Value;
        Grid.SetColumn(speedLabel, 0); Grid.SetColumn(speedNum, 1);
        speedGrid.Children.Add(speedLabel); speedGrid.Children.Add(speedNum);
        panel.Children.Add(speedGrid);

        // ── Default Output File Size (index 0-20) ──
        var qGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 200") };
        var qLabel = new TextBlock { Text = "Default Output File Size", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var qCombo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Right };
        var qItems = new List<string>();
        for (int i = 0; i < 20; i++) qItems.Add($"{5 + i * 5}MB");
        qItems.Add("ORIGINAL QUALITY");
        qCombo.ItemsSource = qItems;
        qCombo.SelectedIndex = Math.Clamp(_pendingDefaults.QualityIndex, 0, 20);
        qCombo.SelectionChanged += (_, _) => _pendingDefaults.QualityIndex = qCombo.SelectedIndex;
        Grid.SetColumn(qLabel, 0); Grid.SetColumn(qCombo, 1);
        qGrid.Children.Add(qLabel); qGrid.Children.Add(qCombo);
        panel.Children.Add(qGrid);

        // ── Default Volume (0-100) ──
        var volGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 200") };
        var volLabel = new TextBlock { Text = "Default Volume", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var volSlider = new Slider { Minimum = 0, Maximum = 100, Value = _pendingDefaults.Volume, Width = 200, HorizontalAlignment = HorizontalAlignment.Right };
        volSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) _pendingDefaults.Volume = (int)volSlider.Value; };
        Grid.SetColumn(volLabel, 0); Grid.SetColumn(volSlider, 1);
        volGrid.Children.Add(volLabel); volGrid.Children.Add(volSlider);
        panel.Children.Add(volGrid);

        // ── Checkbox Defaults ──
        panel.Children.Add(MakeCheckboxRow("Portrait Mode (9:16)", _pendingDefaults.PortraitMode, v => _pendingDefaults.PortraitMode = v));
        panel.Children.Add(MakeCheckboxRow("Boss HP", _pendingDefaults.BossHp, v => _pendingDefaults.BossHp = v));
        panel.Children.Add(MakeCheckboxRow("Show Teammates", _pendingDefaults.ShowTeammates, v => _pendingDefaults.ShowTeammates = v));
        panel.Children.Add(MakeCheckboxRow("Disable Fade-In/Out", _pendingDefaults.NoFade, v => _pendingDefaults.NoFade = v));
    }

    private Grid MakeCheckboxRow(string label, bool isChecked, Action<bool> onToggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };
        var tb = new TextBlock { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var cb = new ToggleButton { IsChecked = isChecked, Width = 60, HorizontalAlignment = HorizontalAlignment.Right };
        cb.IsCheckedChanged += (_, _) => onToggle(cb.IsChecked == true);
        Grid.SetColumn(tb, 0); Grid.SetColumn(cb, 1);
        grid.Children.Add(tb); grid.Children.Add(cb);
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

        // Apply default values
        SettingsManager.Instance.Defaults = _pendingDefaults;

        SettingsManager.Save();
        Close(true); // Return true to indicate settings were changed
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = WindowBoundsHelper.LoadBoundsAsync(this, "SettingsBounds");
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        try { WindowBoundsHelper.SaveBoundsSync(this, "SettingsBounds"); } catch { }
        base.OnClosing(e);
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    try { BeginMoveDrag(e); } catch { }
                }
            };
        }
    }
}
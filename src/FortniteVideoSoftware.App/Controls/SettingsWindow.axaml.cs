using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FortniteVideoSoftware.Core.Infrastructure;
using System.Collections.Generic;

namespace FortniteVideoSoftware.App.Controls;

public partial class SettingsWindow : Window
{
    private Dictionary<string, Key> _pendingKeys = new();
    private Dictionary<string, Button> _keyButtons = new();
    private string? _waitingForKeyAction;

    public SettingsWindow()
    {
        InitializeComponent();

        if (Avalonia.Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Styling.ThemeVariant.Light)
        {
            var mainBorder = this.FindControl<Border>("MainBorder");
            var titleBarBorder = this.FindControl<Border>("TitleBarBorder");
            
            if (mainBorder != null) mainBorder.BorderBrush = Avalonia.Media.Brush.Parse("#334155");
            if (titleBarBorder != null) titleBarBorder.Background = Avalonia.Media.Brush.Parse("#0f172a");
        }

        LoadCurrentKeybinds();
        BuildKeyBindsUi();

        this.FindControl<Button>("SaveBtn")!.Click += (s, e) => SaveAndClose();
        this.FindControl<Button>("CancelBtn")!.Click += (s, e) => Close();

        this.KeyDown += OnWindowKeyDown;
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
                Foreground = Avalonia.Media.Brushes.White,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
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

        SettingsManager.Save();
        Close(true); // Return true to indicate settings were changed
    }
}

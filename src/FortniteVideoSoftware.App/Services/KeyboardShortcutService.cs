using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FortniteVideoSoftware.App.Infrastructure;

namespace FortniteVideoSoftware.App.Services;

public static class KeyboardShortcutService
{
    public static void BuildShortcutSheetRows(StackPanel rows, Window parent)
    {
        rows.Children.Clear();
        var kb = SettingsManager.Instance.KeyBinds;

        void Section(string title)
        {
            rows.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.Bold,
                FontSize = ThemeManager.ScaledFontSize(12),
                Margin = new Thickness(0, 12, 0, 2),
                Foreground = ThemeResources.Brush(parent, "AppAccentBrush", new SolidColorBrush(Color.Parse("#60a5fa")))
            });
        }

        void Row(string keyText, string what)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };

            var chip = new Border
            {
                Background = ThemeResources.Brush(parent, "AppControlTrackBrush", new SolidColorBrush(Color.Parse("#222222"))),
                BorderBrush = ThemeResources.Brush(parent, "AppBorderBrush", new SolidColorBrush(Color.Parse("#475569"))),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = keyText,
                    FontWeight = FontWeight.Bold,
                    FontSize = ThemeManager.ScaledFontSize(11),
                    Foreground = ThemeResources.Brush(parent, "AppTextPrimaryBrush", new SolidColorBrush(Color.Parse("#ffffff")))
                }
            };
            Grid.SetColumn(chip, 0);
            grid.Children.Add(chip);

            var label = new TextBlock
            {
                Text = what,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = ThemeManager.ScaledFontSize(12),
                Foreground = ThemeResources.Brush(parent, "AppTextMutedBrush", new SolidColorBrush(Color.Parse("#b6c2d0")))
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            rows.Children.Add(grid);
        }

        Section("Playing and moving around");
        Row(PrettyKey(kb.PlayPause), "Play or pause the video");
        Row(PrettyKey(kb.SeekForward), "Jump forwards");
        Row(PrettyKey(kb.SeekBackward), "Jump backwards");
        Row("Ctrl + " + PrettyKey(kb.FineSeekForward), "Nudge forwards one frame at a time");
        Row("Ctrl + " + PrettyKey(kb.FineSeekBackward), "Nudge backwards one frame at a time");

        Section("Cutting the clip");
        Row(PrettyKey(kb.MarkStart), "Cut off everything before this point");
        Row(PrettyKey(kb.MarkEnd), "Cut off everything after this point");

        Section("Volume");
        Row(PrettyKey(kb.VolumeUp), "Louder");
        Row(PrettyKey(kb.VolumeDown), "Quieter");
        Row("Ctrl + " + PrettyKey(kb.AggressiveVolumeUp), "Louder, in big steps");
        Row("Ctrl + " + PrettyKey(kb.AggressiveVolumeDown), "Quieter, in big steps");

        Section("This card");
        Row("?", "Open or close this list");
        Row("Esc", "Close this list");
    }

    public static string PrettyKey(Key key) => key switch
    {
        Key.Space => "Space",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemBackslash => "\\",
        Key.OemTilde => "`",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "Page Up",
        Key.PageDown => "Page Down",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString()
    };
}

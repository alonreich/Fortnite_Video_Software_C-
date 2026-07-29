using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Spatial visual grid for meme selection (replaces the boring text dropdown).
/// Shows a grid of thumbnails so users pick with their EYES, not filenames.
/// Image memes get static thumbnails; video memes get a placeholder with play icon.
/// Aspect-ratio validation styling is preserved (red text for mismatched ratios).
/// </summary>
public partial class MemeWallControl : UserControl
{
    private Border? _backdrop;
    private WrapPanel? _gridPanel;
    private Button? _closeButton;

    /// <summary>Fired when a meme is selected; passes the file path.</summary>
    public event Action<string>? MemeSelected;

    /// <summary>Fired when the wall is closed without a selection.</summary>
    public event Action? Closed;

    public sealed class MemeItem
    {
        public required string FilePath { get; init; }
        public required string DisplayName { get; init; }
        public bool IsVideo { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double AspectRatio => Height > 0 ? (double)Width / Height : 1.0;
        public Bitmap? Thumbnail { get; set; }
    }

    public MemeWallControl()
    {
        InitializeComponent();
        _backdrop = this.FindControl<Border>("BackdropBorder");
        _gridPanel = this.FindControl<WrapPanel>("MemeGridPanel");
        _closeButton = this.FindControl<Button>("CloseButton");

        if (_closeButton != null)
            _closeButton.Click += (_, _) => CloseWall();

        if (_backdrop != null)
        {
            _backdrop.PointerPressed += (_, e) =>
            {
                if (e.Source == _backdrop)
                    CloseWall();
            };
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Populates and shows the meme wall.
    /// </summary>
    /// <param name="items">Meme items to display.</param>
    /// <param name="isPortraitMode">If true, landscape memes are flagged with red warning text.</param>
    public void ShowWall(IEnumerable<MemeItem> items, bool isPortraitMode)
    {
        if (_gridPanel == null) return;

        _gridPanel.Children.Clear();

        foreach (var item in items)
        {
            var cell = CreateMemeCell(item, isPortraitMode);
            _gridPanel.Children.Add(cell);
        }

        if (_backdrop != null)
        {
            _backdrop.Classes.Remove("MemeWallHidden");
            _backdrop.Classes.Add("MemeWallVisible");
        }
        IsVisible = true;
        Opacity = 1;
        IsHitTestVisible = true;
    }

    /// <summary>Closes the wall without a selection.</summary>
    public void CloseWall()
    {
        if (_backdrop != null)
        {
            _backdrop.Classes.Remove("MemeWallVisible");
            _backdrop.Classes.Add("MemeWallHidden");
        }
        IsHitTestVisible = false;
        Task.Delay(220).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (!IsHitTestVisible)
            {
                Opacity = 0;
                IsVisible = false;
            }
        }));
        Closed?.Invoke();
    }

    private Control CreateMemeCell(MemeItem item, bool isPortraitMode)
    {
        bool ratioWarning = isPortraitMode && item.AspectRatio > 0.85;

        var cellBorder = new Border
        {
            Classes = { "MemeCell" },
            Margin = new Thickness(6),
            MinWidth = 110,
            MinHeight = 90,
            MaxWidth = 150
        };

        var stack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        if (item.Thumbnail != null)
        {
            var img = new Image
            {
                Source = item.Thumbnail,
                MaxWidth = 120,
                MaxHeight = 70,
                Stretch = Stretch.UniformToFill
            };
            stack.Children.Add(img);
        }
        else
        {
            var placeholder = new Border
            {
                MinWidth = 100,
                MinHeight = 60,
                Background = (IBrush?)Application.Current?.FindResource("AppSurfaceAlpha80Brush"),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = item.IsVideo ? "🎬" : "🖼",
                    FontSize = ThemeManager.ScaledFontSize(28),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            stack.Children.Add(placeholder);
        }

        var nameText = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = ThemeManager.ScaledFontSize(10),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Foreground = (IBrush?)Application.Current?.FindResource("AppTextMutedBrush")
        };
        stack.Children.Add(nameText);

        if (ratioWarning)
        {
            var warn = new TextBlock
            {
                Text = "⚠ Landscape",
                FontSize = ThemeManager.ScaledFontSize(9),
                Foreground = (IBrush?)Application.Current?.FindResource("AppWarningBrush"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            ToolTip.SetTip(cellBorder, "Fit for landscape — may crop in portrait mode");
            stack.Children.Add(warn);
        }

        cellBorder.Child = stack;

        cellBorder.PointerPressed += (_, e) =>
        {
            cellBorder.Classes.Add("MemeCellSelected");
            MemeSelected?.Invoke(item.FilePath);
            DispatcherTimer.RunOnce(() => CloseWall(), TimeSpan.FromMilliseconds(200));
            e.Handled = true;
        };

        return cellBorder;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

public partial class AiSubjectPickerWindow : Window
{
    public DiscoveredSubject? SelectedSubject { get; private set; }

    private readonly List<DiscoveredSubject> _subjects;
    private readonly List<Border> _cardBorders = new();
    private DiscoveredSubject? _currentSelected;

    public AiSubjectPickerWindow() : this(string.Empty, string.Empty, string.Empty, 0, 0, 0, new List<DiscoveredSubject>())
    {
    }

    public AiSubjectPickerWindow(
        string frame1Path,
        string frame2Path,
        string frame3Path,
        double t1,
        double t2,
        double t3,
        List<DiscoveredSubject> subjects)
    {
        _subjects = subjects ?? new List<DiscoveredSubject>();
        InitializeComponent();

        var scope = this.FindNameScope();

        var ci = CultureInfo.InvariantCulture;
        var f1Lbl = scope?.Find("Frame1TimeLabel") as TextBlock;
        var f2Lbl = scope?.Find("Frame2TimeLabel") as TextBlock;
        var f3Lbl = scope?.Find("Frame3TimeLabel") as TextBlock;

        if (f1Lbl != null) f1Lbl.Text = $"Angle 1 (Start): {t1.ToString("0.00", ci)}s";
        if (f2Lbl != null) f2Lbl.Text = $"Angle 2 (Midpoint): {t2.ToString("0.00", ci)}s";
        if (f3Lbl != null) f3Lbl.Text = $"Angle 3 (End): {t3.ToString("0.00", ci)}s";

        LoadFrameImage(scope?.Find("Frame1Image") as Image, frame1Path);
        LoadFrameImage(scope?.Find("Frame2Image") as Image, frame2Path);
        LoadFrameImage(scope?.Find("Frame3Image") as Image, frame3Path);

        var container = scope?.Find("SubjectsContainer") as StackPanel;
        var confirmBtn = scope?.Find("ConfirmPickerBtn") as Button;
        var cancelBtn = scope?.Find("CancelPickerBtn") as Button;
        var statusText = scope?.Find("SelectionStatusText") as TextBlock;

        if (cancelBtn != null)
        {
            cancelBtn.Click += (_, _) => Close(false);
        }

        if (confirmBtn != null)
        {
            confirmBtn.Click += (_, _) =>
            {
                if (_currentSelected != null)
                {
                    SelectedSubject = _currentSelected;
                    Close(true);
                }
            };
        }

        if (container != null && _subjects.Count > 0)
        {
            for (int i = 0; i < _subjects.Count; i++)
            {
                var sub = _subjects[i];
                var card = CreateSubjectCard(sub, i + 1, confirmBtn, statusText);
                _cardBorders.Add(card);
                container.Children.Add(card);
            }

            SelectSubject(_subjects[0], _cardBorders[0], confirmBtn, statusText);
        }
    }

    private static void LoadFrameImage(Image? img, string path)
    {
        if (img == null || !File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            img.Source = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private Border CreateSubjectCard(DiscoveredSubject sub, int index, Button? confirmBtn, TextBlock? statusText)
    {
        var card = new Border
        {
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(16, 12),
            MinWidth = 230,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Transparent),
            BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray)
        };

        var sp = new StackPanel { Spacing = 6 };

        var headerRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var badge = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(10),
            Width = 22,
            Height = 22,
            Background = ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple)
        };
        badge.Child = new TextBlock
        {
            Text = index.ToString(),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        headerRow.Children.Add(badge);

        var titleBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(sub.Label) ? $"Subject #{sub.Id}" : sub.Label,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White)
        };
        headerRow.Children.Add(titleBlock);
        sp.Children.Add(headerRow);

        var coordText = new TextBlock
        {
            Text = $"Anchor: [{sub.Ymin},{sub.Xmin}] to [{sub.Ymax},{sub.Xmax}]",
            FontSize = 11,
            Foreground = ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray)
        };
        sp.Children.Add(coordText);

        var actionBadge = new TextBlock
        {
            Text = "CLICK TO SELECT",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple)
        };
        sp.Children.Add(actionBadge);

        card.Child = sp;

        card.PointerEntered += (_, _) =>
        {
            if (_currentSelected != sub)
            {
                card.BorderBrush = ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple);
            }
        };

        card.PointerExited += (_, _) =>
        {
            if (_currentSelected != sub)
            {
                card.BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray);
            }
        };

        card.PointerPressed += (_, _) => SelectSubject(sub, card, confirmBtn, statusText);

        return card;
    }

    private void SelectSubject(DiscoveredSubject sub, Border selectedCard, Button? confirmBtn, TextBlock? statusText)
    {
        _currentSelected = sub;

        for (int i = 0; i < _cardBorders.Count; i++)
        {
            var c = _cardBorders[i];
            bool isThis = c == selectedCard;
            if (isThis)
            {
                c.BorderBrush = ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple);
                c.Background = ThemeResources.Brush(this, "AppSurfaceSubtleBrush", Brushes.DimGray);
            }
            else
            {
                c.BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray);
                c.Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Transparent);
            }
        }

        if (confirmBtn != null) confirmBtn.IsEnabled = true;
        if (statusText != null)
        {
            statusText.Text = $"Target Selected: {sub.Label}";
            statusText.Foreground = ThemeResources.Brush(this, "AppSuccessBrush", Brushes.ForestGreen);
        }
    }
}

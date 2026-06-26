using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using FortniteVideoSoftware.Core.Infrastructure;
using System.Diagnostics;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using Avalonia.Media;
using Avalonia.Layout;

namespace FortniteVideoSoftware.App;

public class MediaItem
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}

public partial class AdvancedVideoEditorWindow : Window
{
    public ObservableCollection<MediaItem> MediaPool { get; } = new();

    // Demo duration (seconds) used to drive ticks / pill formatting until MPV is wired in.
    private const double DemoDurationSeconds = 120.0;
    private double _timelineWidth = 0;
    private bool _pillVisible = false;

    public AdvancedVideoEditorWindow()
    {
        InitializeComponent();

        var mediaPoolList = this.FindControl<ListBox>("MediaPoolList");
        if (mediaPoolList != null)
        {
            mediaPoolList.ItemsSource = MediaPool;
        }

        var returnBtn = this.FindControl<Button>("ReturnButton");
        if (returnBtn != null)
        {
            returnBtn.Click += (s, e) =>
            {
                ReturnToMainApp();
            };
        }

        var exportBtn = this.FindControl<Button>("ExportButton");
        if (exportBtn != null)
        {
            exportBtn.Click += (s, e) =>
            {
                RuntimeLog.Info("ADVANCED_EDITOR", "Export clicked in Advanced Editor");
            };
        }

        var mediaPoolBorder = this.FindControl<Border>("MediaPoolBorder");
        if (mediaPoolBorder != null)
        {
            mediaPoolBorder.AddHandler(DragDrop.DropEvent, OnFilesDropped);
        }

        WirePremiumTimeline();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  PREMIUM X-AXIS TIMELINE WIRING
    //  Specs 6 (tick rendering), 7 (marker positioning), 8 (magnetic pill), 9 (glow).
    // ─────────────────────────────────────────────────────────────────────────────
    private void WirePremiumTimeline()
    {
        var slider = this.FindControl<Slider>("TimelineSlider");
        var scaleCanvas = this.FindControl<Canvas>("TimelineScaleCanvas");
        var markersCanvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        var container = this.FindControl<Border>("TimelineContainer");
        var startMarker = this.FindControl<Rectangle>("StartMarker");
        var endMarker = this.FindControl<Rectangle>("EndMarker");
        var pill = this.FindControl<Border>("TimePill");
        var pillText = this.FindControl<TextBlock>("TimePillText");

        if (slider == null || scaleCanvas == null || markersCanvas == null ||
            container == null || startMarker == null || endMarker == null ||
            pill == null || pillText == null)
        {
            RuntimeLog.Fail("ADVANCED_EDITOR", "Premium timeline controls not found; aborting wiring.");
            return;
        }

        container.AddHandler(DragDrop.DropEvent, OnFilesDroppedOnTimeline);

        // Re-render ticks/markers whenever the timeline width changes.
        container.SizeChanged += (s, e) =>
        {
            _timelineWidth = container.Bounds.Width;
            RenderTimeScaleTicks(scaleCanvas, _timelineWidth);
            PositionMarkers(startMarker, endMarker, slider.Value, _timelineWidth);
            UpdatePill(pill, pillText, slider.Value, _timelineWidth);
        };

        // Spec 8: Continuous pill tracking while the value changes (keyboard / programmatic).
        slider.ValueChanged += (s, e) =>
        {
            PositionMarkers(startMarker, endMarker, e.NewValue, _timelineWidth);
            UpdatePill(pill, pillText, e.NewValue, _timelineWidth);
        };

        // Spec 8: Continuous pill tracking via pointer (drag + hover-scrub feel).
        slider.PointerMoved += (s, e) =>
        {
            var p = e.GetCurrentPoint(s as Visual);
            EnsurePillVisible(pill);
            // Map pointer X to a 0..100 value for the pill text while hovering.
            double interactionWidth = slider.Bounds.Width > 0 ? slider.Bounds.Width : _timelineWidth;
            double frac = interactionWidth > 0 ? Math.Clamp(p.Position.X / interactionWidth, 0, 1) : 0;
            UpdatePill(pill, pillText, frac * 100.0, _timelineWidth);
        };

        // Spec 8: Hide the pill when the pointer leaves the slider.
        slider.PointerExited += (s, e) => HidePill(pill);
    }

    // Spec 6: Render evenly spaced tick marks + time labels into the scale canvas.
    private void RenderTimeScaleTicks(Canvas canvas, double width)
    {
        canvas.Children.Clear();
        if (width <= 0) return;

        int tickCount = 10;
        for (int i = 0; i <= tickCount; i++)
        {
            double x = (width / tickCount) * i;

            // Minor tick line
            var tick = new Rectangle
            {
                Width = 1,
                Height = 6,
                Fill = Brushes.Gray,
                Opacity = 0.7
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 8);
            canvas.Children.Add(tick);

            // Time label (mm:ss)
            double seconds = (DemoDurationSeconds / tickCount) * i;
            var label = new TextBlock
            {
                Text = $"{(int)seconds / 60:00}:{(int)seconds % 60:00}",
                Foreground = Brushes.LightGray,
                FontSize = 9
            };
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 0);
            canvas.Children.Add(label);
        }
    }

    // Spec 7: Position Start/End markers strictly within the markers canvas (rendered above the slider).
    private void PositionMarkers(Rectangle startMarker, Rectangle endMarker, double sliderValue, double width)
    {
        if (width <= 0) return;

        // Demo: Start pinned at 0%, End pinned at 100%. The slider thumb follows sliderValue.
        Canvas.SetLeft(startMarker, 0);
        Canvas.SetLeft(endMarker, width - endMarker.Width);

        // Spec 9: The markers carry their DropShadow glow in XAML; refresh their top anchors too.
        Canvas.SetTop(startMarker, 2);
        Canvas.SetTop(endMarker, 2);
    }

    // Spec 8: Translate the magnetic time pill to sit above the current slider position.
    private void UpdatePill(Border pill, TextBlock pillText, double sliderValue, double width)
    {
        if (width <= 0) return;

        double frac = Math.Clamp(sliderValue / 100.0, 0, 1);
        double seconds = frac * DemoDurationSeconds;
        pillText.Text = $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";

        // Center the pill on the thumb, clamped so it never spills past the window edges.
        double pillWidth = Math.Max(pill.Bounds.Width, 48);
        double maxX = Math.Max(0, width - pillWidth);
        double x = Math.Clamp((frac * width) - (pillWidth / 2.0), 0, maxX);
        double y = -28; // Floats above the slider thumb.

        var renderTransform = new TranslateTransform(x, y);
        pill.RenderTransform = renderTransform;
    }

    private void EnsurePillVisible(Border pill)
    {
        if (!_pillVisible)
        {
            _pillVisible = true;
            pill.Opacity = 1;
        }
    }

    private void HidePill(Border pill)
    {
        _pillVisible = false;
        pill.Opacity = 0;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  DRAG-DROP / MEDIA POOL (preserved from original design)
    // ─────────────────────────────────────────────────────────────────────────────
    private void OnFilesDropped(object? sender, DragEventArgs e)
    {
        if (e.Data.GetFiles() is { } files)
        {
            foreach (var file in files)
            {
                string path = file.Path.LocalPath;
                if (MediaPool.Any(m => m.FilePath == path)) continue;

                MediaPool.Add(new MediaItem 
                { 
                    Name = System.IO.Path.GetFileName(path),
                    FilePath = path
                });
            }
        }
    }

    private void OnFilesDroppedOnTimeline(object? sender, DragEventArgs e)
    {
        if (e.Data.GetFiles() is { } files)
        {
            var timelineCanvas = this.FindControl<Canvas>("TimelineCanvas");

            foreach (var file in files)
            {
                string path = file.Path.LocalPath;
                
                // User requirement: "so if one drags directly to the lane it is also in the media pool as well"
                if (!MediaPool.Any(m => m.FilePath == path))
                {
                    MediaPool.Add(new MediaItem 
                    { 
                        Name = System.IO.Path.GetFileName(path),
                        FilePath = path
                    });
                }

                if (timelineCanvas != null)
                {
                    // Render a dummy clip on the timeline for visualization
                    var clipRect = new Border
                    {
                        Background = Brushes.SteelBlue,
                        BorderBrush = Brushes.LightBlue,
                        BorderThickness = new Avalonia.Thickness(1),
                        CornerRadius = new Avalonia.CornerRadius(4),
                        Width = 150,
                        Height = 60,
                        Child = new TextBlock
                        {
                            Text = System.IO.Path.GetFileName(path),
                            Foreground = Brushes.White,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Avalonia.Thickness(5)
                        }
                    };
                    
                    // Stagger clips so they don't exactly overlap on drop
                    double yOffset = timelineCanvas.Children.Count * 65;
                    Canvas.SetLeft(clipRect, 10);
                    Canvas.SetTop(clipRect, 10 + yOffset);
                    timelineCanvas.Children.Add(clipRect);
                }
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ReturnToMainApp()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? "FortniteVideoSoftware.App.exe";
            Process.Start(new ProcessStartInfo(exePath, "run-ui") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("ADVANCED_EDITOR", "Error launching main app: " + ex.Message);
        }
        
        Environment.Exit(0);
    }
}

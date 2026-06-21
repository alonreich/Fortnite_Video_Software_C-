using Avalonia.Controls;
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

        var timelineCanvas = this.FindControl<Canvas>("TimelineCanvas");
        if (timelineCanvas != null)
        {
            timelineCanvas.AddHandler(DragDrop.DropEvent, OnFilesDroppedOnTimeline);
        }
    }

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
                    Name = Path.GetFileName(path),
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
                        Name = Path.GetFileName(path),
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
                            Text = Path.GetFileName(path),
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

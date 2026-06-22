using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using FortniteVideoSoftware.Core.Media;
using System.Diagnostics;
using System.IO;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public partial class VideoMergerWindow : Window
{
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    public ObservableCollection<string> VideoQueue { get; } = new();

    private MusicWizardResult? _musicResult;

    private Avalonia.Threading.DispatcherTimer? _playbackTimer;

    public VideoMergerWindow()
    {
        InitializeComponent();

        // Smart OS Theme Detection
        if (Avalonia.Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Styling.ThemeVariant.Light)
        {
            var mainBorder = this.FindControl<Avalonia.Controls.Border>("MainBorder");
            var titleBarBorder = this.FindControl<Avalonia.Controls.Border>("TitleBarBorder");
            
            if (mainBorder != null) mainBorder.BorderBrush = Avalonia.Media.Brush.Parse("#334155");
            if (titleBarBorder != null) titleBarBorder.Background = Avalonia.Media.Brush.Parse("#0f172a");
        }

        this.Loaded += (s, e) => InitializeMpv();
        
        _playbackTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        this.Loaded += async (s, e) => {
            await WindowBoundsHelper.LoadBoundsAsync(this, "VideoMergerBounds");
        };

        this.Closing += (s, e) => {
            WindowBoundsHelper.SaveBoundsSync(this, "VideoMergerBounds");
        };

        var videoList = this.FindControl<ListBox>("VideoList");
        if (videoList != null)
        {
            videoList.ItemsSource = VideoQueue;
            videoList.SelectionChanged += (s, e) =>
            {
                if (videoList.SelectedItem is string path && _videoHost?.IpcClient != null)
                {
                    _ = _videoHost?.IpcClient?.LoadFileAsync(path);
                    _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");
                }
            };
            videoList.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, VideoList_DragOver);
            videoList.AddHandler(Avalonia.Input.DragDrop.DropEvent, VideoList_Drop);
            videoList.PointerPressed += VideoList_PointerPressed;
            videoList.PointerMoved += VideoList_PointerMoved;
            videoList.PointerReleased += VideoList_PointerReleased;
        }

        var returnBtn = this.FindControl<Button>("ReturnButton");
        if (returnBtn != null)
            returnBtn.Click += (s, e) => ReturnToMainApp();

        var playPauseBtn = this.FindControl<Button>("PlayPauseButton");
        if (playPauseBtn != null)
        {
            playPauseBtn.Click += (s, e) =>
            {
                if (_videoHost?.IpcClient != null)
                {
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
                }
            };
        }

        var addBtn = this.FindControl<Button>("AddVideoButton");
        if (addBtn != null)
        {
            addBtn.Click += async (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Add Video in Video Merger.");
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Add Videos to Merger",
                    AllowMultiple = true,
                    FileTypeFilter = new[] { new FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
                });

                foreach (var file in files)
                {
                    VideoQueue.Add(file.Path.LocalPath);
                }
            };
        }

        var addMusicBtn = this.FindControl<Button>("AddMusicButton");
        if (addMusicBtn != null)
        {
            addMusicBtn.Click += async (s, e) =>
            {
                var wizard = new MusicWizardWindow();
                await wizard.ShowDialog(this);

                if (wizard.Result != null)
                {
                    _musicResult = wizard.Result;
                    RuntimeLog.Info("MERGER", $"User added music via wizard: {_musicResult.MusicFilePath}, offset={_musicResult.OffsetSeconds}");
                    addMusicBtn.Content = "🎵 " + System.IO.Path.GetFileName(_musicResult.MusicFilePath);
                }
            };
        }

        var removeBtn = this.FindControl<Button>("RemoveVideoButton");
        if (removeBtn != null)
        {
            removeBtn.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Remove Video in Video Merger.");
                int index = videoList?.SelectedIndex ?? -1;
                if (index >= 0 && index < VideoQueue.Count)
                {
                    VideoQueue.RemoveAt(index);
                }
            };
        }

        var moveUpBtn = this.FindControl<Button>("MoveUpButton");
        if (moveUpBtn != null)
        {
            moveUpBtn.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Move Up in Video Merger.");
                int index = videoList?.SelectedIndex ?? -1;
                if (index > 0)
                {
                    string item = VideoQueue[index];
                    VideoQueue.RemoveAt(index);
                    VideoQueue.Insert(index - 1, item);
                    if (videoList != null) videoList.SelectedIndex = index - 1;
                }
            };
        }

        var moveDownBtn = this.FindControl<Button>("MoveDownButton");
        if (moveDownBtn != null)
        {
            moveDownBtn.Click += (s, e) =>
            {
                RuntimeLog.Info("UI", "User clicked Move Down in Video Merger.");
                int index = videoList?.SelectedIndex ?? -1;
                if (index >= 0 && index < VideoQueue.Count - 1)
                {
                    string item = VideoQueue[index];
                    VideoQueue.RemoveAt(index);
                    VideoQueue.Insert(index + 1, item);
                    if (videoList != null) videoList.SelectedIndex = index + 1;
                }
            };
        }

        var mergeBtn = this.FindControl<Button>("MergeButton");
        if (mergeBtn != null)
        {
            mergeBtn.Click += async (s, e) =>
            {
                if (VideoQueue.Count == 0) return;
                
                RuntimeLog.Info("MERGER", $"Starting merge with {VideoQueue.Count} files.");
                mergeBtn.IsEnabled = false;
                mergeBtn.Content = "MERGING...";

                await Task.Yield();

                try
                {
                    var worker = new MergerWorker
                    {
                        InputFiles = new List<string>(VideoQueue)
                    };

                    if (_musicResult != null)
                    {
                        worker.MusicTrack = new MusicTrack(_musicResult.MusicFilePath, _musicResult.OffsetSeconds, 9999.0);
                        if (_musicResult.EnableDucking)
                        {
                            worker.MusicConfig = new System.Text.Json.Nodes.JsonObject
                            {
                                ["ducking_threshold"] = 0.15,
                                ["ducking_ratio"] = 2.5
                            };
                        }
                    }
                    
                    OverlayLayer.StartOverlay();

                    worker.ProgressUpdate += percent =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            mergeBtn.Content = $"MERGING... {percent}%";
                        });
                    };

                    worker.Finished += async (success, msg) =>
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            OverlayLayer.StopOverlay();
                            mergeBtn.IsEnabled = true;
                            mergeBtn.Content = "MERGE VIDEOS";

                            if (success)
                            {
                                RuntimeLog.Info("MERGER", "Merge completed successfully.");
                                var dlg = new FortniteVideoSoftware.App.Controls.FinishedDialogWindow();
                                dlg.SetOutputPath(msg);
                                await dlg.ShowDialog(this);
                                if (dlg.DialogResult == 1)
                                {
                                    Close();
                                }
                            }
                            else
                            {
                                RuntimeLog.Fail("MERGER", "Merge failed: " + msg);
                            }
                        });
                    };

                    await worker.RunAsync();
                }
                catch (Exception ex)
                {
                    OverlayLayer.StopOverlay();
                    RuntimeLog.Fail("MERGER", "Merge error: " + ex.Message);
                    mergeBtn.IsEnabled = true;
                    mergeBtn.Content = "MERGE VIDEOS";
                }
            };
        }

        UpdateTooltips();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, MergerKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void UpdateTooltips()
    {
        var kb = FortniteVideoSoftware.Core.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playBtn = this.FindControl<Button>("PlayPauseButton");
        if (playBtn != null) ToolTip.SetTip(playBtn, $"Play or pause the video ({kb.PlayPause})");
    }

    private void MergerKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Input.FocusManager.Instance?.Current?.GetLogicalParent() is TextBox or NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.Core.Infrastructure.SettingsManager.Instance.KeyBinds;

        if (e.Key == kb.PlayPause)
        {
            var btn = this.FindControl<Button>("PlayPauseButton");
            if (_videoHost?.IpcClient != null)
            {
                bool isPaused = _videoHost.IpcClient.IsPaused;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", isPaused ? "no" : "yes");
            }
            e.Handled = true;
        }
        else if (e.Key == kb.SeekForward)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", 5);
            e.Handled = true;
        }
        else if (e.Key == kb.SeekBackward)
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("seek", -5);
            e.Handled = true;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
    
    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;
        
        var playPauseBtn = this.FindControl<Button>("PlayPauseButton");
        if (playPauseBtn != null)
        {
            if (_videoHost.IpcClient.IsPaused)
            {
                if (playPauseBtn.Content?.ToString() != "▶")
                    playPauseBtn.Content = "▶";
            }
            else
            {
                if (playPauseBtn.Content?.ToString() != "⏸")
                    playPauseBtn.Content = "⏸";
            }
        }
        
        double time = _videoHost.IpcClient.CurrentTime;
        double dur = _videoHost.IpcClient.Duration;
        
        var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (timeElapsed != null) timeElapsed.Text = TimeSpan.FromSeconds(time).ToString("hh\\:mm\\:ss");
        
        var timeRemaining = this.FindControl<TextBlock>("TimeRemaining");
        if (timeRemaining != null) timeRemaining.Text = "-" + TimeSpan.FromSeconds(Math.Max(0, dur - time)).ToString("hh\\:mm\\:ss");
        
        var slider = this.FindControl<Slider>("TimelineSlider");
        if (slider != null && dur > 0)
        {
            // Simple slider update
            slider.Value = (time / dur) * 100.0;
        }
    }

    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        if (_videoHost != null)
        {
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = "mpv.exe";
            await _videoHost.StartMpvProcessAsync(mpvPath);
            if (_videoHost.IpcClient != null)
            {
                _videoHost.IpcClient.SeekCompleted += () => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                        _isSeeking = false;
                        if (_nextSeekTarget.HasValue) {
                            double target = _nextSeekTarget.Value;
                            _nextSeekTarget = null;
                            await SeekInternal(target);
                        }
                    });
                };
            }
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) { _nextSeekTarget = time; return; }
        _isSeeking = true;
        if (_videoHost?.IpcClient != null) await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
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
            RuntimeLog.Fail("MERGER", "Error launching main app: " + ex.Message);
        }
        
        Environment.Exit(0);
    }

    public void OnSuccessAction(string action)
    {
        if (action == "whatsapp")
        {
            Process.Start(new ProcessStartInfo("cmd", "/c start whatsapp://send?text=CheckOutThisVideo") { CreateNoWindow = true });
        }
        else if (action == "folder")
        {
            Process.Start(new ProcessStartInfo("explorer.exe", ".") { CreateNoWindow = true });
        }
        
        Environment.Exit(0);
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        try { WindowBoundsHelper.SaveBoundsSync(this, "VideoMergerBounds"); } catch {}
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_videoHost?.IpcClient != null)
        {
            
        }
        base.OnClosed(e);
    }

    private Avalonia.Point? _videoDragStartPoint;
    private bool _isVideoDragging;

    private void VideoList_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Avalonia.Controls.Control);
        if (point.Properties.IsLeftButtonPressed)
        {
            _videoDragStartPoint = point.Position;
        }
    }

    private async void VideoList_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_videoDragStartPoint.HasValue && !_isVideoDragging)
        {
            var point = e.GetCurrentPoint(sender as Avalonia.Controls.Control);
            var diff = point.Position - _videoDragStartPoint.Value;
            if (Math.Abs(diff.X) > 3 || Math.Abs(diff.Y) > 3)
            {
                var source = e.Source as Avalonia.Controls.Control;
                if (source?.DataContext is string itemText)
                {
                    _isVideoDragging = true;
                    var dragData = new Avalonia.Input.DataObject();
                    dragData.Set("VideoItem", itemText);
                    await Avalonia.Input.DragDrop.DoDragDrop(e, dragData, Avalonia.Input.DragDropEffects.Move);
                    _videoDragStartPoint = null;
                    _isVideoDragging = false;
                }
            }
        }
    }

    private void VideoList_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _videoDragStartPoint = null;
    }

    private void VideoList_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        if (e.Data.Contains("VideoItem"))
        {
            e.DragEffects = Avalonia.Input.DragDropEffects.Move;
        }
        else
        {
            e.DragEffects = Avalonia.Input.DragDropEffects.None;
        }
    }

    private void VideoList_Drop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        if (e.Data.Contains("VideoItem"))
        {
            string? itemToMove = e.Data.Get("VideoItem") as string;
            var destItem = (e.Source as Avalonia.Controls.Control)?.DataContext as string;

            if (itemToMove != null && destItem != null && destItem != itemToMove)
            {
                int oldIndex = VideoQueue.IndexOf(itemToMove);
                int newIndex = VideoQueue.IndexOf(destItem);

                if (oldIndex >= 0 && newIndex >= 0)
                {
                    VideoQueue.RemoveAt(oldIndex);
                    VideoQueue.Insert(newIndex, itemToMove);
                }
            }
        }
    }
}

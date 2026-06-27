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

    private bool _isSafeToClose = false;

    private Avalonia.Threading.DispatcherTimer? _playbackTimer;

    public VideoMergerWindow()
    {
        InitializeComponent();

        this.Loaded += (s, e) => InitializeMpv();
        
        _playbackTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        this.Loaded += async (s, e) => {
            await WindowBoundsHelper.LoadBoundsAsync(this, "VideoMergerBounds");
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
                UpdateQueueState();
            };
            videoList.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, VideoList_DragOver);
            videoList.AddHandler(Avalonia.Input.DragDrop.DropEvent, VideoList_Drop);
            videoList.PointerPressed += VideoList_PointerPressed;
            videoList.PointerMoved += VideoList_PointerMoved;
            videoList.PointerReleased += VideoList_PointerReleased;
        }
        VideoQueue.CollectionChanged += (_, _) => UpdateQueueState();


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
                
                var options = new FilePickerOpenOptions
                {
                    Title = "Add Videos to Merger",
                    AllowMultiple = true,
                    FileTypeFilter = new[] { new FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
                };

                var paths = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault();
                try
                {
                    if (System.IO.File.Exists(paths.SessionStateFile))
                    {
                        var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(paths.SessionStateFile));
                        if (state != null && state["MergerUploadDirectory"]?.ToString() is string startPath && System.IO.Directory.Exists(startPath))
                        {
                            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
                        }
                    }
                }
                catch { }

                var files = await StorageProvider.OpenFilePickerAsync(options);

                if (files.Count > 0)
                {
                    try
                    {
                        var state = System.IO.File.Exists(paths.SessionStateFile) 
                            ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(System.IO.File.ReadAllText(paths.SessionStateFile)) ?? new System.Text.Json.Nodes.JsonObject()
                            : new System.Text.Json.Nodes.JsonObject();
                        state["MergerUploadDirectory"] = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath);
                        System.IO.File.WriteAllText(paths.SessionStateFile, state.ToJsonString());
                    }
                    catch { }
                }

                foreach (var file in files)
                {
                    VideoQueue.Add(file.Path.LocalPath);
                }
                SetQueueStatus(files.Count > 0 ? $"{files.Count} video file(s) added." : "No files selected.", false);
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
                    addMusicBtn.Content = "MUSIC ADDED";
                    ToolTip.SetTip(addMusicBtn, "Music: " + System.IO.Path.GetFileName(_musicResult.MusicFilePath));
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
                    if (videoList != null) videoList.SelectedIndex = Math.Min(index, VideoQueue.Count - 1);
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
                if (VideoQueue.Count < 2)
                {
                    UpdateQueueState();
                    SetQueueStatus("Add at least two videos before merging.", true);
                    return;
                }
                 
                RuntimeLog.Info("MERGER", $"Starting merge with {VideoQueue.Count} files.");
                mergeBtn.IsEnabled = false;
                mergeBtn.Content = "MERGING...";
                SetQueueStatus("Merge in progress. Keep this window open.", false);

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
                            UpdateQueueState();

                            if (success)
                            {
                                RuntimeLog.Info("MERGER", "Merge completed successfully.");
                                SetQueueStatus("Merge completed successfully.", false);
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
                                SetQueueStatus("Merge failed: " + msg, true);
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
                    UpdateQueueState();
                    SetQueueStatus("Merge error: " + ex.Message, true);
                }
            };
        }
 
        UpdateTooltips();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, MergerKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AttachTitleBarDrag();
        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        var videoList = this.FindControl<ListBox>("VideoList");
        int selectedIndex = videoList?.SelectedIndex ?? -1;
        int count = VideoQueue.Count;

        var mergeBtn = this.FindControl<Button>("MergeButton");
        if (mergeBtn != null)
        {
            mergeBtn.IsEnabled = count >= 2;
            ToolTip.SetTip(mergeBtn, count >= 2 ? "Merge all listed videos together" : "Add at least two videos to enable merging");
        }

        var removeBtn = this.FindControl<Button>("RemoveVideoButton");
        if (removeBtn != null) removeBtn.IsEnabled = selectedIndex >= 0;

        var moveUpBtn = this.FindControl<Button>("MoveUpButton");
        if (moveUpBtn != null) moveUpBtn.IsEnabled = selectedIndex > 0;

        var moveDownBtn = this.FindControl<Button>("MoveDownButton");
        if (moveDownBtn != null) moveDownBtn.IsEnabled = selectedIndex >= 0 && selectedIndex < count - 1;

        var emptyText = this.FindControl<TextBlock>("EmptyQueueText");
        if (emptyText != null) emptyText.IsVisible = count == 0;

        if (count == 0)
            SetQueueStatus("Waiting for videos.", false);
        else if (count == 1)
            SetQueueStatus("Add one more video to enable merging.", false);
        else
            SetQueueStatus($"Ready to merge {count} videos.", false);
    }

    private void SetQueueStatus(string message, bool isError)
    {
        var status = this.FindControl<TextBlock>("QueueStatusText");
        if (status == null) return;
        status.Text = message;
        status.Foreground = isError
            ? Avalonia.Media.Brush.Parse("#fecaca")
            : Avalonia.Media.Brush.Parse("#94a3b8");
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Avalonia.Controls.Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    BeginMoveDrag(e);
                }
            };
        }
    }

    private void UpdateTooltips()
    {
        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playBtn = this.FindControl<Button>("PlayPauseButton");
        if (playBtn != null) ToolTip.SetTip(playBtn, $"Play or pause the video ({kb.PlayPause})");
    }

    private void MergerKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;

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

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Polygon>("PlayIcon");
        var pauseIcon = this.FindControl<StackPanel>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
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
        RuntimeLog.Info("MERGER", "Returning to parent main window.");
        Close();
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
        
        Close();
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        // If the background work is done, allow the window to close normally
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        // STOP the synchronous UI-blocking close
        e.Cancel = true;

        // Hide the window instantly so the app feels incredibly fast and responsive
        this.Hide();

        try
        {
            // Perform the heavy Mutex locking and file I/O ASYNCHRONOUSLY
            await WindowBoundsHelper.SaveBoundsAsync(this, "VideoMergerBounds");

            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("stop");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            // Mark as safe and programmatically re-trigger the close
            _isSafeToClose = true;
            this.Close();
        }
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

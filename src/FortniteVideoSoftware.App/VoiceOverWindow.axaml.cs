using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

public partial class VoiceOverWindow : Window
{
    private MpvVideoView? _videoHost;
    private VoiceRecorder? _recorder;
    private string _videoPath = "";
    private string _outputWavPath = "";

    private bool _isRecording = false;
    private bool _isMpvReady = false;
    private bool _isClosing = false;
    private DispatcherTimer _timer;

    private List<float> _waveformSamples = new();

    private string? _tempThumbPath;
    private string? _tempWavePath;
    private System.Threading.CancellationTokenSource? _generationCts;

    private double _trimStartSec = 0;
    private double _trimEndSec = 0;

    private class VoiceOverSession
    {
        public string WavPath { get; set; } = "";
        public double StartSec { get; set; }
        public double EndSec { get; set; }
    }
    private List<VoiceOverSession> _sessions = new();
    private VoiceOverSession? _currentSession;

    public VoiceOverResult? Result { get; private set; }

    public class VoiceOverResult
    {
        public string? VoiceOverWavPath { get; set; }
        public double VoiceOverStartTimestampSec { get; set; }
        public bool MuteMale { get; set; }
        public bool MuteFemale { get; set; }
        public bool MuteChild { get; set; }
    }

    public VoiceOverWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        Closing += OnWindowClosing;
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing) return;
        e.Cancel = true;
        _isClosing = true;

        await WindowBoundsHelper.SaveBoundsAsync(this, "VoiceOverWindowBounds");
        Close();
    }

    public VoiceOverWindow(string videoPath, double startPosSec, double trimStartMs = 0, double trimEndMs = 0) : this()
    {
        _videoPath = videoPath;
        _trimStartSec = trimStartMs / 1000.0;
        _trimEndSec = trimEndMs / 1000.0;
        _outputWavPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"voiceover_{Guid.NewGuid()}.wav");

        _videoHost = this.FindControl<MpvVideoView>("VideoHost");

        _recorder = new VoiceRecorder(_outputWavPath);
        _recorder.VolumeChanged += OnVolumeChanged;

        this.FindControl<Button>("MicRecordButton")!.Click += ToggleRecord;
        this.FindControl<Button>("PlayPauseButton")!.Click += ToggleRecord;
        this.FindControl<Button>("ApplyButton")!.Click += (s, e) => ApplyAndClose();
        this.FindControl<Button>("CancelButton")!.Click += (s, e) => Close();

        _timer.Tick += (s, e) => {
            UpdateWaveformUI();
            UpdatePlayPauseIconUI();
            UpdatePlayheadUI();
        };
        _timer.Start();

        KeyDown += OnKeyDownHandler;

        var rulerGrid = this.FindControl<Canvas>("TimelineRulerCanvas");
        if (rulerGrid != null)
        {
            rulerGrid.PointerPressed += (s, e) => SeekTimelineFromPointer(e, rulerGrid);
            rulerGrid.PointerMoved += (s, e) =>
            {
                if (e.GetCurrentPoint(rulerGrid).Properties.IsLeftButtonPressed)
                {
                    SeekTimelineFromPointer(e, rulerGrid);
                }
            };
        }

        var thumbnailGrid = this.FindControl<Grid>("ThumbnailLaneGrid");
        if (thumbnailGrid != null)
        {
            thumbnailGrid.PointerPressed += (s, e) => SeekTimelineFromPointer(e, thumbnailGrid);
            thumbnailGrid.PointerMoved += (s, e) =>
            {
                if (e.GetCurrentPoint(thumbnailGrid).Properties.IsLeftButtonPressed)
                {
                    SeekTimelineFromPointer(e, thumbnailGrid);
                }
            };
        }

        Loaded += async (_, _) =>
        {
            await WindowBoundsHelper.LoadBoundsAsync(this, "VoiceOverWindowBounds");
            if (_videoHost != null)
            {
                string mpvPath = ResolveBinaryPath("mpv.exe", "frontend");
                await _videoHost.StartMpvProcessAsync(mpvPath);
                
                if (_videoHost.IpcClient != null)
                {
                    await _videoHost.IpcClient.LoadFileAsync(_videoPath);
                    await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                    
                    double initialPos = startPosSec;
                    if (_trimStartSec > 0 || _trimEndSec > 0)
                    {
                         await _videoHost.IpcClient.SetPropertyAsync("ab-loop-a", _trimStartSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                         if (_trimEndSec > 0)
                         {
                             await _videoHost.IpcClient.SetPropertyAsync("ab-loop-b", _trimEndSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                         }
                         if (initialPos < _trimStartSec) initialPos = _trimStartSec;
                         if (_trimEndSec > 0 && initialPos > _trimEndSec) initialPos = _trimStartSec;
                    }

                    if (initialPos > 0)
                        await _videoHost.IpcClient.SetPropertyAsync("time-pos", initialPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    
                    _isMpvReady = true;
                }
                
                _ = RunFrequencyProber();
                _ = GenerateLanesAsync();
            }
        };
    }

    private static string ResolveBinaryPath(string fileName, string preferredSubdirectory)
    {
        string processDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));
        
        string preferredPath = System.IO.Path.Combine(baseDir, preferredSubdirectory, fileName);
        if (System.IO.File.Exists(preferredPath)) return preferredPath;
        
        string rootPath = System.IO.Path.Combine(baseDir, fileName);
        if (System.IO.File.Exists(rootPath)) return rootPath;
        
        string debugPath = System.IO.Path.Combine(processDir, fileName);
        if (System.IO.File.Exists(debugPath)) return debugPath;

        if (System.IO.File.Exists(sourceRootCandidate)) return sourceRootCandidate;

        return fileName;
    }



    private async Task GenerateLanesAsync()
    {
        if (_videoHost?.IpcClient == null) return;
        
        while (_videoHost.IpcClient.Duration <= 0)
        {
            await Task.Delay(100);
            if (_isClosing) return;
        }

        double videoDuration = _videoHost.IpcClient.Duration;
        double durationSec = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (durationSec <= 0) durationSec = videoDuration;
        string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");

        _generationCts = new System.Threading.CancellationTokenSource();
        var token = _generationCts.Token;

        var thumbOverlay = this.FindControl<Border>("ThumbLoadingOverlay");
        var waveOverlay = this.FindControl<Border>("WaveformLoadingOverlay");
        var thumbLane = this.FindControl<Image>("ThumbnailLaneImage");
        var waveLane = this.FindControl<Image>("WaveformLaneImage");

        if (thumbOverlay != null) thumbOverlay.IsVisible = true;
        if (waveOverlay != null) waveOverlay.IsVisible = true;

        var thumbTask = Task.Run(async () =>
        {
            string? tempPng = null;
            System.Diagnostics.Process? process = null;
            try
            {
                tempPng = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fvs_thumb_{Guid.NewGuid():N}.png");
                double fps = 16.0 / (durationSec > 0 ? durationSec : 10);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -hide_banner -loglevel error -ss {_trimStartSec.ToString(System.Globalization.CultureInfo.InvariantCulture)} -t {durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{_videoPath}\" -vf \"fps={fps.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)},scale=-1:60,tile=15x1\" -frames:v 1 \"{tempPng}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process = System.Diagnostics.Process.Start(psi);
                if (process != null) await process.WaitForExitAsync(token);
                if (process?.ExitCode == 0 && System.IO.File.Exists(tempPng)) return tempPng;
            }
            catch { }
            return null;
        });

        var waveTask = Task.Run(async () =>
        {
            try
            {
                return await FortniteVideoSoftware.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
                        ffmpeg, _videoPath, 1200, 60, _trimStartSec, durationSec, token);
            }
            catch { }
            return null;
        });

        string? thumbPath = await thumbTask;
        string? wavePath = await waveTask;

        if (token.IsCancellationRequested) return;

        if (thumbPath != null && thumbLane != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(thumbPath);
                thumbLane.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                _tempThumbPath = thumbPath;
            }
            catch { }
        }
        if (thumbOverlay != null) thumbOverlay.IsVisible = false;

        if (wavePath != null && waveLane != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(wavePath);
                waveLane.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                _tempWavePath = wavePath;
            }
            catch { }
        }
        if (waveOverlay != null) waveOverlay.IsVisible = false;
    }

    private void UpdatePlayPauseIconUI()
    {
        if (_videoHost?.IpcClient == null) return;
        bool isPaused = _videoHost.IpcClient.IsPaused;
        var playIcon = this.FindControl<Polygon>("PlayIcon");
        var pauseIcon = this.FindControl<StackPanel>("PauseIcon");
        if (playIcon != null) playIcon.IsVisible = isPaused;
        if (pauseIcon != null) pauseIcon.IsVisible = !isPaused;
    }

    private void UpdatePlayheadUI()
    {
        if (_videoHost?.IpcClient == null) return;
        double currentTime = _videoHost.IpcClient.CurrentTime;
        double videoDuration = _videoHost.IpcClient.Duration;
        if (videoDuration <= 0) return;
        
        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;

        double relativeTime = currentTime - _trimStartSec;
        double fraction = Math.Clamp(relativeTime / effectiveDuration, 0, 1);

        var ruler = this.FindControl<Canvas>("TimelineRulerCanvas");
        if (ruler != null && ruler.Bounds.Width > 0)
        {
            ruler.Children.Clear();
            double width = ruler.Bounds.Width;
            
            foreach (var session in _sessions)
            {
                double startFrac = (session.StartSec - _trimStartSec) / effectiveDuration;
                double endFrac = (session.EndSec - _trimStartSec) / effectiveDuration;
                double x1 = Math.Clamp(startFrac * width, 0, width);
                double x2 = Math.Clamp(endFrac * width, 0, width);
                if (x2 > x1)
                {
                    ruler.Children.Add(new Avalonia.Controls.Shapes.Rectangle
                    {
                        Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(102, 255, 0, 0)),
                        Width = x2 - x1,
                        Height = ruler.Bounds.Height,
                        [Avalonia.Controls.Canvas.LeftProperty] = x1,
                        [Avalonia.Controls.Canvas.TopProperty] = 0
                    });
                }
            }

            if (_isRecording && _currentSession != null)
            {
                double startFrac = (_currentSession.StartSec - _trimStartSec) / effectiveDuration;
                double endFrac = fraction;
                double x1 = Math.Clamp(startFrac * width, 0, width);
                double x2 = Math.Clamp(endFrac * width, 0, width);
                if (x2 > x1)
                {
                    ruler.Children.Add(new Avalonia.Controls.Shapes.Rectangle
                    {
                        Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(102, 255, 0, 0)),
                        Width = x2 - x1,
                        Height = ruler.Bounds.Height,
                        [Avalonia.Controls.Canvas.LeftProperty] = x1,
                        [Avalonia.Controls.Canvas.TopProperty] = 0
                    });
                }
            }

            double caretX = fraction * width;
            var caret = new Avalonia.Controls.Shapes.Polygon
            {
                Points = new List<Avalonia.Point> { new Avalonia.Point(-6, 0), new Avalonia.Point(6, 0), new Avalonia.Point(0, 10) },
                Fill = Avalonia.Media.Brushes.Red,
                [Avalonia.Controls.Canvas.LeftProperty] = caretX,
                [Avalonia.Controls.Canvas.TopProperty] = 10
            };
            ruler.Children.Add(caret);
            
            ruler.Children.Add(new Avalonia.Controls.Shapes.Line {
                StartPoint = new Avalonia.Point(0, 10),
                EndPoint = new Avalonia.Point(0, 20),
                Stroke = Avalonia.Media.Brushes.Red,
                StrokeThickness = 2,
                [Avalonia.Controls.Canvas.LeftProperty] = caretX
            });
        }

        var thumbCanvas = this.FindControl<Grid>("ThumbnailLaneGrid");
        if (thumbCanvas != null && thumbCanvas.Bounds.Width > 0)
        {
            var thumbPlayhead = this.FindControl<Line>("ThumbPlayheadLine");
            if (thumbPlayhead != null)
            {
                double x = fraction * thumbCanvas.Bounds.Width;
                thumbPlayhead.StartPoint = new Point(x, 0);
                thumbPlayhead.EndPoint = new Point(x, thumbCanvas.Bounds.Height);
            }
        }

        var waveCanvas = this.FindControl<Grid>("WaveformLaneGrid");
        if (waveCanvas != null && waveCanvas.Bounds.Width > 0)
        {
            var wavePlayhead = this.FindControl<Line>("WavePlayheadLine");
            if (wavePlayhead != null)
            {
                double x = fraction * waveCanvas.Bounds.Width;
                wavePlayhead.StartPoint = new Point(x, 0);
                wavePlayhead.EndPoint = new Point(x, waveCanvas.Bounds.Height);
            }
        }
    }

    private async Task RunFrequencyProber()
    {
        var text = this.FindControl<TextBlock>("ProbingStatusText");
        var maleCb = this.FindControl<CheckBox>("MuteMaleCb");
        var femaleCb = this.FindControl<CheckBox>("MuteFemaleCb");
        var childCb = this.FindControl<CheckBox>("MuteChildCb");

        var result = await Task.Run(() => FrequencyProber.Probe(_videoPath, 15));

        Dispatcher.UIThread.Post(() =>
        {
            if (text != null) text.Text = "Probing complete.";
            if (maleCb != null && result.HasAdultMale) maleCb.IsChecked = true;
            if (femaleCb != null && result.HasAdultFemale) femaleCb.IsChecked = true;
            if (childCb != null && result.HasChild) childCb.IsChecked = true;
        });
    }

    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Control timelineCanvas)
    {
        if (e.Handled) return;
        if (_videoHost?.IpcClient == null) return;
        double videoDuration = _videoHost.IpcClient.Duration;
        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;
        double width = timelineCanvas.Bounds.Width;
        if (effectiveDuration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double targetTime = _trimStartSec + (x / width) * effectiveDuration;
        _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", targetTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void OnKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.V)
        {
            ToggleRecord(null, null);
        }
    }

    private void ToggleRecord(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        if (!_isMpvReady) return;

        if (_isRecording)
        {
            StopRecordingAndPlayback();
        }
        else
        {
            StartRecordingAndPlayback();
        }
    }

    private void StartRecordingAndPlayback()
    {
        _isRecording = true;
        
        if (_recorder != null)
        {
            _recorder.VolumeChanged -= OnVolumeChanged;
            _recorder.Dispose();
        }
        _outputWavPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"voiceover_{Guid.NewGuid():N}.wav");
        
        _currentSession = new VoiceOverSession {
            WavPath = _outputWavPath,
            StartSec = _videoHost?.IpcClient?.CurrentTime ?? 0
        };

        _recorder = new VoiceRecorder(_outputWavPath);
        _recorder.VolumeChanged += OnVolumeChanged;
        
        _recorder.StartRecording();
        _waveformSamples.Clear();
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");

        var light = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("RecordingLight");
        var status = this.FindControl<TextBlock>("RecordingStatusText");
        if (light != null) light.Opacity = 1.0;
        if (status != null) { status.Text = "RECORDING"; status.Foreground = Avalonia.Media.Brushes.Red; }
        
        var btn = this.FindControl<Button>("MicRecordButton");
        if (btn != null && !btn.Classes.Contains("recording")) btn.Classes.Add("recording");
    }

    private void StopRecordingAndPlayback()
    {
        _isRecording = false;
        _recorder?.StopRecording();
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        if (_currentSession != null)
        {
            _currentSession.EndSec = _videoHost?.IpcClient?.CurrentTime ?? _currentSession.StartSec;
            _sessions.Add(_currentSession);
            _currentSession = null;
        }

        var light = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("RecordingLight");
        var status = this.FindControl<TextBlock>("RecordingStatusText");
        if (light != null) light.Opacity = 0.2;
        if (status != null) { status.Text = "PAUSED"; status.Foreground = Avalonia.Media.Brushes.White; }
        
        var btn = this.FindControl<Button>("MicRecordButton");
        if (btn != null) btn.Classes.Remove("recording");
    }

    private void OnVolumeChanged(object? sender, float volume)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var eq = this.FindControl<Rectangle>("EqMeter");
            if (eq != null)
            {
                eq.Width = Math.Min(150, volume * 300);
                if (volume > 0.8) eq.Fill = Brushes.Red;
                else if (volume > 0.5) eq.Fill = Brushes.Yellow;
                else eq.Fill = Brushes.LimeGreen;
            }

            if (_isRecording)
            {
                _waveformSamples.Add(volume);
            }
        });
    }

    private void UpdateWaveformUI()
    {
        var canvas = this.FindControl<Canvas>("WaveformCanvas");
        if (canvas == null || !_isRecording) return;

        canvas.Children.Clear();
        int maxLines = (int)(canvas.Bounds.Width / 2);
        
        int startIdx = Math.Max(0, _waveformSamples.Count - maxLines);
        double x = 0;

        for (int i = startIdx; i < _waveformSamples.Count; i++)
        {
            double height = Math.Max(2, _waveformSamples[i] * 60);
            var line = new Line
            {
                StartPoint = new Point(x, 30 - height / 2),
                EndPoint = new Point(x, 30 + height / 2),
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 1
            };
            canvas.Children.Add(line);
            x += 2;
        }
    }

    private async void ApplyAndClose()
    {
        StopRecordingAndPlayback();
        _recorder?.StopRecording();
        
        string? finalWav = null;
        double finalStart = 0;

        if (_sessions.Count == 1)
        {
            finalWav = _sessions[0].WavPath;
            finalStart = _sessions[0].StartSec;
        }
        else if (_sessions.Count > 1)
        {
            var btn = this.FindControl<Button>("ApplyButton");
            if (btn != null) { btn.IsEnabled = false; btn.Content = "MIXING..."; }
            finalWav = await MixSessionsAsync();
            finalStart = _trimStartSec;
        }
        else
        {
            if (System.IO.File.Exists(_outputWavPath))
            {
                finalWav = _outputWavPath;
                finalStart = 0;
            }
        }

        Result = new VoiceOverResult
        {
            VoiceOverWavPath = finalWav,
            VoiceOverStartTimestampSec = finalStart,
            MuteMale = this.FindControl<CheckBox>("MuteMaleCb")?.IsChecked == true,
            MuteFemale = this.FindControl<CheckBox>("MuteFemaleCb")?.IsChecked == true,
            MuteChild = this.FindControl<CheckBox>("MuteChildCb")?.IsChecked == true
        };

        Close();
    }

    private async Task<string?> MixSessionsAsync()
    {
        try
        {
            string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"voiceover_mix_{Guid.NewGuid():N}.wav");
            string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");
            
            var args = new System.Text.StringBuilder("-y -hide_banner -loglevel error ");
            foreach (var s in _sessions)
            {
                args.Append($"-i \"{s.WavPath}\" ");
            }
            
            args.Append("-filter_complex \"");
            for (int i = 0; i < _sessions.Count; i++)
            {
                int delayMs = (int)Math.Max(0, (_sessions[i].StartSec - _trimStartSec) * 1000);
                args.Append($"[{i}]adelay={delayMs}|{delayMs}[a{i}]; ");
            }
            for (int i = 0; i < _sessions.Count; i++)
            {
                args.Append($"[a{i}]");
            }
            args.Append($"amix=inputs={_sessions.Count}:normalize=0[out]\" -map \"[out]\" \"{outPath}\"");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var p = System.Diagnostics.Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();

            if (System.IO.File.Exists(outPath)) return outPath;
        }
        catch { }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _generationCts?.Cancel();
        
        if (_tempThumbPath != null && System.IO.File.Exists(_tempThumbPath))
        {
            try { System.IO.File.Delete(_tempThumbPath); } catch { }
        }
        if (_tempWavePath != null && System.IO.File.Exists(_tempWavePath))
        {
            try { System.IO.File.Delete(_tempWavePath); } catch { }
        }

        _timer.Stop();
        _recorder?.Dispose();
        _videoHost?.Dispose();
        base.OnClosed(e);
    }
}

import re
import sys
import os

files = [
    r"C:\Fortnite_Video_Software - C#\src\FortniteVideoSoftware.App\GranularSpeedEditorWindow.axaml.cs",
    r"C:\Fortnite_Video_Software - C#\src\FortniteVideoSoftware.App\VideoMergerWindow.axaml.cs"
]

def process_file(filepath):
    with open(filepath, "r", encoding="utf-8") as f:
        content = f.read()

    # 1. Replace fields
    content = re.sub(r"private nint _mpvHandle;\s*private MPVSafetyManager\? _safetyManager;", "private MpvVideoView? _videoHost;\n    private bool _isSeeking = false;\n    private double? _nextSeekTarget = null;", content)
    content = re.sub(r"private nint _mpvHandle;", "private MpvVideoView? _videoHost;\n    private bool _isSeeking = false;\n    private double? _nextSeekTarget = null;", content)
    content = re.sub(r"private MPVSafetyManager\? _safetyManager;", "", content)

    # 2. Replace GetMpvTime
    content = re.sub(r"private double GetMpvTime\(\)\s*\{[\s\S]*?return 0;\s*\}", "private double GetMpvTime() { return _videoHost?.IpcClient?.CurrentTime ?? 0.0; }", content)

    # 3. Replace pause
    content = re.sub(r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"pause",\s*"yes"\);', '_ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");', content)
    content = re.sub(r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"pause",\s*"no"\);', '_ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");', content)
    content = re.sub(r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*"set pause yes"\);', '_ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");', content)
    content = re.sub(r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*"cycle pause"\);', 'if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");', content)

    # 4. Replace loadfile
    content = re.sub(r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*\$"loadfile \\"\{_videoPath\.Replace\(\\"\\\\\\", \\"/\\"\)\}\\""\);', '_ = _videoHost?.IpcClient?.LoadFileAsync(_videoPath);', content)
    content = re.sub(r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*\$"loadfile \\"\{path\.Replace\(\\"\\\\\\", \\"/\\"\)\}\\""\);', '_ = _videoHost?.IpcClient?.LoadFileAsync(path);', content)

    # 5. InitializeMpv replacement
    # We will replace the content of InitializeMpv if it matches
    init_match = re.search(r'private void InitializeMpv\(\)\s*\{[\s\S]*?\}', content)
    if init_match:
        new_init = r"""    private async void InitializeMpv()
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
    }"""
        content = content.replace(init_match.group(0), new_init)

    # 6. Replace _mpvHandle checks
    content = content.replace("if (_mpvHandle == nint.Zero)", "if (_videoHost?.IpcClient == null)")
    content = content.replace("if (_mpvHandle != nint.Zero)", "if (_videoHost?.IpcClient != null)")
    content = content.replace("&& _mpvHandle != nint.Zero", "&& _videoHost?.IpcClient != null")

    # 7. Replace seek RequestSeek
    content = content.replace("_safetyManager?.RequestSeek(e.NewValue);", """
                    double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                    if (duration > 0)
                    {
                        double targetTime = (e.NewValue / 100.0) * duration;
                        _ = SeekInternal(targetTime);
                    }""")

    # 8. GetMpvDuration
    content = re.sub(r"private double GetMpvDuration\(\)\s*\{[\s\S]*?return 0;\s*\}", "private double GetMpvDuration() { return _videoHost?.IpcClient?.Duration ?? 0.0; }", content)

    # 9. Polling replacements for GranularSpeedEditor Window
    # Pause polling
    pause_poll = r"""        nint pausePtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "pause");
        if (pausePtr != nint.Zero)
        {
            string? pauseStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(pausePtr);
            MpvWrapper.mpv_free(pausePtr);
            
            var playPauseButton = this.FindControl<Button>("PlayPauseButton");
            if (playPauseButton != null)
            {
                if (pauseStr == "yes") playPauseButton.Content = "▶ PLAY";
                else playPauseButton.Content = "⏸ PAUSE";
            }
        }"""
    pause_repl = r"""        var playPauseButton = this.FindControl<Button>("PlayPauseButton");
        if (playPauseButton != null)
        {
            if (_videoHost?.IpcClient?.IsPaused == true) playPauseButton.Content = "▶ PLAY";
            else playPauseButton.Content = "⏸ PAUSE";
        }"""
    content = content.replace(pause_poll, pause_repl)

    time_poll = r"""        nint timePtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "time-pos");
        if (timePtr != nint.Zero)
        {
            string? timeStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(timePtr);
            MpvWrapper.mpv_free(timePtr);
            nint durPtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "duration");
            if (durPtr != nint.Zero)
            {
                string? durStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(durPtr);
                MpvWrapper.mpv_free(durPtr);
                
                if (double.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture, out double time) &&
                    double.TryParse(durStr, System.Globalization.CultureInfo.InvariantCulture, out double dur) && dur > 0)
                {
                    _isTimerUpdatingSlider = true;
                    if (timelineSlider != null) timelineSlider.Value = (time / dur) * 100.0;
                    _isTimerUpdatingSlider = false;
                    
                    var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
                    if (timeElapsed != null) timeElapsed.Text = TimeSpan.FromSeconds(time).ToString("hh\\:mm\\:ss\\.ff");
                    
                    if (time >= dur - 0.1)
                    {
                        MpvWrapper.mpv_command_string(_mpvHandle, "set pause yes");
                    }
                }
            }
        }"""
    time_repl = r"""        double time = _videoHost?.IpcClient?.CurrentTime ?? 0.0;
        double dur = _videoHost?.IpcClient?.Duration ?? 0.0;
        if (dur > 0)
        {
            _isTimerUpdatingSlider = true;
            if (timelineSlider != null) timelineSlider.Value = (time / dur) * 100.0;
            _isTimerUpdatingSlider = false;
            
            var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
            if (timeElapsed != null) timeElapsed.Text = TimeSpan.FromSeconds(time).ToString("hh\\:mm\\:ss\\.ff");
            
            if (time >= dur - 0.1)
            {
                _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
            }
        }"""
    content = content.replace(time_poll, time_repl)


    # 10. Stop and Destroy
    content = content.replace('MpvWrapper.mpv_command_string(_mpvHandle, "stop");', '_ = _videoHost?.IpcClient?.SendCommandAsync("stop");')
    content = content.replace('MpvWrapper.mpv_terminate_destroy(_mpvHandle);', '')

    with open(filepath, "w", encoding="utf-8") as f:
        f.write(content)

for f in files:
    process_file(f)

print("Migration completed for windows.")

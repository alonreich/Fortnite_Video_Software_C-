import re
import sys

filepath = r"C:\Fortnite_Video_Software - C#\src\FortniteVideoSoftware.App\MainWindow.axaml.cs"

with open(filepath, "r", encoding="utf-8") as f:
    content = f.read()

# 1. Replace fields
content = re.sub(
    r"private nint _mpvHandle;\s*private MPVSafetyManager\? _safetyManager;",
    "private MpvVideoView? _videoHost;\n    private bool _isSeeking = false;\n    private double? _nextSeekTarget = null;",
    content
)

# 2. Replace GetCurrentMpvTime
content = re.sub(
    r"private double GetCurrentMpvTime\(\)\s*\{[\s\S]*?return 0;\s*\}",
    "private double GetCurrentMpvTime() { return _videoHost?.IpcClient?.CurrentTime ?? 0.0; }",
    content
)

# 3. Replace simple set pause "yes" / "no"
content = re.sub(
    r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"pause",\s*"yes"\);',
    '_ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");',
    content
)
content = re.sub(
    r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"pause",\s*"no"\);',
    '_ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");',
    content
)
content = re.sub(
    r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*"set pause yes"\);',
    '_ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");',
    content
)

# 4. Replace loadfile
content = re.sub(
    r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*\$"loadfile \\"\{path\.Replace\(\\"\\\\\\", \\"\\\\\\\\\\"\)\}\\""\);',
    '_ = _videoHost?.IpcClient?.LoadFileAsync(path);',
    content
)

# 5. Replace "cycle pause"
content = re.sub(
    r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*"cycle pause"\);',
    'if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");',
    content
)

# 6. Replace speed
content = re.sub(
    r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"speed",\s*([^)]+)\);',
    r'_ = _videoHost?.IpcClient?.SetPropertyAsync("speed", \1);',
    content
)

# 7. Replace volume
content = re.sub(
    r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"volume",\s*([^)]+)\);',
    r'_ = _videoHost?.IpcClient?.SetPropertyAsync("volume", \1);',
    content
)

# 8. Replace audio properties
content = re.sub(
    r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*\$"audio-add(.*?)"\);',
    r'_ = _videoHost?.IpcClient?.SendCommandAsync("audio-add"\1);',
    content
)
content = re.sub(
    r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"audio-delay",\s*([^)]+)\);',
    r'_ = _videoHost?.IpcClient?.SetPropertyAsync("audio-delay", \1);',
    content
)

# 9. Replace Drawbox filter "vf"
content = re.sub(
    r'MpvWrapper\.mpv_set_property_string\(_mpvHandle,\s*"vf",\s*"([^"]*)"\);',
    r'_ = _videoHost?.IpcClient?.SetPropertyAsync("vf", "\1");',
    content
)

# 10. Replace InitializeMpv
old_init = r"""    private void InitializeMpv()
    {
        _mpvHandle = MpvWrapper.mpv_create();
        if (_mpvHandle != nint.Zero)
        {
            MpvWrapper.mpv_initialize(_mpvHandle);
            MpvWrapper.mpv_set_property_string(_mpvHandle, "keep-open", "yes");
            _safetyManager = new MPVSafetyManager(_mpvHandle);
            
            var videoHost = this.FindControl<MpvVideoView>("VideoHost");
            if (videoHost != null)
            {
                videoHost.AttachMpv(_mpvHandle);
            }
        }
    }"""
new_init = r"""    private async void InitializeMpv()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        if (_videoHost != null)
        {
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) 
            {
                mpvPath = "mpv.exe"; // Fallback to PATH
            }
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
        if (_isSeeking) {
            _nextSeekTarget = time;
            return;
        }
        _isSeeking = true;
        if (_videoHost?.IpcClient != null) {
            await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
        }
    }"""
content = content.replace(old_init, new_init)

# 11. Replace _mpvHandle checks with _videoHost != null checks
content = content.replace("if (_mpvHandle != nint.Zero)", "if (_videoHost?.IpcClient != null)")

# 12. Replace process export reading of duration
duration_read1 = r"""            nint durationPtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "duration");
            if (durationPtr != nint.Zero)
            {
                string? durationStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(durationPtr);
                MpvWrapper.mpv_free(durationPtr);
                double.TryParse(durationStr, System.Globalization.CultureInfo.InvariantCulture, out duration);
            }"""
duration_repl1 = r"""            duration = _videoHost?.IpcClient?.Duration ?? 0.0;"""
content = content.replace(duration_read1, duration_repl1)

duration_read2 = r"""        nint durationPtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "duration");
        if (durationPtr != nint.Zero)
        {
            string? durationStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(durationPtr);
            MpvWrapper.mpv_free(durationPtr);
            double.TryParse(durationStr, System.Globalization.CultureInfo.InvariantCulture, out duration);
        }"""
duration_repl2 = r"""        double duration = _videoHost?.IpcClient?.Duration ?? 0.0;"""
content = content.replace(duration_read2, duration_repl2)

duration_read3 = r"""                    nint durPtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "duration");
                    if (durPtr != nint.Zero)
                    {
                        string? durStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(durPtr);
                        MpvWrapper.mpv_free(durPtr);
                        double.TryParse(durStr, System.Globalization.CultureInfo.InvariantCulture, out duration);
                    }"""
duration_repl3 = r"""                    duration = _videoHost?.IpcClient?.Duration ?? 0.0;"""
content = content.replace(duration_read3, duration_repl3)

# 13. Replace pause polling
pause_poll = r"""        nint pausePtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "pause");
        if (pausePtr != nint.Zero)
        {
            string? pStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(pausePtr);
            MpvWrapper.mpv_free(pausePtr);
            if (pStr == "yes")
            {
                playPauseBtn.Content = "▶ PLAY";
            }
            else
            {
                playPauseBtn.Content = "⏸ PAUSE";
            }
        }"""
pause_repl = r"""        if (_videoHost?.IpcClient?.IsPaused == true)
        {
            playPauseBtn.Content = "▶ PLAY";
        }
        else
        {
            playPauseBtn.Content = "⏸ PAUSE";
        }"""
content = content.replace(pause_poll, pause_repl)

# 14. Replace time polling (Wait, GetCurrentMpvTime handles most, but some places do manual ptr read)
time_poll1 = r"""        nint timePtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "time-pos");
        if (timePtr != nint.Zero)
        {
            string? tStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(timePtr);
            MpvWrapper.mpv_free(timePtr);
            if (double.TryParse(tStr, System.Globalization.CultureInfo.InvariantCulture, out double cTime))
            {
                currentTime = cTime;
            }
        }"""
time_repl1 = r"""        double currentTime = GetCurrentMpvTime();"""
content = content.replace(time_poll1, time_repl1)

time_poll2 = r"""        nint ptr = MpvWrapper.mpv_get_property_string(_mpvHandle, "time-pos");
        if (ptr != nint.Zero)
        {
            string? str = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
            MpvWrapper.mpv_free(ptr);
            double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out timeMs);
            timeMs *= 1000.0;
        }"""
time_repl2 = r"""        timeMs = GetCurrentMpvTime() * 1000.0;"""
content = content.replace(time_poll2, time_repl2)

# 15. Replace eof polling
eof_poll = r"""        nint eofPtr = MpvWrapper.mpv_get_property_string(_mpvHandle, "eof-reached");
        if (eofPtr != nint.Zero)
        {
            string? eofStr = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(eofPtr);
            MpvWrapper.mpv_free(eofPtr);
            if (eofStr == "yes")
            {
                MpvWrapper.mpv_set_property_string(_mpvHandle, "pause", "yes");
                playPauseBtn.Content = "▶ PLAY";
            }
        }"""
eof_repl = r"""        if (_videoHost?.IpcClient?.IsEof == true)
        {
            _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
            playPauseBtn.Content = "▶ PLAY";
        }"""
content = content.replace(eof_poll, eof_repl)


# 16. Replace seek operations
# TimelineSlider PointerPressed:
# Actually wait, I need to wire up SeekInternal.
# TimelineSlider pointer changed:
# MpvWrapper.mpv_command_string(_mpvHandle, $"seek {pos.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} absolute");
seek_repl1 = r'MpvWrapper\.mpv_command_string\(_mpvHandle,\s*\$"seek \{([^}]+)\} absolute"\);'
content = re.sub(seek_repl1, r'_ = SeekInternal(\1);', content)


# 17. OnClosing and OnClosed
content = content.replace('MpvWrapper.mpv_command_string(_mpvHandle, "stop");', '_ = _videoHost?.IpcClient?.SendCommandAsync("stop");')
content = content.replace('_safetyManager?.Dispose();', '')
content = content.replace('MpvWrapper.mpv_terminate_destroy(_mpvHandle);', '')
content = content.replace('_mpvHandle = nint.Zero;', '')


with open(filepath, "w", encoding="utf-8") as f:
    f.write(content)
print("Migration completed.")

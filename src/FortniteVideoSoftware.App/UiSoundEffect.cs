using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FortniteVideoSoftware.App;

/// <summary>The six UI feedback cues. Routing lives in <see cref="UiSoundRouter"/>.</summary>
public enum UiCue
{
    /// <summary>48 ms tick. Default for any ordinary button.</summary>
    Click,
    /// <summary>79 ms blip. Trim in/out points.</summary>
    Mark,
    /// <summary>1.36 s. A tool window or sub-application is opening.</summary>
    Open,
    /// <summary>1.54 s. A destructive action was COMMITTED, or a window is closing.</summary>
    Close,
    /// <summary>1.39 s. A real export finished. Never fired from a button click — see AUDIO_02.</summary>
    Process,
    /// <summary>1.23 s. A failure surfaced to the user.</summary>
    Error
}

/// <summary>
/// UI sound engine.
///
/// ======================================================================================
/// AUDIT ROUND 5 — WHAT CHANGED AND WHY (do not regress any of these)
/// ======================================================================================
/// AUDIO_05  The old throttle was a single 50 ms window compared against ONE remembered
///           buffer reference. Three of the six clips are 1.36-1.54 s long, so 50 ms was 3%
///           of the sound: spam-clicking restarted the clip ~10x/second and you heard only
///           chopped attack transients. Worse, because it remembered only the PREVIOUS
///           buffer, alternating between two different buttons reset it every time and it
///           never engaged at all. Now: a PER-CUE timestamp table, with each cue's window
///           derived from that clip's REAL decoded duration (clamped 80-350 ms), plus a hard
///           cap on simultaneous voices.
/// AUDIO_06  There was no volume and no mute, in an app with a whole "Sound and Music"
///           settings tab. Every play now passes through SettingsManager (UiSoundsEnabled /
///           UiSoundVolume).
/// AUDIO_12  Was System.Media.SoundPlayer -> winmm PlaySound, which is a SINGLE global
///           channel: two UI sounds could not overlap, a new one truncated the one before
///           it, and it offered no volume control at all (which is why AUDIO_05/06 had no
///           clean fix inside the old engine). Now NAudio - already a shipped dependency,
///           already used in production by Core\Media\VoiceRecorder.cs
///           - with a real mixer, so cues layer instead of cutting each other off. The
///           System.Windows.Extensions package reference was dropped from the csproj.
/// AUDIO_13  The old cache was a ConditionalWeakTable keyed on static readonly byte[] fields.
///           Static fields live for the whole process, so nothing could EVER be collected: it
///           was a permanent six-entry dictionary wearing a weak-cache costume, and the six
///           IDisposable SoundPlayers were never disposed. Now a plain fixed array plus a
///           real <see cref="Shutdown"/> wired to the desktop lifetime Exit event.
/// AUDIO_14  SoundPlayer.Play() parses the WAV synchronously if not already loaded, so the
///           first press of each cue did that work on the UI thread - for three clips over
///           119 KB - and Stop()/Play() ran while holding a lock on the UI thread. Decoding
///           and device I/O now happen entirely on the thread pool; the UI thread only does
///           an Interlocked timestamp compare.
/// AUDIO_08  Clips moved from base64 string literals in this file to EmbeddedResource .wav
///           assets (see the csproj). This file went from 650 KB to a readable one.
/// AUDIO_01  <see cref="Suppress"/> exists so the Voice Over Studio can hard-mute the whole
///           engine while the microphone is live.
/// ======================================================================================
///
/// FAILURE POLICY: never throw and never let a sound break a user action. If there is no
/// output device (VM, headless, RDP with audio redirection off - all real deployment targets
/// for this app per the PATH B notes) the engine disables itself permanently after one logged
/// warning and every Play call becomes a no-op.
/// </summary>
public static class UiSoundEffect
{
    private const int CueCount = 6;

    /// <summary>Resource LogicalNames, indexed by <see cref="UiCue"/>. Set explicitly in the
    /// csproj so this lookup can never break from a RootNamespace or folder rename.</summary>
    private static readonly string[] ResourceNames =
    {
        "UiSound.click.wav",
        "UiSound.mark.wav",
        "UiSound.open.wav",
        "UiSound.close.wav",
        "UiSound.process.wav",
        "UiSound.error.wav"
    };


    /// <summary>Floor for the per-cue retrigger window. Below this a human cannot tell two
    /// plays apart anyway, and the 48 ms click would machine-gun.</summary>
    private const double MinThrottleMs = 80;

    /// <summary>Ceiling for the per-cue retrigger window. The old code let a 1.5 s clip restart
    /// every 50 ms; blocking for the FULL 1.5 s would be the opposite mistake and would swallow
    /// deliberate repeat presses. 350 ms kills mashing without feeling deaf.</summary>
    private const double MaxThrottleMs = 350;

    /// <summary>Hard cap on simultaneous voices. The mixer will happily layer 50 clips if a user
    /// leans on a button; this bounds CPU and heap under abuse.</summary>
    private const int MaxConcurrentVoices = 4;

    private static readonly long[] _lastPlayTicks = new long[CueCount];
    private static readonly double[] _throttleMs = new double[CueCount];
    private static readonly CachedSound?[] _sounds = new CachedSound?[CueCount];

    private static int _activeVoices;


    private static readonly object _engineLock = new();
    private static WaveOutEvent? _output;
    private static MixingSampleProvider? _mixer;
    private static VolumeSampleProvider? _volume;
    private static bool _engineFailed;
    private static bool _shutdown;

    /// <summary>Mixer format. All six assets are 44.1 kHz mono 16-bit, which
    /// <c>ToSampleProvider()</c> presents as IEEE-float 44.1 kHz mono.</summary>
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);


    private static int _suppressionDepth;

    /// <summary>
    /// AUDIO_01: hard-mutes the engine for the lifetime of the returned scope.
    ///
    /// The Voice Over Studio's record button is styled Danger-red, so the old dispatcher
    /// classified it as a cancel/close action and played a 1.542 s clip out of the speakers at
    /// the exact instant the microphone opened - bleeding the app's own UI sound into every
    /// take. Recording now wraps itself in one of these. Re-entrant and thread-safe.
    /// </summary>
    public static IDisposable Suppress()
    {
        Interlocked.Increment(ref _suppressionDepth);
        return new SuppressionScope();
    }

    private sealed class SuppressionScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _suppressionDepth);
            }
        }
    }


    public static void PlayClick() => Play(UiCue.Click);
    public static void PlayMark() => Play(UiCue.Mark);
    public static void PlayOpen() => Play(UiCue.Open);
    public static void PlayClose() => Play(UiCue.Close);

    /// <summary>
    /// AUDIO_02: fired when an export actually FINISHES (FinishedDialogWindow), not when the
    /// user presses PROCESS. The old dispatcher played this on any Success-classed button, so
    /// the "your video is done" fanfare went off at the START of an export and on eight
    /// unrelated buttons, while the real completion - the moment the user has walked away from
    /// the machine - was silent.
    /// </summary>
    public static void PlayProcess() => Play(UiCue.Process);

    /// <summary>
    /// AUDIO_03: was declared and never called from anywhere in src\, so a 108 KB clip shipped
    /// in the binary that was structurally impossible to hear while every failure in the suite
    /// happened in silence. Now invoked by ErrorDialogWindow, the single failure surface.
    /// </summary>
    public static void PlayError() => Play(UiCue.Error);

    /// <summary>
    /// Fire-and-forget. Safe from any thread. Returns immediately; all decoding and device work
    /// happens on the thread pool (AUDIO_14).
    /// </summary>
    public static void Play(UiCue cue)
    {
        int i = (int)cue;
        if (i < 0 || i >= CueCount) return;
        if (_shutdown || _engineFailed) return;
        if (Volatile.Read(ref _suppressionDepth) > 0) return;
        if (Volatile.Read(ref _activeVoices) >= MaxConcurrentVoices) return;

        if (!TryReadSettings(out float gain)) return;

        long now = DateTime.UtcNow.Ticks;
        double windowMs = _throttleMs[i] > 0 ? _throttleMs[i] : MinThrottleMs;
        long windowTicks = (long)(windowMs * TimeSpan.TicksPerMillisecond);

        long previous = Interlocked.Read(ref _lastPlayTicks[i]);
        if (previous != 0 && now - previous < windowTicks) return;
        if (Interlocked.CompareExchange(ref _lastPlayTicks[i], now, previous) != previous) return;

        Interlocked.Increment(ref _activeVoices);
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(new WaitCallback(static state =>
            {
                var s = (WorkItem)state!;
                PlayOnWorker(s.CueIndex, s.Gain);
            }), new WorkItem(i, gain));
        }
        catch
        {
            Interlocked.Decrement(ref _activeVoices);
        }
    }

    private sealed class WorkItem
    {
        public readonly int CueIndex;
        public readonly float Gain;
        public WorkItem(int cueIndex, float gain) { CueIndex = cueIndex; Gain = gain; }
    }

    /// <summary>
    /// AUDIO_06: single gate for the user's preference. Returns false when sounds are off.
    /// Never throws - settings may not be loaded yet during very early startup, in which case
    /// SettingsManager.Instance is a default-constructed AppSettings and sounds play at the
    /// default level.
    /// </summary>
    private static bool TryReadSettings(out float gain)
    {
        gain = 1f;
        try
        {
            var s = Infrastructure.SettingsManager.Instance;
            if (!s.UiSoundsEnabled) return false;
            int v = Math.Clamp(s.UiSoundVolume, 0, 100);
            if (v == 0) return false;
            gain = v / 100f;
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static void PlayOnWorker(int cueIndex, float gain)
    {
        bool queued = false;
        try
        {
            CachedSound? sound = _sounds[cueIndex] ?? LoadCue(cueIndex);
            if (sound == null) return;

            lock (_engineLock)
            {
                if (_shutdown || _engineFailed) return;
                if (!EnsureEngineLocked()) return;

                _volume!.Volume = gain;
                _mixer!.AddMixerInput((ISampleProvider)new CachedSoundSampleProvider(sound));
                queued = true;
            }
        }
        catch (Exception ex)
        {
            SafeLog("UI sound playback failed: " + ex.Message);
        }
        finally
        {
            if (!queued)
            {
                if (Interlocked.Decrement(ref _activeVoices) < 0) Interlocked.Exchange(ref _activeVoices, 0);
            }
        }
    }

    /// <summary>Decodes a cue's embedded .wav once. Thread-safe; a benign double-decode under a
    /// race just costs one throwaway array.</summary>
    private static CachedSound? LoadCue(int cueIndex)
    {
        try
        {
            var asm = typeof(UiSoundEffect).Assembly;
            using Stream? stream = asm.GetManifestResourceStream(ResourceNames[cueIndex]);
            if (stream == null)
            {
                SafeLog($"UI sound resource missing: {ResourceNames[cueIndex]} (check the EmbeddedResource LogicalName in the csproj)");
                return null;
            }

            var sound = new CachedSound(stream);

            _throttleMs[cueIndex] = Math.Clamp(sound.Duration.TotalMilliseconds, MinThrottleMs, MaxThrottleMs);
            _sounds[cueIndex] = sound;
            return sound;
        }
        catch (Exception ex)
        {
            SafeLog($"UI sound decode failed for {ResourceNames[cueIndex]}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Lazily brings up the output device. Caller MUST hold <see cref="_engineLock"/>.</summary>
    private static bool EnsureEngineLocked()
    {
        if (_output != null) return true;
        if (_engineFailed) return false;

        try
        {
            if (WaveOut.DeviceCount <= 0)
            {
                _engineFailed = true;
                SafeLog("No audio output device present - UI sounds disabled for this session.");
                return false;
            }

            _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            _mixer.MixerInputEnded += OnMixerInputEnded;

            _volume = new VolumeSampleProvider(_mixer) { Volume = 1f };

            _output = new WaveOutEvent { DesiredLatency = 150 };
            _output.Init(_volume);
            _output.Play();
            return true;
        }
        catch (Exception ex)
        {
            _engineFailed = true;
            TearDownLocked();
            SafeLog("UI sound engine could not start - UI sounds disabled for this session: " + ex.Message);
            return false;
        }
    }

    private static void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        if (Interlocked.Decrement(ref _activeVoices) < 0) Interlocked.Exchange(ref _activeVoices, 0);
    }

    /// <summary>
    /// AUDIO_13: deterministic teardown. The previous implementation never disposed anything.
    /// Wired to IClassicDesktopStyleApplicationLifetime.Exit in AvaloniaApp. Idempotent.
    /// </summary>
    public static void Shutdown()
    {
        lock (_engineLock)
        {
            _shutdown = true;
            TearDownLocked();
        }
    }

    private static void TearDownLocked()
    {
        try { _output?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _output?.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _output = null;

        if (_mixer != null)
        {
            try { _mixer.MixerInputEnded -= OnMixerInputEnded; } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            try { _mixer.RemoveAllMixerInputs(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        _mixer = null;
        _volume = null;
        Interlocked.Exchange(ref _activeVoices, 0);
    }

    private static void SafeLog(string message)
    {
        try { RuntimeLog.Info("UiSound", message); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }


    /// <summary>
    /// A fully decoded clip held as float samples. Decoded ONCE per cue; every play wraps it in
    /// a throwaway <see cref="CachedSoundSampleProvider"/> cursor. This is what allows cues to
    /// overlap - the old winmm path physically could not (AUDIO_12).
    /// </summary>
    private sealed class CachedSound
    {
        public float[] AudioData { get; }
        public WaveFormat WaveFormat { get; }
        public TimeSpan Duration { get; }

        public CachedSound(Stream stream)
        {
            using var reader = new WaveFileReader(stream);
            ISampleProvider source = reader.ToSampleProvider();
            WaveFormat = source.WaveFormat;

            int bytesPerSample = Math.Max(1, reader.WaveFormat.BitsPerSample / 8);
            int estimate = (int)Math.Max(1024, reader.Length / bytesPerSample);
            var data = new float[estimate];
            int total = 0;

            while (true)
            {
                if (total == data.Length) Array.Resize(ref data, data.Length * 2);
                int read = source.Read(data, total, data.Length - total);
                if (read <= 0) break;
                total += read;
            }

            if (total != data.Length) Array.Resize(ref data, total);
            AudioData = data;

            int perSecond = Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels);
            Duration = TimeSpan.FromSeconds(total / (double)perSecond);
        }
    }

    /// <summary>Read cursor over a <see cref="CachedSound"/>. Returning 0 makes the mixer drop
    /// it and raise MixerInputEnded, which is how the voice counter is released.</summary>
    private sealed class CachedSoundSampleProvider : ISampleProvider
    {
        private readonly CachedSound _sound;
        private int _position;

        public CachedSoundSampleProvider(CachedSound sound) => _sound = sound;

        public WaveFormat WaveFormat => _sound.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _sound.AudioData.Length - _position;
            int n = Math.Min(available, count);
            if (n > 0)
            {
                Array.Copy(_sound.AudioData, _position, buffer, offset, n);
                _position += n;
            }
            return n < 0 ? 0 : n;
        }
    }
}

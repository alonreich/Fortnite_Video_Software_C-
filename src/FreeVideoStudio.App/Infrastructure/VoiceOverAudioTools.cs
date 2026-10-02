using System;
using System.Collections.Generic;
using System.IO;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// VOTOOLS_01 — peak decoding, take-file cleanup, preview-player retirement and the remembered
/// voice-protection choices, lifted out of <c>VoiceOverWindow</c>.
///
/// That window is 3,792 lines and 116 fields, and what makes it risky is not its size but that it
/// owns LIVE MICROPHONE AND PLAYBACK HANDLES alongside UI state. These members touch none of that
/// — they are file and buffer work — so moving them shrinks the surface that has to be reasoned
/// about together with device lifetime.
///
/// ⚠️ <c>TryDeleteFile</c>, <c>RetirePreviewPlayersAsync</c> AND <c>RetireGenerationCts</c> ARE
/// TOTAL BY CONTRACT. They run on teardown paths; an exception escaping any of them turns "a take
/// could not be cleaned up" into a failed close. Bodies moved verbatim — do not add rethrows.
///
/// ⚠️ Take WAV files live in TWO directories with different retention rules (the temp root and the
/// voice-over directory). Deleting from the wrong one destroys a recording the user still needs.
/// </summary>
internal static class VoiceOverAudioTools
{
    /// <summary>
    /// VOTAKE_01 — reduces a WAV to <paramref name="buckets"/> absolute peaks (0..1).
    /// Runs on a worker thread; touches no interface state.
    /// </summary>
    internal static float[] DecodePeaks(string path, int buckets)
    {
        if (buckets < 1) buckets = 1;
        using var reader = new FreeVideoStudio.Core.Media.WavAudioReader(path);

        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        if (totalSamples <= 0) return Array.Empty<float>();

        var peaks = new float[buckets];
        var buffer = new float[8192];
        long samplesPerBucket = Math.Max(1, totalSamples / buckets);

        long read = 0;
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int bucket = (int)Math.Min(buckets - 1, (read + i) / samplesPerBucket);
                float v = buffer[i];
                if (v < 0) v = -v;
                if (v > peaks[bucket]) peaks[bucket] = v;
            }
            read += n;
        }

        return peaks;
    }

    /// <summary>
    /// ISSUE_05 — deletes a temp take, tolerating a briefly-still-held file handle.
    ///
    /// The recorder now closes its WAV writer in an ordered shutdown, but Windows can hold a
    /// handle open for a few more milliseconds after the last Dispose. A single attempt therefore
    /// used to lose the race now and then and leave abandoned takes accumulating in the temp
    /// folder forever, because every failure was swallowed by a bare `catch`.
    /// </summary>
    internal static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!System.IO.File.Exists(path)) return;
                    System.IO.File.Delete(path);
                    return;
                }
                catch (System.IO.IOException) when (attempt < 3)
                {
                    await Task.Delay(60);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    await Task.Delay(60);
                }
                catch (Exception ex)
                {
                    RuntimeLog.WarnThrottled("VoiceOver", $"Could not delete temp take '{System.IO.Path.GetFileName(path)}': {ex.Message}");
                    return;
                }
            }

            RuntimeLog.Debug("VoiceOver", $"Temp take '{System.IO.Path.GetFileName(path)}' is still locked; leaving it for temp cleanup.");
        });
    }

    /// <summary>
    /// LEAK_02 — cancel a superseded generation, then dispose it OFF the interface thread.
    ///
    /// ⚠️ NEVER call <c>CancellationTokenSource.Dispose()</c> directly from an event handler here.
    /// Dispose BLOCKS until every callback raised by Cancel() has finished, and those callbacks
    /// marshal back to the interface thread — so the interface thread ends up waiting for itself.
    /// That is the exact deadlock that froze the Granular Speed Editor earlier (see CANCEL_01);
    /// it is a real, reproduced bug in this codebase, not a theoretical one.
    ///
    /// Handing the disposal to a worker thread keeps the tidy-up without the wait: nothing on that
    /// thread is holding the interface hostage, so Cancel's callbacks are free to complete.
    /// </summary>
    internal static void RetireGenerationCts(System.Threading.CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { cts.Dispose(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    /// <summary>
    /// VOTL_01 — formats a clock the same way the main screen's timeline does, so the two windows
    /// read as one product. Always hh:mm:ss; a leading sign is the caller's business.
    /// </summary>
    internal static string FormatClock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    /// <summary>
    /// VOPROT_02 — stores the applied choices as the "last time" values, but ONLY for a checkbox
    /// the user was actually allowed to set. Writing back a locked box would let an Always mode
    /// quietly overwrite the preference the user would return to if they switched back to
    /// Remember — the setting would appear to change itself.
    /// </summary>
    internal static void RememberVoiceProtectionChoices(bool duckGame, bool duckMusic)
    {
        var settings = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance;
        bool dirty = false;

        if (settings.VoiceProtectGameMode == FreeVideoStudio.App.Infrastructure.VoiceProtectionMode.RememberLastChoice &&
            settings.VoiceProtectGameLast != duckGame)
        {
            dirty = true;
        }

        if (settings.VoiceProtectMusicMode == FreeVideoStudio.App.Infrastructure.VoiceProtectionMode.RememberLastChoice &&
            settings.VoiceProtectMusicLast != duckMusic)
        {
            dirty = true;
        }

        if (!dirty) return;

        try
        {
            bool committed = FreeVideoStudio.App.Infrastructure.SettingsManager.Update(s =>
            {
                if (s.VoiceProtectGameMode == FreeVideoStudio.App.Infrastructure.VoiceProtectionMode.RememberLastChoice)
                {
                    s.VoiceProtectGameLast = duckGame;
                }

                if (s.VoiceProtectMusicMode == FreeVideoStudio.App.Infrastructure.VoiceProtectionMode.RememberLastChoice)
                {
                    s.VoiceProtectMusicLast = duckMusic;
                }
            });
            if (committed)
            {
                RuntimeLog.Info("VoiceOver",
                    $"Voice-protection choices remembered: game={duckGame}, music={duckMusic}.");
            }
            else
            {
                RuntimeLog.Fail("VoiceOver", "Could not save the voice-protection choices: the settings transaction did not commit.");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"Could not save the voice-protection choices: {ex.Message}");
        }
    }
}

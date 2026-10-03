// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// VOLSHARED_01 — ONE implementation of the vertical master-volume rack (fluid slider, % badge,
/// speaker/mute button) for the Main App, the Video Merger and the Crop Tools.
///
/// <para>Each window used to carry its own copy, and the copies had drifted:</para>
/// <list type="bullet">
/// <item>the Main App's handler for a change made ELSEWHERE re-applied its players but never moved
///   its own slider, badge or icon — after a trip to the Crop Tools it showed a stale level, and the
///   next re-apply from that stale slider (after the Music Wizard) silently snapped the master back;</item>
/// <item>"mute" meant "drag the slider to 0" with a per-window memory of the old level, so muting in
///   one app and unmuting in another restored 100% (VOLMUTE_01 makes mute one shared flag);</item>
/// <item>the Crop Tools and the Merger persisted the level synchronously on the UI thread (the
///   EDITHOT_01 stall the Main App had already fixed), and only on mouse release — keyboard, wheel
///   and mute changes were never saved (<see cref="MasterVolumePersistence"/> now saves every change).</item>
/// </list>
/// </summary>
public static class MasterVolumeUi
{
    public static readonly Geometry SpeakerOn =
        Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M13,5 A4,4 0 0,1 13,11 M16,2 A8,8 0 0,1 16,14");
    public static readonly Geometry SpeakerMuted =
        Geometry.Parse("M3,7 L6,7 L10,3 L10,13 L6,9 L3,9 Z M12,5 L16,13 M16,5 L12,13");

    /// <summary>
    /// Wires one window's rack to the suite master. <paramref name="applyPlayers"/> re-applies that
    /// window's own players (it runs on the UI thread after every level/mute change, wherever it came
    /// from). <paramref name="onUserTouch"/>, when given, runs on every user gesture on the rack.
    /// Unhooks itself when <paramref name="owner"/> closes.
    /// </summary>
    public static void Bind(Window owner, Slider? slider, TextBlock? badge, Avalonia.Controls.Shapes.Path? icon, Button? speakerButton,
        Action applyPlayers, Action? onUserTouch = null)
    {
        bool syncing = false;

        void Paint()
        {
            int level = MpvIpcClient.GlobalMasterVolume;
            bool muted = MpvIpcClient.GlobalMuted;
            if (slider != null && Math.Abs(slider.Value - level) > 0.5)
            {
                syncing = true;
                try { slider.Value = level; }
                finally { syncing = false; }
            }
            if (badge != null) badge.Text = muted ? "MUTE" : $"{level}%";
            if (icon != null) icon.Data = muted || level == 0 ? SpeakerMuted : SpeakerOn;
            if (slider != null) slider.Opacity = muted ? 0.55 : 1.0;
        }

        void OnMasterChanged(int _)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnMasterChanged(0));
                return;
            }
            Paint();
            try { applyPlayers(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        if (slider != null)
        {
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty || syncing) return;
                int vol = (int)Math.Round(slider.Value);
                // Moving the level is asking to hear it: a muted master unmutes (VOLMUTE_01).
                if (MpvIpcClient.GlobalMuted && vol > 0) MpvIpcClient.SetGlobalMuted(false);
                MpvIpcClient.SetGlobalMasterVolume(vol);
            };
            if (onUserTouch != null)
            {
                slider.PointerPressed += (_, _) => onUserTouch();
                slider.PointerWheelChanged += (_, _) => onUserTouch();
            }
        }

        if (speakerButton != null)
        {
            speakerButton.Click += (_, _) =>
            {
                onUserTouch?.Invoke();
                ToggleMute();
            };
        }

        MpvIpcClient.GlobalMasterVolumeChanged += OnMasterChanged;
        owner.Closed += (_, _) => MpvIpcClient.GlobalMasterVolumeChanged -= OnMasterChanged;
        Paint();
    }

    /// <summary>VOLMUTE_01 — the shared mute. A master at 0% unmutes to 50% so the button always does something.</summary>
    public static void ToggleMute()
    {
        if (MpvIpcClient.GlobalMuted)
        {
            MpvIpcClient.SetGlobalMuted(false);
            if (MpvIpcClient.GlobalMasterVolume == 0) MpvIpcClient.SetGlobalMasterVolume(50);
        }
        else
        {
            MpvIpcClient.SetGlobalMuted(true);
        }
    }

    /// <summary>Keyboard nudge (Main App shortcuts): moves the shared level, unmuting on the way up.</summary>
    public static void Nudge(int delta)
    {
        int next = Math.Clamp(MpvIpcClient.GlobalMasterVolume + delta, 0, 100);
        if (MpvIpcClient.GlobalMuted && delta > 0) MpvIpcClient.SetGlobalMuted(false);
        MpvIpcClient.SetGlobalMasterVolume(next);
    }
}

/// <summary>
/// VOLSHARED_01 — saves the shared level (session state "MainVolume") and the shared mute
/// (settings "PreviewMuted") after EVERY change, debounced, off the UI thread. Started once.
/// </summary>
public static class MasterVolumePersistence
{
    private static System.Threading.Timer? _timer;
    private static int _savedLevel = -1;
    private static bool? _savedMuted;
    private static readonly object Gate = new();

    public static void Start()
    {
        lock (Gate)
        {
            if (_timer != null) return;
            _savedLevel = MpvIpcClient.GlobalMasterVolume;
            _savedMuted = MpvIpcClient.GlobalMuted;
            _timer = new System.Threading.Timer(_ => _ = FlushAsync(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
        MpvIpcClient.GlobalMasterVolumeChanged += _ =>
        {
            lock (Gate) _timer?.Change(600, System.Threading.Timeout.Infinite);
        };
    }

    private static async System.Threading.Tasks.Task FlushAsync()
    {
        int level = MpvIpcClient.GlobalMasterVolume;
        bool muted = MpvIpcClient.GlobalMuted;
        try
        {
            if (level != _savedLevel)
            {
                _savedLevel = level;
                await new FreeVideoStudio.Core.Ipc.StateTransferStore(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault())
                    .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject { ["MainVolume"] = (double)level })
                    .ConfigureAwait(false);
            }
            if (_savedMuted != muted)
            {
                _savedMuted = muted;
                SettingsManager.Update(s => s.PreviewMuted = muted);
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}

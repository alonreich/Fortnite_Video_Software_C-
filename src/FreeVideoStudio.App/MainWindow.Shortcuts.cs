
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private void GlobalKeyUpHandler(object? sender, KeyEventArgs e)
    {
        if (Controls.PhaseOverlayControl.FightInputActive) return;

        if (KeyboardFocusPolicy.HotkeysSuspended(TopLevel.GetTopLevel(this)))
            return;

        var kb = SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);

        if (playPause.Matches(e) || markStart.Matches(e) || markEnd.Matches(e) || e.Key is Key.Space or Key.Left or Key.Right)
        {
            e.Handled = true;
        }
    }

    private void GlobalKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (Controls.PhaseOverlayControl.FightInputActive) return;

        if (KeyboardFocusPolicy.HotkeysSuspended(TopLevel.GetTopLevel(this)))
        {
            return;
        }

        if (_projectSession != null && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.S:
                    e.Handled = true;
                    _ = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                        ? _projectSession.SaveAsAsync()
                        : _projectSession.SaveAsync();
                    return;

                case Key.O:
                    e.Handled = true;
                    _ = _projectSession.OpenAsync();
                    return;

                case Key.Z when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    e.Handled = true;
                    _projectSession.Undo();
                    return;

                case Key.Y:
                case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    e.Handled = true;
                    _projectSession.Redo();
                    return;
            }
        }

        bool questionPressed = e.Key == Key.OemQuestion;
        var sheet = ShortcutSheetOverlayCtl;
        if (sheet != null)
        {
            if (questionPressed)
            {
                if (!sheet.IsVisible) BuildShortcutSheetRows();
                sheet.IsVisible = !sheet.IsVisible;
                if (sheet.IsVisible) Focus();
                RuntimeLog.Info("UI", $"Keyboard shortcut sheet {(sheet.IsVisible ? "opened" : "closed")}.");
                e.Handled = true;
                return;
            }
            if (sheet.IsVisible && e.Key == Key.Escape)
            {
                if (KeyboardFocusPolicy.HotkeysSuspended(TopLevel.GetTopLevel(this))) return;
                var sheetFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                if (sheetFocus is null or MainWindow)
                {
                    sheet.IsVisible = false;
                    e.Handled = true;
                }
                return;
            }
            if (sheet.IsVisible)
            {
                e.Handled = true;
                return;
            }
        }

        if (_isThumbnailMarkerSelected && _thumbnailSet && e.Key is Key.Left or Key.Right)
        {
            bool precise = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            int frames = precise ? 1 : 10;
            MoveThumbnailMarkerByFrames(e.Key == Key.Left ? -frames : frames);
            e.Handled = true;
            return;
        }

        var kb = SettingsManager.Instance.KeyBinds;

        if (e.Key == Key.Delete)
        {
            if (FocusManager?.GetFocusedElement() is ListBox listBox && listBox.SelectedItem is string filePath)
            {
                try
                {
                    System.IO.File.Delete(filePath);
                }
                catch (System.Exception __ex)
                {
                    RuntimeLog.Fail("UI", $"Could not delete '{System.IO.Path.GetFileName(filePath)}': {__ex.Message}");
                    RuntimeLog.Swallowed(__ex);
                    ShowTacticalError("Could not delete that file");
                }
            }
        }
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);
        var seekFwd = new Avalonia.Input.KeyGesture(kb.SeekForward);
        var seekBack = new Avalonia.Input.KeyGesture(kb.SeekBackward);
        var fineSeekFwdCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekFwdShift = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Shift);
        var fineSeekBackCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekBackShift = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Shift);
        var volUp = new Avalonia.Input.KeyGesture(kb.VolumeUp);
        var volDown = new Avalonia.Input.KeyGesture(kb.VolumeDown);
        var aggVolUpCtrl = new Avalonia.Input.KeyGesture(kb.AggressiveVolumeUp, Avalonia.Input.KeyModifiers.Control);
        var aggVolDownCtrl = new Avalonia.Input.KeyGesture(kb.AggressiveVolumeDown, Avalonia.Input.KeyModifiers.Control);

        if (playPause.Matches(e))
        {
            if (TryExecutePlayPause()) e.Handled = true;
        }
        else if (_isMusicBlockFocused && _musicWizardResult != null && (e.Key == Key.Left || e.Key == Key.Right))
        {
            double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0;
            if (duration > 0)
            {
                double dur = _musicWizardResult.TimelineEndSeconds - _musicWizardResult.TimelineStartSeconds;
                double offset = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.05 : ((_trimEndMs - _trimStartMs) / 1000.0) * 0.01;

                double newStart = _musicWizardResult.TimelineStartSeconds + (e.Key == Key.Left ? -offset : offset);
                double newEnd = newStart + dur;

                if (newStart < 0) {
                    newStart = 0;
                    newEnd = dur;
                }
                if (newEnd > duration) {
                    newEnd = duration;
                    newStart = duration - dur;
                }

                _musicWizardResult.TimelineStartSeconds = newStart;
                _musicWizardResult.TimelineEndSeconds = newEnd;
                UpdateTimelineMarkers();
                SaveRecoveryState(label: "move music", gestureKey: "music-nudge");
                e.Handled = true;
            }
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("seek", 5);
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            _ = ActiveVideoHost?.IpcClient?.SendCommandAsync("seek", -5);
            e.Handled = true;
        }
        else if (aggVolUpCtrl.Matches(e))
        {
            AdjustPreviewMasterVolume(10);
            e.Handled = true;
        }
        else if (aggVolDownCtrl.Matches(e))
        {
            AdjustPreviewMasterVolume(-10);
            e.Handled = true;
        }
        else if (volUp.Matches(e))
        {
            AdjustPreviewMasterVolume(2);
            e.Handled = true;
        }
        else if (volDown.Matches(e))
        {
            AdjustPreviewMasterVolume(-2);
            e.Handled = true;
        }
        else if (markStart.Matches(e))
        {
            if (_markStartCommand?.CanExecute(null) == true)
            {
                _markStartCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (markEnd.Matches(e))
        {
            if (_markEndCommand?.CanExecute(null) == true)
            {
                _markEndCommand.Execute(null);
                e.Handled = true;
            }
        }
    }


    private FreeVideoStudio.App.ViewModels.RelayCommand? _playPauseCommand;
    private FreeVideoStudio.App.ViewModels.RelayCommand? _markStartCommand;
    private FreeVideoStudio.App.ViewModels.RelayCommand? _markEndCommand;

    /// <summary>
    /// Unified play/pause behaviour. Returns true when the key was consumed, mirroring
    /// KeyBinding.TryHandle (Handled only when the command actually ran).
    /// </summary>
    /// <summary>DOUBLEFIRE_01 — when the transport toggle last actually ran.</summary>
    private DateTime _lastTransportToggleUtc = DateTime.MinValue;

    /// <summary>
    /// DOUBLEFIRE_01 — the shortest gap between two DELIBERATE play/pause presses. A person
    /// cannot press a button twice in 60 ms; two activations that close together are one physical
    /// press arriving down two wirings.
    /// </summary>
    private const int TransportToggleCoalesceMs = 60;

    private bool TryExecutePlayPause()
    {
        if (Controls.PhaseOverlayControl.FightInputActive) return false;

        var now = DateTime.UtcNow;
        if ((now - _lastTransportToggleUtc).TotalMilliseconds < TransportToggleCoalesceMs)
        {
            RuntimeLog.Fail("UI",
                $"Play/Pause was activated twice within {TransportToggleCoalesceMs}ms — the second " +
                "activation was ignored. A transport control has more than one activation path " +
                "wired to it (Command AND Click?); find it and remove one. See DOUBLEFIRE_01.");
            return true;
        }
        _lastTransportToggleUtc = now;

        var btn = PlayPauseButtonCtl;
        if (btn == null || !btn.IsEnabled) return false;

        if (_isMusicBlockFocused)
        {
            _isMusicBlockFocused = false;
            UpdateTimelineMarkers();
        }

        if (ActiveVideoHost?.IpcClient != null)
        {
            RuntimeLog.Info("UI", "User toggled Play/Pause state.");
            TogglePlayPauseTransport();
            return true;
        }
        return false;
    }

    private void ExecuteMarkStart()
    {
        EnsureTrimPointsSet();
        RuntimeLog.Info("UI", $"User clicked MARK START at {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss\\.ff}.");
        double time = GetCurrentMpvTime();
        SetTrimStart(time * 1000);
        _trimStartSet = true;
        var markStartButton = MarkStartButtonCtl;
        if (markStartButton != null) markStartButton.Content = $"START: {FormatTime(TimeSpan.FromSeconds(time))}";

        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "no");
        }

        PlayUiSound();
        ShowTacticalFeedback($"🏁 {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}");
        ShowTimelineGlow(_trimStartMs, Avalonia.Media.Brushes.SeaGreen);
        UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        SaveRecoveryState(label: "mark start");

        TriggerParticleBurst(new Avalonia.Point(Bounds.Width / 2, Bounds.Height * 0.7),
            Controls.ParticleBurstCanvas.BurstPreset.MarkerDrop);
    }

    private void ExecuteMarkEnd()
    {
        EnsureTrimPointsSet();
        RuntimeLog.Info("UI", $"User clicked MARK END at {TimeSpan.FromMilliseconds(_trimEndMs):hh\\:mm\\:ss\\.ff}.");
        double time = GetCurrentMpvTime();
        if (time * 1000.0 <= _trimStartMs)
        {
            ShowTacticalFeedback("⚠ END must be after START");
            RuntimeLog.Info("UI", $"MARK END blocked: {TimeSpan.FromSeconds(time):hh\\:mm\\:ss\\.ff} is not after START {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss\\.ff}.");
            return;
        }
        _trimEndMs = time * 1000;
        _prewarmArmed = true;
        SchedulePrewarm();
        var markEndButton = MarkEndButtonCtl;
        if (markEndButton != null) markEndButton.Content = $"END: {FormatTime(TimeSpan.FromSeconds(time))}";

        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }

        PlayUiSound();
        ShowTacticalFeedback($"🏁 {TimeSpan.FromSeconds(time):mm\\:ss\\.ff}");
        ShowTimelineGlow(_trimEndMs, Avalonia.Media.Brushes.SeaGreen);
        UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        SaveRecoveryState(label: "mark end");

        TriggerParticleBurst(new Avalonia.Point(Bounds.Width / 2, Bounds.Height * 0.7),
            Controls.ParticleBurstCanvas.BurstPreset.MarkerDrop);
    }

    /// <summary>Attaches the transport Commands and settings-bound KeyBindings to the transport buttons.</summary>
    private void RefreshTransportKeyBindings()
    {
        var kb = SettingsManager.Instance.KeyBinds;

        _playPauseCommand ??= new FreeVideoStudio.App.ViewModels.RelayCommand(() => TryExecutePlayPause());
        _markStartCommand ??= new FreeVideoStudio.App.ViewModels.RelayCommand(ExecuteMarkStart);
        _markEndCommand ??= new FreeVideoStudio.App.ViewModels.RelayCommand(ExecuteMarkEnd);

        void Attach(string name, Avalonia.Input.Key key, System.Windows.Input.ICommand command)
        {
            var btn = this.FindControl<Button>(name);
            if (btn == null) return;
            btn.Command = command;
            btn.KeyBindings.Clear();
            btn.KeyBindings.Add(new Avalonia.Input.KeyBinding
            {
                Gesture = new Avalonia.Input.KeyGesture(key),
                Command = command
            });
        }

        Attach("PlayPauseButton", kb.PlayPause, _playPauseCommand);
        Attach("MarkStartButton", kb.MarkStart, _markStartCommand);
        Attach("MarkEndButton", kb.MarkEnd, _markEndCommand);
    }
}

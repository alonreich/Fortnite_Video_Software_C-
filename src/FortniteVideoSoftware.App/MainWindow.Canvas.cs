using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.Models;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

public partial class MainWindow
{
    private void UpdateAutoThumbnailMarker(double canvasWidth, double duration)
    {
        if (!_thumbnailSet && _thumbnailCameraControl != null)
        {
            double effectiveEnd = _trimEndMs > 0 ? _trimEndMs : duration * 1000.0;
            double effectiveStart = _trimStartSet ? _trimStartMs : 0;
            _thumbnailPosMs = effectiveStart + (effectiveEnd - effectiveStart) * 0.6666;
            
            double thumbMs = Math.Clamp(_thumbnailPosMs, 0, duration * 1000.0);
            double thumbX = (thumbMs / 1000.0 / duration) * canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(_thumbnailCameraControl, ClampTimelineCameraLeft(thumbX, canvasWidth));
        }
    }

    private void UpdateDraggingVisuals(double canvasWidth, double duration)
    {
        if (duration <= 0) return;
        
        UpdateAutoThumbnailMarker(canvasWidth, duration);
        
        if (_regionRectRef != null)
        {
            double regStartX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
            double regEndX = _trimEndMs > 0 ? (_trimEndMs / 1000.0 / duration) * canvasWidth : canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(_regionRectRef, regStartX);
            _regionRectRef.Width = Math.Max(0, regEndX - regStartX);
        }
        if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
        {
            double mStartX = (_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth;
            double mEndX = (_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth;
            if (_musicStartPopupRef != null) Avalonia.Controls.Canvas.SetLeft(_musicStartPopupRef, mStartX - 26);
            if (_musicEndPopupRef != null) Avalonia.Controls.Canvas.SetLeft(_musicEndPopupRef, mEndX - 26);
            if (_musicBlockRectRef != null)
            {
                Avalonia.Controls.Canvas.SetLeft(_musicBlockRectRef, mStartX);
                _musicBlockRectRef.Width = Math.Max(2, mEndX - mStartX);
            }
        }
    }

    private void AttachThumbnailCameraMarkerInteractions(Control marker, Canvas timelineCanvas, double durationSeconds)
    {
        marker.PointerEntered += (_, _) => SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (!_isDraggingThumbnailMarker)
            {
                SetTimelineCameraHover(marker, false);
            }
        };
        marker.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isThumbnailMarkerSelected = true;
            _isDraggingThumbnailMarker = true;
            if (_thumbnailMarkerIconAntsRef != null) _thumbnailMarkerIconAntsRef.IsVisible = true;
            if (_thumbnailMarkerLineAntsRef != null) _thumbnailMarkerLineAntsRef.IsVisible = true;
            marker.Focus();
            SetTimelineCameraHover(marker, true);
            MoveThumbnailMarkerToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            e.Pointer.Capture(marker);
            e.Handled = true;
        };
        marker.PointerMoved += (_, e) =>
        {
            if (!_isDraggingThumbnailMarker)
            {
                return;
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // THUMB_02 — THE FLAG ALONE IS NOT PROOF THAT A DRAG IS STILL HAPPENING.
            //
            // `_isDraggingThumbnailMarker` was raised on PointerPressed and lowered ONLY in
            // PointerReleased. If the marker control was torn down mid-gesture — UpdateTimelineMarkers
            // clears and rebuilds the whole canvas, and it is POSTED, so it can land between the
            // press and the release — Avalonia raises PointerCaptureLost on the dead control
            // instead of PointerReleased, and nothing ever lowered the flag again.
            //
            // From that moment this handler was live on the REBUILT marker with no button held, so
            // merely moving the mouse near the camera icon re-ran MoveThumbnailMarkerToCanvasX,
            // which PAUSES the player and seeks it (SeekMainPreviewToMarkerMs). That is the
            // "touched the thumbnail icon and now play only advances one frame and pauses" trap.
            //
            // Two independent guards, because either one alone can be defeated: the button must
            // still be down, and PointerCaptureLost below must clear the flag.
            // ══════════════════════════════════════════════════════════════════════════════
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed)
            {
                EndThumbnailMarkerDrag(marker, redraw: true);
                return;
            }

            // THUMB_01 — WAS `seekPreview: false`, WHICH IS WHY DRAGGING SHOWED NOTHING.
            // The marker slid along the timeline while the picture stayed frozen on whatever frame
            // was up before the drag began, so you were choosing a cover image blind and only saw
            // the result on release. Seeking on every move turns the drag into a scrub.
            //
            // Safe to fire on every pointer move: SeekInternal coalesces (a seek already in flight
            // parks the newest target in `_nextSeekTarget` and runs it on completion), so a fast
            // drag collapses into "seek to wherever the pointer ended up" instead of queueing one
            // mpv command per pixel.
            MoveThumbnailMarkerToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            e.Handled = true;
        };
        marker.PointerReleased += (_, e) =>
        {
            if (!_isDraggingThumbnailMarker)
            {
                return;
            }

            MoveThumbnailMarkerToCanvasX(e.GetPosition(timelineCanvas).X, timelineCanvas, durationSeconds, marker, seekPreview: true);
            e.Pointer.Capture(null);
            EndThumbnailMarkerDrag(marker, redraw: true);
            e.Handled = true;
        };

        // THUMB_02 — the only event that is GUARANTEED to arrive when a captured control is
        // removed from the tree, the window loses focus, or the pointer is stolen. Without it a
        // rebuild mid-gesture left the drag flag raised for the rest of the session.
        marker.PointerCaptureLost += (_, _) =>
        {
            if (!_isDraggingThumbnailMarker) return;
            EndThumbnailMarkerDrag(marker, redraw: false);
        };
    }

    /// <summary>
    /// THUMB_02 — one exit for the thumbnail-marker drag, reached from PointerReleased, from a
    /// move with no button held, and from PointerCaptureLost. The marker stays SELECTED (the arrow
    /// keys still nudge it, per TL-HITBOX) — only the DRAG ends here.
    ///
    /// <paramref name="redraw"/> is false on capture-loss on purpose: that path is usually already
    /// inside a rebuild, and asking for another one from within it re-enters the same teardown.
    /// </summary>
    private void EndThumbnailMarkerDrag(Control marker, bool redraw)
    {
        _isDraggingThumbnailMarker = false;
        _isThumbnailMarkerSelected = true;
        SetTimelineCameraHover(marker, false);
        UpdateThumbnailButtonState();   // THUMB_01
        if (redraw) UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        SaveRecoveryState();
    }

    /// <summary>
    /// THUMB_01 — is the preview parked on the thumbnail frame? Half a second of tolerance,
    /// because a seek lands near a keyframe rather than exactly where it was asked, and demanding
    /// an exact match would make REMOVE unreachable.
    /// </summary>
    private bool IsPlayheadOnThumbnail()
    {
        if (!_thumbnailSet) return false;
        return Math.Abs(GetCurrentMpvTime() * 1000.0 - _thumbnailPosMs) <= 500.0;
    }

    /// <summary>
    /// THUMB_01 — keeps the button's label, colour and tooltip describing what pressing it will
    /// actually do right now. Called from the playback tick, so it must stay cheap: it writes
    /// nothing unless the text has genuinely changed.
    /// </summary>
    private void UpdateThumbnailButtonState()
    {
        var btn = this.FindControl<Button>("SetThumbnailButton");
        var txt = this.FindControl<TextBlock>("SetThumbnailText");
        if (btn == null) return;

        string label;
        string tip;
        bool destructive = false;

        if (!_thumbnailSet)
        {
            label = " SET THUMBNAIL ";
            tip = "Save the exact frame you are looking at right now as the cover image for your video.";
        }
        else if (IsPlayheadOnThumbnail())
        {
            label = " REMOVE THUMBNAIL ";
            tip = "You are parked on your cover frame. Press to clear it and go back to the automatic choice.";
            destructive = true;
        }
        else
        {
            label = " MOVE THUMBNAIL HERE ";
            tip = "Move the cover image to the frame you are looking at now. Your old choice is replaced, not deleted twice.";
        }

        if (txt != null && txt.Text != label) txt.Text = label;

        if (destructive)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            if (!btn.Classes.Contains("Danger")) btn.Classes.Add("Danger");
        }
        else
        {
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Primary") && !btn.Classes.Contains("Secondary")) btn.Classes.Add("Primary");
        }

        ToolTip.SetTip(btn, tip);
    }

    private void MoveThumbnailMarkerToCanvasX(double canvasX, Canvas timelineCanvas, double durationSeconds, Control marker, bool seekPreview)
    {
        double width = timelineCanvas.Bounds.Width;
        if (durationSeconds <= 0 || width <= 0)
        {
            return;
        }

        double clampedX = Math.Clamp(canvasX, 0, width);
        _thumbnailPosMs = (clampedX / width) * durationSeconds * 1000.0;
        _thumbnailSet = true;
        Canvas.SetLeft(marker, ClampTimelineCameraLeft(clampedX, width));

        if (seekPreview)
        {
            SeekMainPreviewToMarkerMs(_thumbnailPosMs);
        }
    }

    /// <summary>
    /// THUMB_01 — nudges the thumbnail marker and scrubs the preview to match.
    ///
    /// The frame rate now comes from the file (mpv's container-fps) instead of a hard-coded 60.
    /// On 30 fps footage the old constant stepped a frame and a half, so a control advertised as
    /// frame-accurate landed between frames on most real recordings; 60 remains the fallback for
    /// the moment before mpv has reported anything.
    /// </summary>
    private void MoveThumbnailMarkerByFrames(int frameDelta)
    {
        var ipc = ActiveVideoHost?.IpcClient;
        double duration = ipc?.Duration ?? 0.0;
        if (!_thumbnailSet || duration <= 0)
        {
            return;
        }

        double fps = ipc?.VideoFps ?? 0.0;
        if (fps <= 1.0) fps = 60.0;

        double deltaMs = (1000.0 / fps) * frameDelta;
        _thumbnailPosMs = Math.Clamp(_thumbnailPosMs + deltaMs, 0, duration * 1000.0);
        SeekMainPreviewToMarkerMs(_thumbnailPosMs);
        UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        UpdateThumbnailButtonState();

        // A held arrow key repeats at the OS key rate. Writing the whole recovery file on each
        // repeat is pure waste, so the save is coalesced to once the key has settled.
        QueueThumbnailRecoverySave();
    }

    /// <summary>THUMB_01 — coalesces recovery writes while the marker is being nudged.</summary>
    private Avalonia.Threading.DispatcherTimer? _thumbnailSaveDebounce;

    private void QueueThumbnailRecoverySave()
    {
        _thumbnailSaveDebounce ??= new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _thumbnailSaveDebounce.Stop();
        _thumbnailSaveDebounce.Tick -= ThumbnailSaveDebounce_Tick;
        _thumbnailSaveDebounce.Tick += ThumbnailSaveDebounce_Tick;
        _thumbnailSaveDebounce.Start();
    }

    private void ThumbnailSaveDebounce_Tick(object? sender, EventArgs e)
    {
        _thumbnailSaveDebounce?.Stop();
        SaveRecoveryState();
    }

    private void SeekMainPreviewToMarkerMs(double markerMs)
    {
        if (ActiveVideoHost?.IpcClient == null)
        {
            return;
        }

        _isCurrentlyFrozen = false;
        TransportTrace("marker-seek", "pause");   // TRANSPORT_TRACE_01
        _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        _ = SeekInternal(markerMs / 1000.0);
    }


    private void UpdateTimelineMarkers()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineMarkersCanvas");
        var bottomCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineBottomCanvas");
        var scaleCanvas = this.FindControl<Avalonia.Controls.Canvas>("TimelineScaleCanvas");
        if (canvas == null || ActiveVideoHost?.IpcClient == null) return;

        double duration = ActiveVideoHost.IpcClient.Duration;

        if (duration <= 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            canvas.Children.Clear();
            var playheadBadge = this.FindControl<Avalonia.Controls.Border>("PlayheadBadge");
            if (playheadBadge != null && !canvas.Children.Contains(playheadBadge))
                canvas.Children.Add(playheadBadge);
            bottomCanvas?.Children.Clear();
            scaleCanvas?.Children.Clear();
            double canvasWidth = canvas.Bounds.Width;
            if (canvasWidth <= 0) return;
            double ClampLabelLeft(double desired, double approxWidth)
                => Math.Max(0, Math.Min(Math.Max(0, canvasWidth - approxWidth), desired));
            const double trimMarkerWidth = 3.0;
            const double trimMarkerTop = 0.0;
            double trimMarkerHeight = Math.Max(1, canvas.Bounds.Height);

            if (_trimStartSet && _trimEndMs > _trimStartMs)
            {
                double regStartX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                double regEndX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                var regionRect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 128, 128, 128)),
                    Width = Math.Max(2, regEndX - regStartX),
                    Height = trimMarkerHeight,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(regionRect, regStartX);
                Avalonia.Controls.Canvas.SetTop(regionRect, trimMarkerTop);
                canvas.Children.Add(regionRect);
                _regionRectRef = regionRect;
            }


            // ══════════════════════════════════════════════════════════════════════════════
            // CUT_01 — CUTS ARE DRAWN AS FIXED-WIDTH MARKERS, NOT AS BLOCKS.
            //
            // This is THE design decision that makes the whole feature workable, and it is the
            // answer to the "invisible ghost" objection that sank the original proposal. A cut
            // occupies ZERO time in the finished video, so on an output-time ruler it is zero
            // pixels wide — there is nothing to grab, nothing to drag, nothing to point a coach
            // cursor at. Every professional editor solves this the same way: draw a constant-size
            // glyph at the join. It is always CutMarkerWidth px, whether it removed half a second
            // or half an hour, so it is always clickable and never "violently glitches".
            //
            // This canvas is a SOURCE-time ruler (it is drawn against the full clip duration), so
            // the deleted span CAN be shaded here to show what is gone. The zero-width problem is
            // real on the OUTPUT ruler — which is exactly why cuts are set on this screen and not
            // in the Granular editor, whose timeline is output time.
            // ══════════════════════════════════════════════════════════════════════════════
            if (_cuts.Count > 0)
            {
                const double CutMarkerWidth = 9.0;
                // TONE_01: the deleted-span shading and its handle both come off AppDangerColor
                // now, so darkening the token darkens the cut markers with everything else.
                var cutBase = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Avalonia.Media.Color.FromRgb(168, 50, 50));
                var cutFill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(150, cutBase.R, cutBase.G, cutBase.B));
                var cutEdge = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255,
                    (byte)Math.Min(255, cutBase.R + 60), (byte)Math.Min(255, cutBase.G + 45), (byte)Math.Min(255, cutBase.B + 45)));

                foreach (var cut in _cuts)
                {
                    double cx0 = (cut.StartMs / 1000.0 / duration) * canvasWidth;
                    double cx1 = (cut.EndMs / 1000.0 / duration) * canvasWidth;

                    var removedBand = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Fill = cutFill,
                        Width = Math.Max(2, cx1 - cx0),
                        Height = trimMarkerHeight,
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(removedBand, cx0);
                    Avalonia.Controls.Canvas.SetTop(removedBand, trimMarkerTop);
                    canvas.Children.Add(removedBand);

                    // The constant-size handle. Centred on the deleted span so it stays reachable
                    // even when the span itself is thinner than the glyph.
                    var handle = new Avalonia.Controls.Border
                    {
                        Background = cutEdge,
                        CornerRadius = new Avalonia.CornerRadius(2),
                        Width = CutMarkerWidth,
                        Height = trimMarkerHeight,
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(handle, Math.Max(0, (cx0 + cx1) / 2.0 - CutMarkerWidth / 2.0));
                    Avalonia.Controls.Canvas.SetTop(handle, trimMarkerTop);
                    ToolTip.SetTip(handle,
                        $"Deleted: {TimeSpan.FromMilliseconds(cut.StartMs):mm\\:ss\\.f} to " +
                        $"{TimeSpan.FromMilliseconds(cut.EndMs):mm\\:ss\\.f}");
                    canvas.Children.Add(handle);
                }
            }

            if (_speedSegments != null && _speedSegments.Count > 0)
            {
                foreach (var seg in _speedSegments)
                {
                    double segStartX = (seg.StartMs / 1000.0 / duration) * canvasWidth;
                    double segEndX = (seg.EndMs / 1000.0 / duration) * canvasWidth;
                    double segW = Math.Max(2, segEndX - segStartX);

                    Avalonia.Media.Color segColor;
                    double speed = seg.Speed;
                    double baseSpd = _baseSpeed;

                    if (speed < 0.01)
                    {
                        segColor = Avalonia.Media.Color.FromArgb(230, 96, 165, 250);
                    }
                    else if (speed < baseSpd - 0.0001)
                    {
                        double factor = Math.Clamp((baseSpd - speed) / Math.Max(0.001, baseSpd - 0.1), 0.0, 1.0);
                        byte alpha = (byte)(51 + factor * (230 - 51));
                        // TONE_01 — mirrors GranularSpeedEditorWindow.GetSegmentOverlayColor exactly.
                var slowC = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Avalonia.Media.Color.FromRgb(168, 50, 50));
                segColor = Avalonia.Media.Color.FromArgb(alpha, slowC.R, slowC.G, slowC.B);
                    }
                    else
                    {
                        double factor = Math.Clamp((speed - baseSpd) / Math.Max(0.001, 4.1 - baseSpd), 0.0, 1.0);
                        byte alpha = (byte)(51 + factor * (230 - 51));
                        // TONE_01
                var fastC = Infrastructure.ThemeResources.Colour(this, "AppSuccessColor", Avalonia.Media.Color.FromRgb(63, 156, 107));
                segColor = Avalonia.Media.Color.FromArgb(alpha, fastC.R, fastC.G, fastC.B);
                    }

                    var segRect = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Width = segW,
                        Height = trimMarkerHeight,
                        Fill = new Avalonia.Media.SolidColorBrush(segColor),
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(segRect, segStartX);
                    Avalonia.Controls.Canvas.SetTop(segRect, trimMarkerTop);
                    canvas.Children.Add(segRect);
                }
            }

            // ══════════════════════════════════════════════════════════════════════════
            // MEME_06 — ONE CLOWN, AND IT IS DISPLAY ONLY.
            //
            // This canvas is a SOURCE-time ruler. A meme occupies zero source seconds, so its start
            // and its end are the same instant here and two heads would land on the same pixel —
            // there is no band to grab and nothing to drag along. One head, at the moment of
            // gameplay it interrupts, so you can see at a glance that the video has memes in it and
            // where; the block with its two ends lives in the Speed Editor, where output time gives
            // it a real width.
            // ══════════════════════════════════════════════════════════════════════════
            if (_memePlacements.Count > 0)
            {
                foreach (var meme in _memePlacements)
                {
                    double memeAbsSec = (_trimStartMs / 1000.0) + meme.AtSourceSecRelative;
                    double memeX = (memeAbsSec / duration) * canvasWidth;

                    var memeMarker = CreateMemeTimelineCameraIcon();
                    memeMarker.IsHitTestVisible = false;
                    Avalonia.Controls.ToolTip.SetTip(memeMarker,
                        $"Meme: {System.IO.Path.GetFileName(meme.FilePath)} ({meme.DurationSec:0.0}s)\n" +
                        "Your gameplay pauses here and the meme plays, then carries on from this exact frame.\n" +
                        "Open GRANULAR SPEED to move or remove it.");
                    Avalonia.Controls.Canvas.SetTop(memeMarker, -79);
                    Avalonia.Controls.Canvas.SetLeft(memeMarker, ClampTimelineCameraLeft(memeX, canvasWidth));
                    canvas.Children.Add(memeMarker);
                }
            }

            if (_freezeTimeMs >= 0)
            {
                double freezeX = (_freezeTimeMs / 1000.0 / duration) * canvasWidth;
                var freezeCamera = CreateTimelineCameraIcon(false, 0, out _, out _);
                Avalonia.Controls.Canvas.SetTop(freezeCamera, -79);
                Avalonia.Controls.Canvas.SetLeft(freezeCamera, ClampTimelineCameraLeft(freezeX, canvasWidth));
                Avalonia.Controls.ToolTip.SetTip(freezeCamera, $"Freeze Image set at {FormatTime(TimeSpan.FromMilliseconds(_freezeTimeMs))} for {_freezeDurationS:0.0}s");
                canvas.Children.Add(freezeCamera);
                
                var freezeLine = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 4,
                    Height = trimMarkerHeight,
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(96, 165, 250)),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(freezeLine, freezeX);
                Avalonia.Controls.Canvas.SetTop(freezeLine, trimMarkerTop);
                canvas.Children.Add(freezeLine);
            }

            double tickInterval = 5;
            if (duration > 3600) tickInterval = 300;
            else if (duration > 1800) tickInterval = 60;
            else if (duration > 300) tickInterval = 30;
            else if (duration > 60) tickInterval = 10;

            for (double t = 0; t <= duration; t += tickInterval)
            {
                double tx = (t / duration) * canvasWidth;

                var tickLine = new Avalonia.Controls.Shapes.Rectangle { Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 255, 255, 255)), Width = 1, Height = canvas.Bounds.Height, IsHitTestVisible = false };
                Avalonia.Controls.Canvas.SetLeft(tickLine, tx);
                canvas.Children.Add(tickLine);

                bool shouldShowTickLabel = t > 0.001 && duration - t > 0.001;
                if (scaleCanvas != null && shouldShowTickLabel)
                {
                    var tickText = new TextBlock {
                        Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(180, 255, 255, 255)),
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(9)
                    };
                    Avalonia.Controls.Canvas.SetLeft(tickText, ClampLabelLeft(tx + 2, 36));
                    Avalonia.Controls.Canvas.SetTop(tickText, 0);
                    scaleCanvas.Children.Add(tickText);
                }
            }

            if (!_thumbnailSet)
            {
                double effectiveEnd = _trimEndMs > 0 ? _trimEndMs : duration * 1000.0;
                double effectiveStart = _trimStartSet ? _trimStartMs : 0;
                _thumbnailPosMs = effectiveStart + (effectiveEnd - effectiveStart) * 0.6666;
            }

            {
                double thumbMs = Math.Clamp(_thumbnailPosMs, 0, duration * 1000.0);
                double thumbX = (thumbMs / 1000.0 / duration) * canvasWidth;


                _thumbnailCameraControl = CreateTimelineCameraIcon(
                    _isThumbnailMarkerSelected || _isDraggingThumbnailMarker,
                    _marchingAntsOffset,
                    out _thumbnailMarkerIconAntsRef,
                    out _thumbnailMarkerLineAntsRef);
                Avalonia.Controls.ToolTip.SetTip(_thumbnailCameraControl, "This exact frame will be used as the cover picture (thumbnail) for your video when you share it.");
                Avalonia.Controls.Canvas.SetTop(_thumbnailCameraControl, -79);
                Avalonia.Controls.Canvas.SetLeft(_thumbnailCameraControl, ClampTimelineCameraLeft(thumbX, canvasWidth));
                AttachThumbnailCameraMarkerInteractions(_thumbnailCameraControl, canvas, duration);
                canvas.Children.Add(_thumbnailCameraControl);
            }

            if (_trimStartSet)
            {
                double startX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                var startHitBox = new Avalonia.Controls.Border {
                    Width = 24, Height = trimMarkerHeight, Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                var startRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.SeaGreen, Width = trimMarkerWidth, Height = trimMarkerHeight, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                startHitBox.Child = startRect;

                Avalonia.Controls.Canvas.SetLeft(startHitBox, startX - 12);
                Avalonia.Controls.Canvas.SetTop(startHitBox, trimMarkerTop);

                startHitBox.PointerEntered += (s,e) => { startHitBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 46, 139, 87)); startRect.Fill = Avalonia.Media.Brushes.MediumSeaGreen; };
                startHitBox.PointerExited += (s,e) => { startHitBox.Background = Avalonia.Media.Brushes.Transparent; startRect.Fill = Avalonia.Media.Brushes.SeaGreen; };
                startHitBox.PointerPressed += (s,e) => {
                    if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
                    _draggingStartMarker = true;
                    e.Pointer.Capture(startHitBox);
                    e.Handled = true;
                };
                
                canvas.Children.Add(startHitBox);

                var startText = new TextBlock { Text = "START", Foreground = Avalonia.Media.Brushes.SeaGreen, FontSize = Infrastructure.ThemeManager.ScaledFontSize(9), FontWeight = Avalonia.Media.FontWeight.Bold, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2,0) };
                if (scaleCanvas != null)
                {
                    Avalonia.Controls.Canvas.SetLeft(startText, ClampLabelLeft(startX + 5, 36));
                    Avalonia.Controls.Canvas.SetTop(startText, 0);
                    scaleCanvas.Children.Add(startText);
                }

                startHitBox.PointerMoved += (s,e) => {
                    if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) {
                        if (_draggingStartMarker) {
                            _draggingStartMarker = false;
                            try { e.Pointer.Capture(null); } catch (System.Exception) { /* ISSUE_13: releasing a capture the OS already dropped. Nothing to report. */ }
                        }
                        return;
                    }
                    if (_draggingStartMarker) {
                        var pt = e.GetPosition(canvas);
                        double newX = Math.Max(0, Math.Min(pt.X, canvasWidth));
                        
                        double currentEndSec = _trimEndMs / 1000.0;
                        double currentEndX = (currentEndSec / duration) * canvasWidth;
                        
                        if (_trimEndMs > 0 && newX >= currentEndX) {
                            newX = currentEndX - 1;
                        }
                        
                        double newStartSec = (newX / canvasWidth) * duration;
                        _trimStartMs = newStartSec * 1000.0;
                        Avalonia.Controls.Canvas.SetLeft(startHitBox, newX - 12);
                        Avalonia.Controls.Canvas.SetLeft(startText, ClampLabelLeft(newX + 5, 36));
                        UpdateDraggingVisuals(canvasWidth, duration);
                        _ = SeekInternal(newStartSec);
                    }
                };

                startHitBox.PointerReleased += (s,e) => {
                    if (_draggingStartMarker) {
                        _draggingStartMarker = false;
                        e.Pointer.Capture(null);

                        SetTrimStart(_trimStartMs);
                        RuntimeLog.Info("UI", $"Trim START marker dragged to {TimeSpan.FromMilliseconds(_trimStartMs):hh\\:mm\\:ss\\.ff}.");

                        if (ActiveVideoHost?.IpcClient?.IsPaused == true)
                        {
                            _ = SeekInternal(_trimStartMs / 1000.0);
                        }

                        var markStartBtn = this.FindControl<Avalonia.Controls.Button>("MarkStartButton");
                        if (markStartBtn != null) markStartBtn.Content = "START: " + FormatTime(TimeSpan.FromMilliseconds(_trimStartMs));
                        PlayUiSound();
                        ShowTacticalFeedback("🏁 " + TimeSpan.FromMilliseconds(_trimStartMs).ToString("mm\\:ss\\.ff"));
                        ShowTimelineGlow(_trimStartMs, Avalonia.Media.Brushes.SeaGreen);
                        UpdateTimelineMarkers();
                        UpdateEstimatedQuality(); 
                        SaveRecoveryState(); 
                        UpdateDraggingVisuals(canvasWidth, duration);
                    }
                };
            }

            if (_trimEndMs > 0)
            {
                double endX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                var endHitBox = new Avalonia.Controls.Border {
                    Width = 24, Height = trimMarkerHeight, Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                var endRect = new Avalonia.Controls.Shapes.Rectangle { Fill = Avalonia.Media.Brushes.SeaGreen, Width = trimMarkerWidth, Height = trimMarkerHeight, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                endHitBox.Child = endRect;

                Avalonia.Controls.Canvas.SetLeft(endHitBox, endX - 12);
                Avalonia.Controls.Canvas.SetTop(endHitBox, trimMarkerTop);

                endHitBox.PointerEntered += (s,e) => { endHitBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 46, 139, 87)); endRect.Fill = Avalonia.Media.Brushes.MediumSeaGreen; };
                endHitBox.PointerExited += (s,e) => { endHitBox.Background = Avalonia.Media.Brushes.Transparent; endRect.Fill = Avalonia.Media.Brushes.SeaGreen; };

                endHitBox.PointerPressed += (s,e) => {
                    if (e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) {
                        _draggingEndMarker = true;
                        e.Pointer.Capture(endHitBox);
                        UpdateDraggingVisuals(canvasWidth, duration);

                        e.Handled = true;
                    }
                };
                canvas.Children.Add(endHitBox);

                var endText = new TextBlock { Text = "END", Foreground = Avalonia.Media.Brushes.SeaGreen, FontSize = Infrastructure.ThemeManager.ScaledFontSize(9), FontWeight = Avalonia.Media.FontWeight.Bold, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000")), Padding = new Avalonia.Thickness(2,0) };
                if (scaleCanvas != null)
                {
                    Avalonia.Controls.Canvas.SetLeft(endText, ClampLabelLeft(endX - 28, 28));
                    Avalonia.Controls.Canvas.SetTop(endText, 0);
                    scaleCanvas.Children.Add(endText);
                }

                endHitBox.PointerMoved += (s,e) => {
                    if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) {
                        if (_draggingEndMarker) {
                            _draggingEndMarker = false;
                            try { e.Pointer.Capture(null); } catch (System.Exception) { /* ISSUE_13: releasing a capture the OS already dropped. Nothing to report. */ }
                        }
                        return;
                    }
                    if (_draggingEndMarker) {
                        var pt = e.GetPosition(canvas);
                        double newX = Math.Max(0, Math.Min(pt.X, canvasWidth));
                        
                        double currentStartSec = _trimStartMs / 1000.0;
                        double currentStartX = (currentStartSec / duration) * canvasWidth;
                        
                        if (newX <= currentStartX) {
                            newX = currentStartX + 1;
                        }
                        
                        double newEndSec = (newX / canvasWidth) * duration;
                        _trimEndMs = newEndSec * 1000.0;
                        _prewarmArmed = true;
                        SchedulePrewarm();
                        Avalonia.Controls.Canvas.SetLeft(endHitBox, newX - 12);
                        if (scaleCanvas != null) Avalonia.Controls.Canvas.SetLeft(endText, ClampLabelLeft(newX - 28, 28));
                        UpdateDraggingVisuals(canvasWidth, duration);
                        _ = SeekInternal(newEndSec);
                    }
                };
                endHitBox.PointerReleased += (s,e) => {
                    if (_draggingEndMarker) {
                        _draggingEndMarker = false;
                        e.Pointer.Capture(null);
                        RuntimeLog.Info("UI", $"Trim END marker dragged to {TimeSpan.FromMilliseconds(_trimEndMs):hh\\:mm\\:ss\\.ff}.");

                        if (ActiveVideoHost?.IpcClient?.IsPaused == true)
                        {
                            _ = SeekInternal(_trimEndMs / 1000.0);
                        }

                        var markEndBtn = this.FindControl<Avalonia.Controls.Button>("MarkEndButton");
                        if (markEndBtn != null) markEndBtn.Content = "END: " + FormatTime(TimeSpan.FromMilliseconds(_trimEndMs));
                        PlayUiSound();
                        ShowTacticalFeedback("🏁 " + TimeSpan.FromMilliseconds(_trimEndMs).ToString("mm\\:ss\\.ff"));
                        ShowTimelineGlow(_trimEndMs, Avalonia.Media.Brushes.SeaGreen);
                        UpdateTimelineMarkers();
                        UpdateEstimatedQuality(); 
                        SaveRecoveryState(); 
                        UpdateDraggingVisuals(canvasWidth, duration);
                    }
                };
            }

            if (_musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath))
            {
                double mStartX = (_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth;
                double mEndX = (_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth;

                var musicRect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(80, 255, 105, 180)),
                    Width = Math.Max(2, mEndX - mStartX),
                    Height = trimMarkerHeight,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    IsHitTestVisible = true
                };

                if (_isMusicBlockFocused)
                {
                    musicRect.Stroke = Avalonia.Media.Brushes.Yellow;
                    musicRect.StrokeThickness = 1;
                    musicRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                    musicRect.StrokeDashOffset = _marchingAntsOffset;
                }

                _musicBlockRectRef = musicRect;

                var removeMusicMenu = new Avalonia.Controls.ContextMenu();
                var removeMusicItem = new Avalonia.Controls.MenuItem { Header = "Remove Music", Icon = new TextBlock { Text = "🗑️", Margin = new Avalonia.Thickness(0,0,5,0) } };
                removeMusicItem.Click += (s, ev) => {
                    _musicWizardResult = null;
                    SetMusicButtonActive(false);
                    UpdateTimelineMarkers();
                };
                removeMusicMenu.ItemsSource = new[] { removeMusicItem };
                musicRect.ContextMenu = removeMusicMenu;

                musicRect.KeyDown += (s, e) => {
                    if (e.Key == Avalonia.Input.Key.Delete) {
                        _musicWizardResult = null;
                        SetMusicButtonActive(false);
                        UpdateTimelineMarkers();
                    }
                };

                Avalonia.Controls.Canvas.SetLeft(musicRect, mStartX);
                Avalonia.Controls.Canvas.SetTop(musicRect, trimMarkerTop);
                if (bottomCanvas != null) bottomCanvas.Children.Add(musicRect);
                else canvas.Children.Add(musicRect);

                var startNoteText = new TextBlock { Text = "♪", FontFamily = new Avalonia.Media.FontFamily("Segoe UI Symbol"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), FontSize = Infrastructure.ThemeManager.ScaledFontSize(52), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 52, TextAlignment = Avalonia.Media.TextAlignment.Center, Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 }, IsHitTestVisible = false };
                var startStick = new Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 40, Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), IsHitTestVisible = false };
                
                var startHitBox = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 41,
                    Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };

                var startCanvas = new Avalonia.Controls.Canvas { Width = 52, Height = 80, ClipToBounds = false };
                Avalonia.Controls.Canvas.SetLeft(startNoteText, 0);
                Avalonia.Controls.Canvas.SetTop(startStick, 58);
                Avalonia.Controls.Canvas.SetLeft(startStick, 24);
                Avalonia.Controls.Canvas.SetTop(startHitBox, 14);
                Avalonia.Controls.Canvas.SetLeft(startHitBox, 0);

                startCanvas.Children.Add(startNoteText);
                startCanvas.Children.Add(startStick);
                startCanvas.Children.Add(startHitBox);

                var musicStartBorder = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 80,
                    Child = startCanvas
                };
                
                startHitBox.PointerEntered += (s, e) => {
                    startNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 180, 0));
                    startNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Color.FromArgb(255, 255, 180, 0), BlurRadius = 15, Opacity = 0.9 };
                };
                startHitBox.PointerExited += (s, e) => {
                    startNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180));
                    startNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 };
                };

                _musicStartPopupRef = musicStartBorder;
                Avalonia.Controls.Canvas.SetTop(musicStartBorder, -72);
                Avalonia.Controls.Canvas.SetLeft(musicStartBorder, mStartX - 26);
                musicStartBorder.ZIndex = 100;
                canvas.Children.Add(musicStartBorder);

                var endNoteText = new TextBlock { Text = "♪", FontFamily = new Avalonia.Media.FontFamily("Segoe UI Symbol"), Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), FontSize = Infrastructure.ThemeManager.ScaledFontSize(52), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 52, TextAlignment = Avalonia.Media.TextAlignment.Center, Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 }, IsHitTestVisible = false };
                var endStick = new Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 40, Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180)), IsHitTestVisible = false };
                
                var endHitBox = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 41,
                    Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };

                var endCanvas = new Avalonia.Controls.Canvas { Width = 52, Height = 80, ClipToBounds = false };
                Avalonia.Controls.Canvas.SetLeft(endNoteText, 0);
                Avalonia.Controls.Canvas.SetTop(endStick, 58);
                Avalonia.Controls.Canvas.SetLeft(endStick, 24);
                Avalonia.Controls.Canvas.SetTop(endHitBox, 14);
                Avalonia.Controls.Canvas.SetLeft(endHitBox, 0);

                endCanvas.Children.Add(endNoteText);
                endCanvas.Children.Add(endStick);
                endCanvas.Children.Add(endHitBox);

                var musicEndBorder = new Avalonia.Controls.Border {
                    Width = 52,
                    Height = 80,
                    Child = endCanvas
                };

                endHitBox.PointerEntered += (s, e) => {
                    endNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 180, 0));
                    endNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Color.FromArgb(255, 255, 180, 0), BlurRadius = 15, Opacity = 0.9 };
                };
                endHitBox.PointerExited += (s, e) => {
                    endNoteText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, 255, 105, 180));
                    endNoteText.Effect = new Avalonia.Media.DropShadowDirectionEffect { Color = Avalonia.Media.Colors.Black, BlurRadius = 4, Opacity = 0.8 };
                };

                _musicEndPopupRef = musicEndBorder;
                Avalonia.Controls.Canvas.SetTop(musicEndBorder, -72);
                Avalonia.Controls.Canvas.SetLeft(musicEndBorder, mEndX - 26);
                musicEndBorder.ZIndex = 100;
                canvas.Children.Add(musicEndBorder);

                double dragStartPointerX = 0;
                double dragInitialStartSec = 0;
                double dragInitialEndSec = 0;

                startHitBox.PointerPressed += (s, e) => {
                    _isMusicBlockFocused = false;
                    _draggingMusicStart = true;
                    e.Pointer.Capture(startHitBox);
                    e.Handled = true;
                };
                startHitBox.PointerReleased += (s, e) => {
                    _draggingMusicStart = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                startHitBox.PointerMoved += (s, e) => {
                    if (_draggingMusicStart) {
                        double currentX = e.GetPosition(canvas).X;

                        double markStartX = (_trimStartMs / 1000.0 / duration) * canvasWidth;
                        if (currentX < markStartX) currentX = markStartX;
                        if (Math.Abs(currentX - markStartX) < 10) currentX = markStartX;

                        double newStart = (currentX / canvasWidth) * duration;
                        if (newStart < 0) newStart = 0;
                        if (newStart >= _musicWizardResult.TimelineEndSeconds - 0.5) newStart = _musicWizardResult.TimelineEndSeconds - 0.5;
                        _musicWizardResult.TimelineStartSeconds = newStart;
                        double nx = (newStart / duration) * canvasWidth;
                        Avalonia.Controls.Canvas.SetLeft(musicRect, nx);
                        Avalonia.Controls.Canvas.SetLeft(musicStartBorder, nx - 26);
                        musicRect.Width = Math.Max(2, ((_musicWizardResult.TimelineEndSeconds / duration) * canvasWidth) - nx);
                    }
                };

                endHitBox.PointerPressed += (s, e) => {
                    _isMusicBlockFocused = false;
                    _draggingMusicEnd = true;
                    e.Pointer.Capture(endHitBox);
                    e.Handled = true;
                };
                endHitBox.PointerReleased += (s, e) => {
                    _draggingMusicEnd = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                endHitBox.PointerMoved += (s, e) => {
                    if (_draggingMusicEnd) {
                        double currentX = e.GetPosition(canvas).X;

                        double markEndX = (_trimEndMs / 1000.0 / duration) * canvasWidth;
                        if (currentX > markEndX) currentX = markEndX;
                        if (Math.Abs(currentX - markEndX) < 10) currentX = markEndX;

                        double newEnd = (currentX / canvasWidth) * duration;
                        if (newEnd > duration) newEnd = duration;
                        if (newEnd <= _musicWizardResult.TimelineStartSeconds + 0.5) newEnd = _musicWizardResult.TimelineStartSeconds + 0.5;
                        _musicWizardResult.TimelineEndSeconds = newEnd;
                        double nx = (newEnd / duration) * canvasWidth;
                        Avalonia.Controls.Canvas.SetLeft(musicEndBorder, nx - 26);
                        musicRect.Width = Math.Max(2, nx - ((_musicWizardResult.TimelineStartSeconds / duration) * canvasWidth));
                    }
                };

                musicRect.PointerPressed += (s, e) => {
                    if (e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed) return;

                    if (!_isMusicBlockFocused)
                    {
                        _isMusicBlockFocused = true;
                        _suppressNextMusicDeselect = true;
                        musicRect.Stroke = Avalonia.Media.Brushes.Yellow;
                        musicRect.StrokeThickness = 1;
                        musicRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                    }
                    if (e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed)
                    {
                        _draggingMusicBlock = true;
                        dragStartPointerX = e.GetPosition(canvas).X;
                        dragInitialStartSec = _musicWizardResult.TimelineStartSeconds;
                        dragInitialEndSec = _musicWizardResult.TimelineEndSeconds;
                        e.Pointer.Capture(musicRect);
                        e.Handled = true;
                    }
                };
                musicRect.PointerReleased += (s, e) => {
                    _draggingMusicBlock = false;
                    e.Pointer.Capture(null);
                    UpdateTimelineMarkers();
                    SaveRecoveryState();
                };
                musicRect.PointerMoved += (s, e) => {
                    if (_draggingMusicBlock) {
                        double currentX = e.GetPosition(canvas).X;
                        double dxSeconds = ((currentX - dragStartPointerX) / canvasWidth) * duration;
                        double dur = dragInitialEndSec - dragInitialStartSec;
                        double rawNewStart = dragInitialStartSec + dxSeconds;
                        double rawNewEnd = dragInitialEndSec + dxSeconds;

                        double markStartSec = _trimStartMs / 1000.0;
                        double markEndSec = _trimEndMs / 1000.0;

                        if (rawNewStart < markStartSec) {
                            rawNewStart = markStartSec;
                            rawNewEnd = rawNewStart + dur;
                        }
                        if (rawNewEnd > markEndSec) {
                            rawNewEnd = markEndSec;
                            rawNewStart = rawNewEnd - dur;
                        }

                        double distStartToMarkStart = Math.Abs((rawNewStart / duration) * canvasWidth - (markStartSec / duration) * canvasWidth);
                        double distEndToMarkEnd = Math.Abs((rawNewEnd / duration) * canvasWidth - (markEndSec / duration) * canvasWidth);

                        double newStart = rawNewStart;
                        double newEnd = rawNewEnd;

                        if (distStartToMarkStart < 10 && distStartToMarkStart <= distEndToMarkEnd)
                        {
                            newStart = markStartSec;
                            newEnd = newStart + dur;
                        }
                        else if (distEndToMarkEnd < 10)
                        {
                            newEnd = markEndSec;
                            newStart = newEnd - dur;
                        }

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

                        double nStartX = (newStart / duration) * canvasWidth;
                        double nEndX = (newEnd / duration) * canvasWidth;

                        Avalonia.Controls.Canvas.SetLeft(musicRect, nStartX);
                        Avalonia.Controls.Canvas.SetLeft(musicStartBorder, nStartX - 20);
                        Avalonia.Controls.Canvas.SetLeft(musicEndBorder, nEndX - 20);
                    }
                };
            }
        });
    }

    /// <summary>POPSICLE_01 — x:Name of the visible stick inside a timeline camera/magnifier.</summary>
    public const string TimelineCameraStickName = "TimelineCameraStick";

    /// <summary>
    /// POPSICLE_01 — the stick length every marker is BUILT with: head bottom (local y 56) down to
    /// the bottom of the 103px control. Correct for the Main App's 32px marker canvas, which is the
    /// only caller that leaves it alone.
    /// </summary>
    public const double TimelineCameraDefaultStickPx = 47;

    /// <summary>POPSICLE_01 — local y at which the stick starts, i.e. the bottom of the head.</summary>
    public const double TimelineCameraStickTopPx = 56;

    /// <summary>
    /// POPSICLE_01 — LENGTHEN ONE MARKER'S STICK SO THE POPSICLE REACHES THE BOTTOM OF THE TIMELINE.
    ///
    /// <para>
    /// The marker is built 52x103 with a 47px stick, which was sized for the Main App's single
    /// 32px marker canvas. The Granular editor mounts the same control on a marker overlay that
    /// spans the ruler AND BOTH 60px lanes (~150px), so the stick died roughly halfway down the
    /// upper lane and the popsicle read as floating rather than pointing at an instant.
    /// </para>
    /// <para>
    /// ⚠️ THE STICK IS STRETCHED, THE MARKER IS NOT MOVED. Dropping the whole control would take
    /// the head down with it and destroy the shape — the head must stay above the ruler. Callers
    /// pass the height they need; every caller that does not call this is bit-identical to before,
    /// which is what keeps the Main App timeline untouched.
    /// </para>
    /// <para>
    /// ⚠️ ONLY THE DRAWN STICK MOVES — NOT `stemHit`. Its 16x47 grab column is deliberately short
    /// (HITBOX_02): a full-height grab box over a staggered pair swallows the lower head's clicks,
    /// which is the exact bug HITBOX_02 exists to prevent. A longer VISIBLE line costs nothing
    /// because it is `IsHitTestVisible = false`.
    /// </para>
    /// </summary>
    public static void StretchTimelineCameraStick(Control marker, double stickHeightPx)
    {
        if (marker is not Canvas outer) return;
        double h = Math.Max(TimelineCameraDefaultStickPx, stickHeightPx);

        foreach (var child in outer.Children)
        {
            if (child is Avalonia.Controls.Shapes.Rectangle r && r.Name == TimelineCameraStickName)
            {
                r.Height = h;
                return;
            }
        }
    }

    public static Control CreateTimelineCameraIcon()
    {
        return CreateTimelineCameraIcon(false, 0, out _, out _);
    }

    public static Control CreateTimelineCameraIcon(
        bool isSelected,
        double marchingAntsOffset,
        out Avalonia.Controls.Shapes.Rectangle iconAnts,
        out Avalonia.Controls.Shapes.Rectangle lineAnts)
    {
        var icon = new Border
        {
            Width = 36,
            Height = 28,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(220, 15, 23, 42)),
            BorderBrush = Avalonia.Media.Brushes.Gold,
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(4),
            IsHitTestVisible = false
        };

        var hoverIconGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconGlow",
            Width = 44,
            Height = 36,
            Stroke = Avalonia.Media.Brushes.Gold,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(hoverIconGlow, 4);
        Avalonia.Controls.Canvas.SetTop(hoverIconGlow, 24);

        var hoverLineGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineGlow",
            Width = 8,
            Height = 53,
            Stroke = Avalonia.Media.Brushes.Gold,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(hoverLineGlow, 20);
        Avalonia.Controls.Canvas.SetTop(hoverLineGlow, 53);

        var canvas = new Canvas { Width = 36, Height = 28 };
        var top = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12,
            Height = 6,
            Fill = Avalonia.Media.Brushes.Gold,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(top, 6);
        Avalonia.Controls.Canvas.SetTop(top, 2);
        canvas.Children.Add(top);

        var lens = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = Avalonia.Media.Brushes.Gold,
            StrokeThickness = 2.8,
            Fill = Avalonia.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(lens, 12);
        Avalonia.Controls.Canvas.SetTop(lens, 10);
        canvas.Children.Add(lens);

        var flash = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = Avalonia.Media.Brushes.Gold,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(flash, 26);
        Avalonia.Controls.Canvas.SetTop(flash, 8);
        canvas.Children.Add(flash);

        icon.Child = canvas;

        var outerCanvas = new Canvas
        {
            Width = 52,
            Height = 103,
            ClipToBounds = false,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Focusable = true
        };

        var headHit = new Border
        {
            Width = 52,
            Height = 36,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        Avalonia.Controls.Canvas.SetLeft(headHit, 0);
        Avalonia.Controls.Canvas.SetTop(headHit, 24);
        outerCanvas.Children.Add(headHit);

        var stemHit = new Border
        {
            Width = 16,
            Height = 47,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        Avalonia.Controls.Canvas.SetLeft(stemHit, 18);
        Avalonia.Controls.Canvas.SetTop(stemHit, 56);
        outerCanvas.Children.Add(stemHit);
        outerCanvas.Children.Add(hoverIconGlow);
        outerCanvas.Children.Add(hoverLineGlow);
        Avalonia.Controls.Canvas.SetTop(icon, 28);
        Avalonia.Controls.Canvas.SetLeft(icon, 8);
        outerCanvas.Children.Add(icon);

        var line = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = TimelineCameraStickName,
            Width = 2,
            Height = TimelineCameraDefaultStickPx,
            Fill = Avalonia.Media.Brushes.Gold,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetTop(line, 56);
        Avalonia.Controls.Canvas.SetLeft(line, 23);
        outerCanvas.Children.Add(line);

        iconAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconAnts",
            Width = 36,
            Height = 28,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(iconAnts, 8);
        Avalonia.Controls.Canvas.SetTop(iconAnts, 28);
        outerCanvas.Children.Add(iconAnts);

        lineAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineAnts",
            Width = 6,
            Height = 49,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(lineAnts, 21);
        Avalonia.Controls.Canvas.SetTop(lineAnts, 55);
        outerCanvas.Children.Add(lineAnts);

        return outerCanvas;
    }

    public static Control CreateZoomTimelineCameraIcon()
    {
        return CreateZoomTimelineCameraIcon(false, 0, out _, out _);
    }

    public static Control CreateZoomTimelineCameraIcon(
        bool isSelected,
        double marchingAntsOffset,
        out Avalonia.Controls.Shapes.Rectangle iconAnts,
        out Avalonia.Controls.Shapes.Rectangle lineAnts)
    {
        var icon = new Border
        {
            Width = 42,
            Height = 34,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(220, 15, 23, 42)),
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(4),
            IsHitTestVisible = false
        };
        icon[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppZoomBrush");

        var hoverIconGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconGlow",
            Width = 50,
            Height = 42,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        hoverIconGlow[!Avalonia.Controls.Shapes.Rectangle.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppZoomBrush");
        Avalonia.Controls.Canvas.SetLeft(hoverIconGlow, 1);
        Avalonia.Controls.Canvas.SetTop(hoverIconGlow, 21);

        var hoverLineGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineGlow",
            Width = 8,
            Height = 53,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        hoverLineGlow[!Avalonia.Controls.Shapes.Rectangle.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppZoomBrush");
        Avalonia.Controls.Canvas.SetLeft(hoverLineGlow, 22);
        Avalonia.Controls.Canvas.SetTop(hoverLineGlow, 59);

        var txt = new Avalonia.Controls.TextBlock
        {
            Text = "🔍",
            FontSize = 17,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        txt[!Avalonia.Controls.TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppZoomBrush");
        icon.Child = txt;

        var outerCanvas = new Canvas
        {
            Width = 52,
            Height = 106,
            ClipToBounds = false,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Focusable = true
        };

        var headHit = new Border
        {
            Width = 52,
            Height = 42,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        Avalonia.Controls.Canvas.SetLeft(headHit, 0);
        Avalonia.Controls.Canvas.SetTop(headHit, 21);
        outerCanvas.Children.Add(headHit);

        var stemHit = new Border
        {
            Width = 16,
            Height = 47,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        Avalonia.Controls.Canvas.SetLeft(stemHit, 18);
        Avalonia.Controls.Canvas.SetTop(stemHit, 59);
        outerCanvas.Children.Add(stemHit);
        outerCanvas.Children.Add(hoverIconGlow);
        outerCanvas.Children.Add(hoverLineGlow);
        Avalonia.Controls.Canvas.SetTop(icon, 25);
        Avalonia.Controls.Canvas.SetLeft(icon, 5);
        outerCanvas.Children.Add(icon);

        var line = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = TimelineCameraStickName,
            Width = 2,
            Height = TimelineCameraDefaultStickPx,
            IsHitTestVisible = false
        };
        line[!Avalonia.Controls.Shapes.Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppZoomBrush");
        Avalonia.Controls.Canvas.SetTop(line, 59);
        Avalonia.Controls.Canvas.SetLeft(line, 25);
        outerCanvas.Children.Add(line);

        iconAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconAnts",
            Width = 42,
            Height = 34,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(iconAnts, 5);
        Avalonia.Controls.Canvas.SetTop(iconAnts, 25);
        outerCanvas.Children.Add(iconAnts);

        lineAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineAnts",
            Width = 6,
            Height = 49,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(lineAnts, 23);
        Avalonia.Controls.Canvas.SetTop(lineAnts, 58);
        outerCanvas.Children.Add(lineAnts);

        return outerCanvas;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // MEME_06 — THE CLOWN MARKER.
    //
    // Deliberately the SAME lollipop as the freeze camera and the zoom magnifier: a head, a stem,
    // and a hairline that runs all the way down to cross the ruler at the exact instant. A user who
    // has learned one of these markers has learned all three, and the crossing line is what makes
    // the position readable to the pixel rather than approximately.
    //
    // It differs from the other two in ONE way, and that difference is the feature: a freeze and a
    // zoom each expose two independently draggable ends, because their two ends are two separate
    // decisions. A meme's length is the meme file's own length — it is not a decision at all — so
    // its two markers are two views of ONE object. The Speed Editor therefore attaches drag to the
    // BAND BETWEEN them and to neither head. See AttachMemeBandInteractions.
    // ══════════════════════════════════════════════════════════════════════════════════════
    public static Control CreateMemeTimelineCameraIcon()
    {
        return CreateMemeTimelineCameraIcon(false, 0, out _, out _);
    }

    public static Control CreateMemeTimelineCameraIcon(
        bool isSelected,
        double marchingAntsOffset,
        out Avalonia.Controls.Shapes.Rectangle iconAnts,
        out Avalonia.Controls.Shapes.Rectangle lineAnts)
    {
        var icon = new Border
        {
            Width = 42,
            Height = 34,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(220, 15, 23, 42)),
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(4),
            IsHitTestVisible = false
        };
        icon[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppMemeBrush");

        var hoverIconGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconGlow",
            Width = 50,
            Height = 42,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        hoverIconGlow[!Avalonia.Controls.Shapes.Rectangle.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppMemeBrush");
        Avalonia.Controls.Canvas.SetLeft(hoverIconGlow, 1);
        Avalonia.Controls.Canvas.SetTop(hoverIconGlow, 21);

        var hoverLineGlow = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineGlow",
            Width = 8,
            Height = 53,
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false
        };
        hoverLineGlow[!Avalonia.Controls.Shapes.Rectangle.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppMemeBrush");
        Avalonia.Controls.Canvas.SetLeft(hoverLineGlow, 22);
        Avalonia.Controls.Canvas.SetTop(hoverLineGlow, 59);

        var txt = new Avalonia.Controls.TextBlock
        {
            Text = "🤡",
            FontSize = 17,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        txt[!Avalonia.Controls.TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppMemeBrush");
        icon.Child = txt;

        var outerCanvas = new Canvas
        {
            Width = 52,
            Height = 106,
            ClipToBounds = false,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Focusable = true
        };

        var headHit = new Border
        {
            Width = 52,
            Height = 42,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        Avalonia.Controls.Canvas.SetLeft(headHit, 0);
        Avalonia.Controls.Canvas.SetTop(headHit, 21);
        outerCanvas.Children.Add(headHit);

        var stemHit = new Border
        {
            Width = 16,
            Height = 47,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        Avalonia.Controls.Canvas.SetLeft(stemHit, 18);
        Avalonia.Controls.Canvas.SetTop(stemHit, 59);
        outerCanvas.Children.Add(stemHit);
        outerCanvas.Children.Add(hoverIconGlow);
        outerCanvas.Children.Add(hoverLineGlow);
        Avalonia.Controls.Canvas.SetTop(icon, 25);
        Avalonia.Controls.Canvas.SetLeft(icon, 5);
        outerCanvas.Children.Add(icon);

        var line = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = TimelineCameraStickName,
            Width = 2,
            Height = TimelineCameraDefaultStickPx,
            IsHitTestVisible = false
        };
        line[!Avalonia.Controls.Shapes.Rectangle.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppMemeBrush");
        Avalonia.Controls.Canvas.SetTop(line, 59);
        Avalonia.Controls.Canvas.SetLeft(line, 25);
        outerCanvas.Children.Add(line);

        iconAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraIconAnts",
            Width = 42,
            Height = 34,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(iconAnts, 5);
        Avalonia.Controls.Canvas.SetTop(iconAnts, 25);
        outerCanvas.Children.Add(iconAnts);

        lineAnts = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "TimelineCameraLineAnts",
            Width = 6,
            Height = 49,
            Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155")),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2),
            StrokeDashOffset = marchingAntsOffset,
            IsVisible = isSelected,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(lineAnts, 23);
        Avalonia.Controls.Canvas.SetTop(lineAnts, 58);
        outerCanvas.Children.Add(lineAnts);

        return outerCanvas;
    }

    public static double ClampTimelineCameraLeft(double markerCenterX, double canvasWidth)
    {
        const double markerWidth = 52.0;
        return markerCenterX - markerWidth / 2.0;
    }

    public static void SetTimelineCameraHover(Control marker, bool isHovered)
    {
        double opacity = isHovered ? 0.38 : 0.0;
        SetTimelineCameraHoverRecursive(marker, opacity);
    }

    private static void SetTimelineCameraHoverRecursive(Control control, double opacity)
    {
        if (control.Name is "TimelineCameraIconGlow" or "TimelineCameraLineGlow")
        {
            control.Opacity = opacity;
        }

        if (control is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Control childControl)
                {
                    SetTimelineCameraHoverRecursive(childControl, opacity);
                }
            }
        }
    }


}

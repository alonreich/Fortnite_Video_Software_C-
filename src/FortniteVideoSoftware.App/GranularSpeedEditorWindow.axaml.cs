using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Collections.Immutable;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FortniteVideoSoftware.Core.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App;

/// <summary>
/// Granular Speed Editor dialog window.
/// Mirrors the Python GranularSpeedEditor: lets the user mark time ranges and assign
/// a playback speed (including freeze-frame at 0x) to each segment.
/// </summary>
public partial class GranularSpeedEditorWindow : Window
{
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private readonly string _videoPath;
    private readonly double _trimStartMs;
    private readonly double _trimEndMs;

    private readonly List<SpeedSegment> _segments = new();

    private int _pendingStartMs = -1;
    private int _pendingEndMs   = -1;
    private double _pendingSpeed = 1.1;
    private double _baseSpeed    = 1.1;

    private bool _isSafeToClose = false;

    private bool _gpuLiveZoomPreview;
    private string _lastLiveCrop = "";

    private int _selectedSegmentIndex = -1;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;

    private DispatcherTimer? _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _isTimelineDrawn = false;
    private double _lastAppliedSpeed = 1.0;

    public bool Accepted { get; private set; }
    public IReadOnlyList<SpeedSegment> ResultSegments => _segments
        .Select(s => s with
        {
            StartMs = s.StartMs + (int)_trimStartMs,
            EndMs = s.EndMs + (int)_trimStartMs,
            ZoomStartMs = s.ZoomStartMs.HasValue ? s.ZoomStartMs.Value + _trimStartMs : (double?)null,
            ZoomEndMs = s.ZoomEndMs.HasValue ? s.ZoomEndMs.Value + _trimStartMs : (double?)null
        })
        .ToList()
        .AsReadOnly();

    private bool _isMobileFormat;
    private string _originalResolution = "1920x1080";
    /// <summary>
    /// CUT_02 — sections deleted from the middle of the clip, TRIM-RELATIVE ms while this window
    /// is open. Same frame of reference as <c>_segments</c>.
    /// </summary>
    private readonly List<FortniteVideoSoftware.Core.Media.CutRange> _cuts = new();

    // ══════════════════════════════════════════════════════════════════════════════════════
    // MEME_06 — MEMES SPLICED INTO THE MIDDLE OF THE VIDEO.
    //
    // The engine has been able to do this since MEME_05 and nothing could reach it: no screen
    // populated ExportPayload.MemePlacements, so every export fell through to the legacy
    // "one meme, start or end" branch. This editor is where it becomes reachable.
    //
    // WHY HERE AND NOT THE MAIN SCREEN — the exact mirror of the argument for cuts, and worth
    // stating because it is the reason the two features live on opposite screens:
    //
    //   A CUT occupies zero OUTPUT time. On this editor's ruler (output time) it is zero pixels
    //   wide, so it is marked on the Main App's SOURCE ruler where it has width.
    //
    //   A MEME occupies zero SOURCE time. On the Main App's ruler it is zero pixels wide — both
    //   clown heads would land on the same pixel — so it is placed HERE, on the output ruler,
    //   where it is a real block with a left edge, a right edge and a middle to grab.
    //
    // Each feature is edited where it actually has a shape. The Main App shows a single clown for
    // awareness only; it cannot be dragged there because there is nothing to drag along.
    //
    // ⚠️ AtSourceSecRelative is CLIP-RELATIVE SOURCE seconds and must already be snapped through
    // OutputTimeline.SnapInsertionPoint. Storing a raw click drops the meme somewhere the user
    // never saw. Every write to this list goes through PlaceMeme or MoveMemeTo, never directly.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private readonly List<FortniteVideoSoftware.Core.Media.MemePlacement> _memes = new();

    /// <summary>MEME_06 — the meme currently selected (marching ants), by Id. Null = none.</summary>
    private string? _selectedMemeId;

    /// <summary>MEME_06 — monotonic id counter. Never reset, never derived from _memes.Count.</summary>
    private int _nextMemeIdIndex;

    /// <summary>MEME_06 — the meme being dragged by its band, by Id. Null = not dragging.</summary>
    private string? _draggingMemeId;

    /// <summary>MEME_06 — where in the band the grab started, so the block does not jump under the pointer.</summary>
    private double _memeDragGrabOffsetOutSec;

    /// <summary>MEME_06 (DRAG_FIX) — last canvas X consumed by a meme drag; -1 when none is running.</summary>
    private double _memeDragLastX = -1;

    /// <summary>
    /// MEME_07 — plays the meme in the preview instead of holding the anchor frame. See
    /// <see cref="Infrastructure.MemePreviewDirector"/> for why it swaps the file in the one host
    /// rather than layering a second one, and for the rule its host tick has to follow.
    /// </summary>
    private Infrastructure.MemePreviewDirector? _memePreview;

    /// <summary>MEME_07 — true while the blocking rebuild stall is up; the timeline is frozen.</summary>
    private bool _memeRebuildStallActive;

    /// <summary>
    /// MEME_08 — while set AND the player is paused, the playback tick must not overwrite the
    /// caret. This is what lets a meme drag park the caret on the block's START while mpv sits on
    /// the anchor FRAME: the two are the same instant but different positions on the ruler, and
    /// without this the next tick would snap the caret to the block's far end. Any scrub, and any
    /// resumption of playback, releases it.
    /// </summary>
    private bool _memeCaretSticky;

    /// <summary>MEME_08 — was the preview playing when this meme drag began? Restored on release.</summary>
    private bool _memeDragWasPlaying;

    /// <summary>
    /// MEME_06 — two memes closer together than this (in OUTPUT seconds) would be merged onto one
    /// seam by ProcessWorker, because a zero-length piece between them makes the filter graph fail
    /// to configure. The editor blocks the placement instead, so the merge can never happen
    /// silently behind the user's back.
    /// </summary>
    private const double MemeMinSeparationOutSec = 0.05;

    /// <summary>
    /// MEME_09 — the smallest the meme's INVISIBLE grab area may be, in pixels. The coloured band
    /// is still painted at its true width; this only governs what the pointer can catch. Matches
    /// the freeze/zoom marker hitboxes, which solved the same problem for the same reason
    /// (HITBOX_01 / HITBOX_02).
    /// </summary>
    private const double MemeGrabMinWidthPx = 18;

    /// <summary>MEME_06 — the placements the Main App reads back, in clip-relative source seconds.</summary>
    public IReadOnlyList<FortniteVideoSoftware.Core.Media.MemePlacement> ResultMemes =>
        _memes.OrderBy(m => m.AtSourceSecRelative).ToList().AsReadOnly();

    /// <summary>CUT_02 — the cut list in ABSOLUTE source ms, for the Main App.</summary>
    public IReadOnlyList<FortniteVideoSoftware.Core.Media.CutRange> ResultCuts => _cuts
        .Select(c => new FortniteVideoSoftware.Core.Media.CutRange(c.StartMs + _trimStartMs, c.EndMs + _trimStartMs))
        .ToList()
        .AsReadOnly();

    public double ResultBaseSpeed => _baseSpeed;
    public double ResultFreezeTimeMs => _freezeTimeMs;
    public double ResultFreezeDurationS => _freezeDurationS;
    
    private readonly FortniteVideoSoftware.App.Controls.VoiceOverPreviewPlayer _voiceOverPlayer = new();

    private double _freezeTimeMs = -1;
    private double _freezeDurationS = 1.0;
    private double _selectedFreezePresetS = -1.0;
    /// <summary>
    /// FREEZE_ARM — true when the playhead is BEHIND the freeze point and the hold is therefore
    /// still owed. Set the moment the playhead is seen before the freeze, cleared the moment the
    /// hold fires.
    ///
    /// <para>
    /// ⚠️ THIS REPLACED A DISTANCE GUARD, AND THE DISTANCE GUARD IS WHY THE FREEZE PLAYED ONCE AND
    /// THEN SOMETIMES NOT AGAIN. The old test was
    /// <c>Math.Abs(currentAbsMs - _lastFreezeTriggerAbsMs) &gt; 500</c>, with
    /// <c>_lastFreezeTriggerAbsMs</c> set to the freeze point after firing. It was trying to say
    /// "do not immediately re-fire the hold we just finished" — but what it actually asks is "is
    /// the playhead more than half a second away from the freeze point", and on the SECOND pass the
    /// playhead crosses that point again at a distance of ~0. So the guard, which cannot tell a
    /// re-entry from an echo, silently suppressed the freeze. Whether it fired depended on how far
    /// the tick happened to land past the mark: near it, suppressed; a slow frame that overshot by
    /// more than 500ms, fired. Hence "sometimes".
    /// </para>
    /// <para>
    /// Arming is the right question. "Have we approached the mark from before it since the last
    /// time it fired?" has one answer, the same answer every pass, and it re-arms itself for free
    /// on a rewind, a seek backwards or a replay.
    /// </para>
    /// </summary>
    private bool _freezeArmed = true;
    private double _prevFreezeTickAbsMs = -1;

    private enum FreezeDragMode { None, Move, ResizeStart, ResizeEnd }
    private FreezeDragMode _freezeDragMode = FreezeDragMode.None;

    /// <summary>Which end of the hold a popsicle marker represents — and which one has focus.</summary>
    private enum FreezeMarkerEnd { None, Start, End }

    /// <summary>
    /// FOCUS_01 — which freeze marker is currently in focus, or None.
    ///
    /// <para>
    /// The hold has TWO popsicles now, one per edge, so "the freeze is selected" is no longer
    /// enough to know which set of marching ants to run. Focus is cleared by Esc, by a right-click
    /// anywhere, or by selecting any other object — see <see cref="ClearTimelineSelection"/>.
    /// </para>
    /// </summary>
    private FreezeMarkerEnd _freezeFocus = FreezeMarkerEnd.None;

    /// <summary>
    /// FOCUS_01 — every marching-ants rectangle belonging to the freeze markers, rebuilt on each
    /// redraw. A list rather than two named fields because there are now two markers with two
    /// rectangles each, and the animation timer does not care which is which.
    /// </summary>
    /// <summary>
    /// Every marching-ants rectangle currently mounted on the timeline overlay — freeze heads and
    /// (ZOOMPOP_01) zoom heads alike. The ants ticker walks this one list, so a marker that forgets
    /// to register its two rectangles here is drawn selected but never animates.
    /// </summary>
    private readonly List<Avalonia.Controls.Shapes.Rectangle> _freezeMarkerAnts = new();

    /// <summary>
    /// ZOOMPOP_01 — which zoom popsicle currently holds focus, or null for none. Focus on the
    /// timeline is exclusive across ALL object kinds: setting this clears the freeze focus and the
    /// selected-segment index, and <see cref="ClearTimelineSelection"/> clears this.
    /// </summary>
    private (int Segment, bool IsStart)? _zoomFocus;

    /// <summary>
    /// ZOOMPOP_01 — the zoom edge being dragged right now, or -1. The drag is driven from the
    /// CANVAS handlers, not the marker's own, because <c>RedrawTimeline</c> tears the marker control
    /// down and rebuilds it on every frame of the drag; a pointer captured to that control loses
    /// capture the instant it leaves the visual tree. This is the identical reason the freeze
    /// popsicle captures to the canvas. DO NOT move this back onto the marker.
    /// </summary>
    private int _zoomDragSegment = -1;
    private bool _zoomDragIsStart;
    private double _freezeDragGrabOffsetSec;
    private double _freezeDragFixedEndOutSec;

    /// <summary>A hold shorter than this is not a freeze, it is a stutter.</summary>
    private const double MinFreezeDurationS = 0.2;

    /// <summary>Ceiling on a dragged hold. The presets stop at 3s; drag is the advanced path, so it
    /// gets more room — but not unbounded, or one careless sweep adds a minute to the export.</summary>
    private const double MaxFreezeDurationS = 10.0;

    /// <summary>
    /// MARKER_01 — LaneABorder's BorderThickness. The marker overlay spans the whole grid cell
    /// while the lane's content sits INSIDE that 2px border, so an X measured against the segment
    /// canvas is 2px left of the same moment on the overlay. Two pixels is small enough to look
    /// like sloppiness rather than a bug, which is exactly why it is named rather than inlined.
    /// </summary>
    private const double LaneBorderInsetPx = 2.0;

    /// <summary>
    /// MARKER_01 — where the freeze popsicle hangs, measured from the TOP OF THE RULER.
    ///
    /// <para>
    /// The camera control is 52x103: head at y 28..56, stick at 56..103. At -52 the head bottom
    /// lands at 4 — just clear of the ruler — and the stick runs from 4 down to 51, straight
    /// through the ruler and into the upper lane where the frozen band is drawn. So the head floats
    /// ABOVE the timeline and the stick points at the exact instant, which is the whole shape of
    /// the Main App's thumbnail mark.
    /// </para>
    /// </summary>
    private const double FreezeMarkerOverlayTop = -52.0;

    /// <summary>
    /// FREEZE_GRAB / LEVEL_01 — how far BELOW the start marker the end marker hangs
    /// <b>WHEN, AND ONLY WHEN, THE TWO HEADS WOULD PHYSICALLY COVER EACH OTHER.</b>
    ///
    /// <para>
    /// Each camera is 52px wide and centred on its own instant (`ClampTimelineCameraLeft`), so a
    /// 1.5s hold on a 60s clip puts the two heads ~20px apart inside 52px of width and they cover
    /// each other by more than half. For that case the vertical stagger is what makes them two
    /// aimable objects instead of one lump. 30 is chosen so the 28px-tall heads clear each other
    /// completely with 2px to spare.
    /// </para>
    /// <para>
    /// ⚠️ IT IS APPLIED CONDITIONALLY. Once the heads are <see cref="FreezeMarkerHeadWidthPx"/>
    /// or more apart there is no occlusion left to solve, and dropping the end head 30px below the
    /// start head is then pure visual noise — it reads as a misaligned pair, which is exactly the
    /// bug this note exists to prevent. Compare the two <i>clamped</i> lefts, not the raw lane X:
    /// the clamp pins a head at the canvas edge, so raw separation lies at both ends of the ruler.
    /// </para>
    /// </summary>
    private const double FreezeMarkerEndStaggerPx = 30.0;

    /// <summary>
    /// LEVEL_01 — the on-screen width of one timeline camera/magnifier head, which is the outer
    /// canvas built by <c>MainWindow.CreateTimelineCameraIcon</c> / <c>CreateZoomTimelineCameraIcon</c>
    /// (52x103) and the same figure <c>ClampTimelineCameraLeft</c> centres on. Two heads whose
    /// clamped lefts differ by at least this much cannot overlap by a single pixel.
    /// </summary>
    private const double FreezeMarkerHeadWidthPx = 52.0;
    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;
    private bool _isFreezeCameraSelected = false;
    private int _draggingSegmentIndex = -1;
    private enum SegDragMode { None, Move, ResizeStart, ResizeEnd }
    private SegDragMode _segDragMode = SegDragMode.None;
    private double _dragOrigStartMs;
    private double _dragOrigEndMs;
    private double _dragStartPointerMs;
    private bool _isCanvasScrubbing;
    private const int SegMinWidthMs = 200;
    private const int SegGapMs = 1000;

    /// <summary>
    /// IDEA_3 — default length of the block auto-created to hold a zoom when the user presses
    /// ZOOM-IN with nothing selected. Long enough to be a usable punch-in, short enough that it is
    /// obviously a starting point to drag rather than a decision made for them.
    /// </summary>
    private const int DefaultZoomBlockMs = 2000;

    /// <summary>
    /// POPSICLE_01 — height assumed for the marker overlay when it has not been laid out yet
    /// (first render, before the first measure pass). Ruler 22 + two 60px lanes inside 2px borders
    /// = 22 + 64 + 64 = 150. It is a FALLBACK ONLY: once the control has real Bounds those are used,
    /// which is what keeps the sticks correct at any font scale or window size.
    /// </summary>
    private const double MarkerOverlayFallbackHeightPx = 150.0;

    private const double LaneHeight = 60.0;

    /// <summary>Coloured speed/freeze blocks fill the lane from the top down to here.</summary>
    private const double LaneBlockHeight = 60.0;

    /// <summary>
    /// RETIRED by ZOOMBAR_01 — kept only as the record of where the fuchsia zoom bar used to sit.
    /// The bar is no longer drawn: the START/END magnifier popsicles (ZOOMPOP_01) already mark the
    /// zoom span, and the bar restated it inside the block where it fought the speed colour. Do not
    /// reintroduce a horizontal bar in the lane for zoom.
    /// </summary>
    private const double LaneZoomBarY = 47.0;

    /// <summary>
    /// RETIRED by ZOOMPOP_01 — kept only as the record of where the magnifier handles used to sit.
    /// They were parented to the lane canvas at this Y, which pinned them INSIDE the 60px lane and
    /// is why they never read as the same object as the freeze cameras. They now mount on the
    /// marker overlay at <see cref="FreezeMarkerOverlayTop"/>. Do not reintroduce a lane-relative Y
    /// for them: the popsicle shape only exists because the head hangs ABOVE the ruler.
    /// </summary>
    private const double LaneZoomMarkerY = 34.0;


    /// <summary>
    /// LANES_01 — playhead position in ms relative to the trim start.
    ///
    /// This replaces the 44px `CompactSlider` that used to BE the playhead model (its 0-1000 Value
    /// was the source of truth). With the slider gone the position needs its own home, and having
    /// one plain field is considerably easier to reason about than a control's Value property that
    /// also fires change notifications while the user drags it.
    /// </summary>
    private double _playheadMs;

    /// <summary>
    /// FREEZE_CARET — the caret's OUTPUT position, when the source position cannot supply it.
    ///
    /// <para>
    /// Everywhere else the caret is derived: <c>SourceToOutput(_playheadMs)</c>. That works because
    /// the map is one-to-one — except across a held frame, where it is deliberately many-to-one.
    /// Every output moment of a 1.5s freeze maps to the SAME source instant, and
    /// <c>SourceToOutput</c> of that instant is defined to already include the WHOLE hold. So while
    /// the freeze plays, a derived caret sits pinned at the far edge of the hold: it leaps the
    /// frozen seconds in one step at the moment the freeze begins and then does not move for 1.5
    /// seconds. The ruler grew by the freeze — correctly, TIME_02 — but the caret refused to walk
    /// across the space it added.
    /// </para>
    ///
    /// <para>
    /// The source position simply does not carry the answer during a hold, so it is supplied
    /// directly instead. Non-null ONLY while the caret is inside a held span; every path that moves
    /// the playhead by ordinary means clears it. See <see cref="UpdateCaret"/>.
    /// </para>
    /// </summary>
    private double? _holdCaretOutSec;


    /// <summary>Pointer travel (px) before an armed press counts as a drag rather than a click.</summary>
    private const double CreateDragThresholdPx = 4.0;

    private bool _createDragArmed;
    private bool _createDragActive;
    private double _createDragStartMs;
    private double _createDragCurrentMs;

    /// <summary>
    /// IDEA_6 — the ONE zoom colour, resolved from the `AppZoomColor` design token.
    ///
    /// Zoom visuals are built in code-behind (the dashed box, its four handles, the timeline bar,
    /// the onboarding banner), so they cannot use DynamicResource from XAML. Routing them all
    /// through here keeps them on the SAME token as the XAML ones instead of the three hard-coded
    /// hexes they used before (#fde047 yellow, #1e40af blue, #d946ef fuchsia) — which is exactly
    /// why users could not tell a zoom from a speed block.
    ///
    /// Falls back to the literal token value if resource lookup fails, so a missing theme resource
    /// can never leave a zoom visual invisible.
    /// </summary>
    private static Avalonia.Media.Color ZoomColor()
    {
        if (Avalonia.Application.Current?.TryFindResource("AppZoomColor", out object? res) == true &&
            res is Avalonia.Media.Color c)
        {
            return c;
        }
        return Avalonia.Media.Color.Parse("#2251c1");
    }

    /// <summary>Fresh brush on the zoom token. Fresh instance per call — Avalonia shapes take ownership.</summary>
    private static Avalonia.Media.SolidColorBrush ZoomBrush(byte alpha = 255)
    {
        var c = ZoomColor();
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>
    /// FREEZE_VIS — the one blue every part of a freeze is drawn in.
    /// <para>
    /// Identical to what <c>GetSegmentOverlayColor</c> returns for a Speed≈0 segment. It is pulled
    /// out here because the frozen span is now drawn in THREE places — the block on the upper lane,
    /// the wash over the thumbnails, and the edge posts on both — and three hand-typed copies of
    /// the same literal is how a colour quietly drifts apart.
    /// </para>
    /// </summary>
    private static Avalonia.Media.SolidColorBrush FreezeBrush(byte alpha = 255)
        => new(Avalonia.Media.Color.FromArgb(alpha, 96, 165, 250));

    /// <summary>
    /// FREEZE_VIS — MAKES A HELD SPAN LOOK HELD.
    ///
    /// <para>
    /// The thumbnail lane already stretched the frozen frame across the whole hold (TIME_02/F4),
    /// which is literally what the exported file shows — and that is exactly why it was not
    /// readable. A stretched frame looks like ordinary footage that happens to be slow, or like a
    /// rendering glitch. Nothing said "time is stopped here".
    /// </para>
    ///
    /// <para>
    /// Four cues, each doing a different job, because one alone is ambiguous:
    /// <list type="number">
    ///   <item><description>A cool blue WASH — the same blue as the freeze block above it, so the
    ///   two read as one object spanning both lanes rather than a marker and some odd footage.</description></item>
    ///   <item><description>Diagonal HATCHING — the universal "this region is not normal content"
    ///   cue. It also survives where colour alone does not: over a blue-ish frame, over a blown-out
    ///   white one, and for a colour-blind user.</description></item>
    ///   <item><description>Solid POSTS at both ends — the wash says "something here", the posts
    ///   say exactly WHERE it starts and stops, which is the thing being asked of the timeline.</description></item>
    ///   <item><description>A centred ❄ LABEL with the duration, when there is room for it. Removes
    ///   the last of the guesswork; suppressed on narrow spans rather than clipped to mush.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Everything added here is <c>IsHitTestVisible = false</c>. The upper lane runs its own pointer
    /// pipeline for block move, edge resize and drag-to-create; a decoration that swallowed a press
    /// would break editing over every freeze.
    /// </para>
    /// </summary>
    private void DecorateFrozenSpan(Avalonia.Controls.Canvas host, double x, double spanW, double h,
                                    bool withLabel, bool withGrips = false)
    {
        if (spanW <= 1 || h <= 0) return;

        var wash = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = spanW,
            Height = h,
            Fill = FreezeBrush(0x3A),
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(wash, x);
        Avalonia.Controls.Canvas.SetTop(wash, 0);
        host.Children.Add(wash);

        var hatchHost = new Avalonia.Controls.Canvas
        {
            Width = spanW,
            Height = h,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(hatchHost, x);
        Avalonia.Controls.Canvas.SetTop(hatchHost, 0);

        const double HatchStep = 11.0;
        var hatchBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x26, 255, 255, 255));
        for (double hx = -h; hx < spanW; hx += HatchStep)
        {
            hatchHost.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Avalonia.Point(hx, h),
                EndPoint = new Avalonia.Point(hx + h, 0),
                Stroke = hatchBrush,
                StrokeThickness = 2,
                IsHitTestVisible = false
            });
        }
        host.Children.Add(hatchHost);

        double postW = withGrips ? 4 : 2;
        foreach (double postX in new[] { x, x + spanW - postW })
        {
            var post = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = postW,
                Height = h,
                Fill = FreezeBrush(0xE6),
                IsHitTestVisible = false
            };
            Avalonia.Controls.Canvas.SetLeft(post, postX);
            Avalonia.Controls.Canvas.SetTop(post, 0);
            host.Children.Add(post);

            if (!withGrips || h < 20) continue;

            for (int n = -1; n <= 1; n++)
            {
                var notch = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 8,
                    Height = 2,
                    Fill = FreezeBrush(0xFF),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(notch, postX + postW / 2.0 - 4);
                Avalonia.Controls.Canvas.SetTop(notch, h / 2.0 - 1 + n * 5);
                host.Children.Add(notch);
            }
        }

        if (!withLabel || h < 18) return;

        string label = $"❄ FROZEN {_freezeDurationS:0.0}s";

        double pillW = label.Length * 6.2 + 12;
        const double PillH = 15;

        if (pillW > spanW - 6) return;

        var pill = new Border
        {
            Width = pillW,
            Height = PillH,
            Background = FreezeBrush(0xF0),
            CornerRadius = new Avalonia.CornerRadius(3),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(12, 20, 34))
            }
        };
        Avalonia.Controls.Canvas.SetLeft(pill, x + (spanW - pillW) / 2.0);
        Avalonia.Controls.Canvas.SetTop(pill, Math.Max(0, (h - PillH) / 2.0));
        host.Children.Add(pill);
    }
    private Avalonia.Controls.Shapes.Rectangle? _selectedSegmentBorderRef;
    private DispatcherTimer? _freezePulseTimer;

    /// <summary>
    /// Parameterless ctor required by Avalonia's XAML runtime loader.
    /// Do not call directly — use the overload that accepts a video path.
    /// </summary>
    public GranularSpeedEditorWindow() : this(string.Empty, 0, 0) { }

    /// <summary>
    /// Creates the Granular Speed Editor constrained to a trim region.
    /// The editor will only show/seek between trimStartMs and trimEndMs.
    /// Segments are stored in absolute video timestamps.
    /// </summary>
    public GranularSpeedEditorWindow(string videoPath, double trimStartMs = 0, double trimEndMs = 0, IEnumerable<SpeedSegment>? existingSegments = null, double baseSpeed = 1.1, double freezeTimeMs = -1, double freezeDurationS = 1.0, bool isMobileFormat = false, string originalResolution = "1920x1080", VoiceOverWindow.VoiceOverResult? voiceOverResult = null, IEnumerable<FortniteVideoSoftware.Core.Media.CutRange>? existingCuts = null,
        IEnumerable<FortniteVideoSoftware.Core.Media.MemePlacement>? existingMemes = null)
    {
        _voiceOverPlayer.Result = voiceOverResult;

        // MEME_06 — memes arrive and leave in CLIP-RELATIVE SOURCE seconds, which is already this
        // window's own frame of reference for insertions, so unlike cuts there is no trim offset to
        // add or subtract. Do not "make it consistent" with the cut handling below by shifting these.
        if (existingMemes != null) _memes.AddRange(existingMemes);

        // CUT_02 — cuts arrive in ABSOLUTE source ms and are held TRIM-RELATIVE inside this window,
        // exactly like _segments. ResultCuts adds _trimStartMs back on the way out.
        if (existingCuts != null)
        {
            foreach (var c in existingCuts)
                _cuts.Add(new FortniteVideoSoftware.Core.Media.CutRange(c.StartMs - trimStartMs, c.EndMs - trimStartMs));
        }
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        _baseSpeed = baseSpeed;
        _freezeTimeMs = freezeTimeMs;
        _freezeDurationS = freezeDurationS;
        _isMobileFormat = isMobileFormat;
        if (!string.IsNullOrWhiteSpace(originalResolution)) _originalResolution = originalResolution;
        _selectedFreezePresetS = -1.0;

        try { _gpuLiveZoomPreview = FortniteVideoSoftware.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration; }
        catch { _gpuLiveZoomPreview = false; }
        RuntimeLog.Info("Granular", $"Live zoom preview path: {(_gpuLiveZoomPreview ? "GPU (mpv video-crop simulation)" : "CPU (yellow box overlay only)")}");

        InitializeComponent();

        var zoomContainer = this.FindControl<Avalonia.Controls.Grid>("ZoomContainerGrid");
        if (zoomContainer != null)
        {
            zoomContainer.Height = _isMobileFormat ? 1280 : 1080;
        }

        this.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (s, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) ClearTimelineSelection();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        this.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (s, e) =>
        {
            if (_isCanvasScrubbing)
            {
                _isCanvasScrubbing = false;
                e.Pointer.Capture(null);
            }

            // MEME_06 — checked before the zoom and freeze branches. A meme drag captures the
            // canvas, so nothing else can be in flight at the same time, and returning here keeps
            // the two gestures from ever interleaving.
            if (EndMemeDrag(e)) return;

            if (_zoomDragSegment >= 0)
            {
                int zi = _zoomDragSegment;
                bool zStart = _zoomDragIsStart;
                _zoomDragSegment = -1;
                _isDraggingZoomMarker = false;
                EndUndoGesture();   // UNDO_02
                e.Pointer.Capture(null);
                RedrawTimeline();

                HideDragReadout();

                if (zi < _segments.Count)
                {
                    var zseg = _segments[zi];
                    double edgeMs = zStart ? (zseg.ZoomStartMs ?? zseg.StartMs) : (zseg.ZoomEndMs ?? zseg.EndMs);
                    RefreshSegmentList();
                    RuntimeLog.Info("Granular",
                        $"Zoom {(zStart ? "START" : "END")} settled on segment #{zi + 1}: {FormatMs(edgeMs)} (block now {FormatMs(zseg.StartMs)}–{FormatMs(zseg.EndMs)}).");
                    SetStatus($"Zoom {(zStart ? "start" : "end")} at {FormatMs(edgeMs)} — block moved with it.");
                    _ = SeekInternal(edgeMs / 1000.0);
                }
                return;
            }

            if (_freezeDragMode != FreezeDragMode.None)
            {
                var finished = _freezeDragMode;
                _freezeDragMode = FreezeDragMode.None;
                EndUndoGesture();   // UNDO_02
                e.Pointer.Capture(null);
                HideDragReadout();
                ClampFreezeIntoClip();
                RedrawTimeline();

                SeekGranularPreviewToFreezeMarker();
                RuntimeLog.Info("Granular",
                    $"Freeze settled: {FormatMs(_freezeTimeMs - _trimStartMs)} for {_freezeDurationS:0.00}s ({finished}).");
                SetStatus($"Freeze at {FormatMs(_freezeTimeMs - _trimStartMs)}, held for {_freezeDurationS:0.00}s.");
                return;
            }

            if (_createDragArmed)
            {
                bool wasDrag = _createDragActive;
                double a = Math.Min(_createDragStartMs, _createDragCurrentMs);
                double b = Math.Max(_createDragStartMs, _createDragCurrentMs);

                _createDragArmed = false;
                _createDragActive = false;
                e.Pointer.Capture(null);
                HideDragReadout();

                if (wasDrag) CreateSegmentFromDrag(a, b);
                else SetPlayheadFromScrub(_createDragStartMs);

                RedrawTimeline();
                return;
            }

            if (_draggingSegmentIndex != -1)
            {
                var finishedMode = _segDragMode;
                int finishedIdx = _draggingSegmentIndex;
                if (_segDragMode != SegDragMode.None && _draggingSegmentIndex < _segments.Count)
                {
                    ClampZoomInsideItsBlock(_draggingSegmentIndex);   // ZOOMLIVE_05
                    var seg = _segments[_draggingSegmentIndex];
                    RuntimeLog.Info("Granular", $"Segment #{_draggingSegmentIndex + 1} settled: rel {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)} @ {seg.Speed:0.0}x (abs {FormatMs(seg.StartMs + _trimStartMs)}–{FormatMs(seg.EndMs + _trimStartMs)}).");
                    SetStatus($"Segment #{_draggingSegmentIndex + 1} set to {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)} @ {seg.Speed:0.0}x.");
                }
                _segDragMode = SegDragMode.None;
                _draggingSegmentIndex = -1;
                EndUndoGesture();   // UNDO_02
                e.Pointer.Capture(null);
                HideDragReadout();
                RefreshSegmentList();
                RedrawTimeline();

                if (finishedMode != SegDragMode.None && finishedIdx >= 0 && finishedIdx < _segments.Count
                    && _videoHost?.IpcClient?.IsPaused == true)
                {
                    var fseg = _segments[finishedIdx];
                    bool didChange = Math.Abs(_dragOrigStartMs - fseg.StartMs) > 1.0 || Math.Abs(_dragOrigEndMs - fseg.EndMs) > 1.0;
                    if (didChange)
                    {
                        double seekRelSec = (finishedMode == SegDragMode.ResizeEnd ? fseg.EndMs : fseg.StartMs) / 1000.0;
                        _ = SeekInternal(seekRelSec);
                    }
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);

        FortniteVideoSoftware.App.WindowBoundsHelper.Track(this, "GranularBounds");
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;
        
        _pendingSpeed = baseSpeed;
        _lastAppliedSpeed = baseSpeed;

        var initialSpeedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider"); if(initialSpeedSlider!=null)initialSpeedSlider.SetRange(1, 40);
        SpeedPresetButtons.SetSpinningWheelValue(initialSpeedSlider, _pendingSpeed);
        var initialSpeedLabel = this.FindControl<TextBlock>("PendingSpeedLabel");
        if (initialSpeedLabel != null) initialSpeedLabel.Text = $"{_pendingSpeed:0.0}x";
        
        if (existingSegments != null)
        {
            foreach (var seg in existingSegments)
            {
                int relStart = (int)(seg.StartMs - _trimStartMs);
                int relEnd = (int)(seg.EndMs - _trimStartMs);
                if (relEnd > 0 && relStart < (int)(_trimEndMs - _trimStartMs))
                {
                    relStart = Math.Max(0, relStart);
                    int maxEnd = _trimEndMs > 0 ? (int)(_trimEndMs - _trimStartMs) : int.MaxValue;
                    relEnd = Math.Min(relEnd, maxEnd);
                    PushUndo("add segment", "seg-drag-create");   // UNDO_02
                    _segments.Add(new SpeedSegment(relStart, relEnd, seg.Speed,
                        seg.ZoomX, seg.ZoomY, seg.ZoomW, seg.ZoomH, seg.ZoomOrigRes, seg.ZoomSlow,
                        seg.ZoomStartMs.HasValue ? seg.ZoomStartMs.Value - _trimStartMs : (double?)null,
                        seg.ZoomEndMs.HasValue ? seg.ZoomEndMs.Value - _trimStartMs : (double?)null));
                }
            }
        }

        BuildLaneContent();

        _openingSignature = BuildStateSignature();

        this.Loaded += (s, e) => Controls.CoachOverlay.Register(this, Controls.CoachTours.GranularKey, Controls.CoachTours.Granular);

        this.Loaded += (s, e) =>
        {
            InitializeMpv();
            _ = BuildFrameLaneAsync();
        };
        WireUpControls();
        WireZoomControls();
        AttachTitleBarDrag();
        RefreshSegmentList();
        UpdateDeleteButtonVisibility();

        if (_freezeTimeMs >= 0)
        {
            var toggle = this.FindControl<Button>("FreezeImageToggle");
            if (toggle != null)
            {
                toggle.Classes.Remove("Primary");
                toggle.Classes.Add("Danger");
                var icon = this.FindControl<TextBlock>("FreezeImageToggleIcon");
                var txt = this.FindControl<TextBlock>("FreezeImageToggleText");
                if (icon != null) icon.Text = "🔓";
                if (txt != null) txt.Text = " UNFREEZE IMAGE ";
            }
        }

        _marchingAntsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _marchingAntsTimer.Tick += (_, _) => {
            _marchingAntsOffset = (_marchingAntsOffset + 1) % 8;
            foreach (var ant in _freezeMarkerAnts) ant.StrokeDashOffset = _marchingAntsOffset;
            if (_selectedSegmentBorderRef != null)
            {
                _selectedSegmentBorderRef.StrokeDashOffset = _marchingAntsOffset;
            }
        };
        _marchingAntsTimer.Start();

        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        var fvPopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("FreezeValidationPopup");
        var targetBorder = this.FindControl<Avalonia.Controls.Border>("GranularVideoAreaBorder");
        if (fvPopup != null && targetBorder != null) fvPopup.PlacementTarget = targetBorder;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void InitializeMpv()
    {
        WirePreviewDetach();

        _videoHost = this.FindControl<MpvVideoView>("GranularVideoHost");
        if (_videoHost != null)
        {
            RuntimeLog.Info("Granular", "Initializing MPV video host for Granular Speed Editor.");
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath))
            {
                RuntimeLog.Fail("Granular", "Could not locate mpv.exe for Granular Speed Editor. Using PATH fallback.");
                mpvPath = "mpv.exe";
            }
            else
            {
                RuntimeLog.Info("Granular", $"Using MPV: {System.IO.Path.GetFileName(mpvPath)}");
                RuntimeLog.Debug("Granular", $"Using MPV path: {mpvPath}");
            }
            await _videoHost.StartMpvProcessAsync(mpvPath);

            if (_videoHost.IpcClient != null)
            {
                RuntimeLog.Info("Granular", "MPV IPC client connected. Attaching seek handler.");
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

                await LoadVideoAsync();
                BuildMemePreviewDirector();   // MEME_07
            }
            else
            {
                RuntimeLog.Fail("Granular", "MPV IPC client is null after starting MPV process.");
            }
        }
        else
        {
            RuntimeLog.Fail("Granular", "Could not find GranularVideoHost control in XAML.");
        }
    }

    private void UpdateTooltips()
    {
        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playBtn = this.FindControl<Button>("GranularPlayPause");
        if (playBtn != null) ToolTip.SetTip(playBtn, $"Play or pause the video ({kb.PlayPause})");
        
        var startBtn = this.FindControl<Button>("MarkStartBtn");
        if (startBtn != null) ToolTip.SetTip(startBtn, $"Mark the start of the segment ({kb.MarkStart})");
        
        var endBtn = this.FindControl<Button>("MarkEndBtn");
        if (endBtn != null) ToolTip.SetTip(endBtn, $"Mark the end of the segment ({kb.MarkEnd})");
    }

    private void GranularKeyUpHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);

        if (playPause.Matches(e) || markStart.Matches(e) || markEnd.Matches(e) || e.Key is Avalonia.Input.Key.Space or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            e.Handled = true;
        }
    }

    private void GranularKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        var kb = FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.KeyBinds;

        // ZOOMLIVE_01 — ESCAPE NO LONGER DESTROYS THE ZOOM.
        // It used to mean "cancel this transaction", stripping the zoom off the block. There is no
        // transaction now: the box is written to the segment as it is dragged, so Escape can only
        // sensibly mean "put the box away". An untouched auto-created block is still cleaned up by
        // ExitZoomMode. To actually delete a zoom, use REMOVE ZOOM (or Ctrl+Z).
        if (e.Key == Avalonia.Input.Key.Escape && _zoomModeActive)
        {
            ExitZoomMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Avalonia.Input.Key.Escape
            && (_isFreezeCameraSelected || _selectedSegmentIndex >= 0))
        {
            ClearTimelineSelection();
            e.Handled = true;
            return;
        }

        if (_isFreezeCameraSelected && _freezeTimeMs >= 0 && e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            int frames = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 10 : 1;
            int dir = e.Key == Avalonia.Input.Key.Left ? -frames : frames;
            if (_freezeFocus == FreezeMarkerEnd.End) NudgeFreezeDurationByFrames(dir);
            else MoveFreezeCameraByFrames(dir);
            e.Handled = true;
            return;
        }

        if (e.Key == Avalonia.Input.Key.Delete || e.Key == Avalonia.Input.Key.Back)
        {
            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                RequestDeleteSegment(_selectedSegmentIndex);
                e.Handled = true;
                return;
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

        if (playPause.Matches(e))
        {
            var btn = this.FindControl<Button>("GranularPlayPause");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (markStart.Matches(e))
        {
            var btn = this.FindControl<Button>("MarkStartBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (markEnd.Matches(e))
        {
            var btn = this.FindControl<Button>("MarkEndBtn");
            btn?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
            double target = Math.Min(currentAbs + 5, trimEndSec);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double target = Math.Max(currentAbs - 5, _trimStartMs / 1000.0);
            _ = SeekInternal(target - (_trimStartMs / 1000.0));
            e.Handled = true;
        }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) { _nextSeekTarget = time; return; }
        _isSeeking = true;
        double absTime = (_trimStartMs / 1000.0) + time;
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
        absTime = Math.Min(absTime, trimEndSec);
        absTime = Math.Max(absTime, _trimStartMs / 1000.0);
        if (_videoHost?.IpcClient != null) await _videoHost.IpcClient.SendCommandAsync("seek", absTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    private async Task LoadVideoAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || _videoHost?.IpcClient == null) return;

        double startSec = _trimStartMs / 1000.0;

        await _videoHost.IpcClient.LoadFileAsync(_videoPath, startSec);
        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        RuntimeLog.Info("Granular", $"Loaded preview video at {startSec:0.###}s.");
    }

    private void WireUpControls()
    {

        var canvas = _segmentCanvas;
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => RedrawTimeline();

            canvas.IsHitTestVisible = true;

            canvas.PointerPressed += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double totalMs = dur * 1000.0;
                double msPerPx = totalMs / w;
                double pointerMs = Math.Clamp(XToSrcMs(e.GetPosition(canvas).X, w), 0, totalMs);
                double edgeMs = 8.0 * msPerPx;

                if (_freezeTimeMs >= 0 && _freezeDurationS > 0)
                {
                    double outSecAtPress = OutXToOutSec(e.GetPosition(canvas).X, w);
                    double holdStart = FreezeHoldStartOutSec();
                    double holdEnd = holdStart + _freezeDurationS;
                    double gripSec = Math.Min(8.0 * (OutDurationSec() / w), _freezeDurationS / 3.0);

                    if (outSecAtPress >= holdStart - gripSec && outSecAtPress <= holdEnd + gripSec)
                    {
                        var fm = FreezeDragMode.Move;
                        if (Math.Abs(outSecAtPress - holdStart) <= gripSec) fm = FreezeDragMode.ResizeStart;
                        else if (Math.Abs(outSecAtPress - holdEnd) <= gripSec) fm = FreezeDragMode.ResizeEnd;

                        _freezeDragMode = fm;
                        _freezeDragFixedEndOutSec = holdEnd;
                        _freezeDragGrabOffsetSec = Math.Max(0,
                            OutXToBaseOutSec(e.GetPosition(canvas).X, w) - holdStart);
                        _isFreezeCameraSelected = true;
                        _selectedSegmentIndex = -1;
                        UpdateDeleteButtonVisibility();
                        e.Pointer.Capture(canvas);
                        SetStatus(fm switch
                        {
                            FreezeDragMode.ResizeStart => "Dragging the freeze START — release to set.",
                            FreezeDragMode.ResizeEnd => "Dragging the freeze END — release to set.",
                            _ => "Moving the freeze — release to set."
                        });
                        RedrawTimeline();
                        e.Handled = true;
                        return;
                    }
                }

                int hitIdx = -1;
                SegDragMode mode = SegDragMode.None;
                double bestEdgeDist = double.MaxValue;
                for (int i = 0; i < _segments.Count; i++)
                {
                    var sg = _segments[i];
                    double effEdgeMs = Math.Min(edgeMs, Math.Max(0.0, sg.EndMs - sg.StartMs) / 3.0);
                    double dStart = Math.Abs(pointerMs - sg.StartMs);
                    double dEnd = Math.Abs(pointerMs - sg.EndMs);
                    if (dStart <= effEdgeMs && dStart < bestEdgeDist) { bestEdgeDist = dStart; hitIdx = i; mode = SegDragMode.ResizeStart; }
                    if (dEnd <= effEdgeMs && dEnd < bestEdgeDist) { bestEdgeDist = dEnd; hitIdx = i; mode = SegDragMode.ResizeEnd; }
                }
                if (hitIdx < 0)
                {
                    for (int i = 0; i < _segments.Count; i++)
                    {
                        var sg = _segments[i];
                        if (pointerMs > sg.StartMs && pointerMs < sg.EndMs) { hitIdx = i; mode = SegDragMode.Move; break; }
                    }
                }

                if (hitIdx >= 0 && mode != SegDragMode.None)
                {
                    // ZOOMLIVE_02 — one selection path for the whole window: this also parks the
                    // playhead on the block's first frame and re-opens its zoom box if it has one.
                    SelectSegment(hitIdx, jumpPlayhead: true);
                    var seg = _segments[hitIdx];

                    if (e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed)
                    {
                        ShowSegmentContextMenu(canvas, hitIdx);
                        e.Handled = true;
                        return;
                    }

                    _draggingSegmentIndex = hitIdx;
                    _segDragMode = mode;
                    _dragOrigStartMs = seg.StartMs;
                    _dragOrigEndMs = seg.EndMs;
                    _dragStartPointerMs = pointerMs;
                    e.Pointer.Capture(canvas);
                    SetStatus(mode == SegDragMode.Move
                        ? $"Moving segment #{hitIdx + 1} — release to set."
                        : $"Resizing segment #{hitIdx + 1} — release to set.");
                    UpdateDragReadout(seg.StartMs, seg.EndMs);
                    RedrawTimeline();
                    e.Handled = true;
                    return;
                }

                if (_isFreezeCameraSelected)
                {
                    _isFreezeCameraSelected = false;
                    _freezeFocus = FreezeMarkerEnd.None;
                }
                if (_selectedSegmentIndex >= 0)
                {
                    _selectedSegmentIndex = -1;
                    UpdateDeleteButtonVisibility();
                }

                _createDragArmed = true;
                _createDragStartMs = pointerMs;
                _createDragCurrentMs = pointerMs;
                _createDragActive = false;
                _isCanvasScrubbing = false;
                e.Pointer.Capture(canvas);
                RedrawTimeline();
                e.Handled = true;
            };

            canvas.PointerMoved += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double totalMs = dur * 1000.0;
                double msPerPx = totalMs / w;
                double pointerMs = Math.Clamp(XToSrcMs(e.GetPosition(canvas).X, w), 0, totalMs);

                // MEME_06 — before scrubbing and before every other drag mode: a meme drag owns the
                // pointer for its whole gesture.
                if (PumpMemeDrag(e, canvas)) return;

                if (_isCanvasScrubbing)
                {
                    SetPlayheadFromScrub(pointerMs);
                    e.Handled = true;
                    return;
                }

                if (_zoomDragSegment >= 0 && _zoomDragSegment < _segments.Count)
                {
                    double zx = Math.Clamp(e.GetPosition(canvas).X, 0, w);
                    double newMs = ClampZoomEdgeAgainstSlowNeighbours(
                        _zoomDragSegment, XToSrcMs(zx, w), _zoomDragIsStart);

                    var zseg = _segments[_zoomDragSegment];

                    double zLower = 0;
                    double zUpper = totalMs;
                    for (int j = 0; j < _segments.Count; j++)
                    {
                        if (j == _zoomDragSegment) continue;
                        if (_segments[j].EndMs <= zseg.StartMs)
                            zLower = Math.Max(zLower, _segments[j].EndMs + SegGapMs);
                        if (_segments[j].StartMs >= zseg.EndMs)
                            zUpper = Math.Min(zUpper, _segments[j].StartMs - SegGapMs);
                    }

                    if (_zoomDragIsStart)
                    {
                        double zNewStart = Math.Clamp(newMs, zLower,
                            Math.Max(zLower, zseg.EndMs - SegMinWidthMs));
                        PushUndo("move zoom box", "zoom-edge");   // UNDO_02
                        _segments[_zoomDragSegment] = zseg with
                        {
                            StartMs = zNewStart,
                            ZoomStartMs = zNewStart
                        };
                    }
                    else
                    {
                        double zNewEnd = Math.Clamp(newMs,
                            Math.Min(zUpper, zseg.StartMs + SegMinWidthMs), zUpper);
                        _segments[_zoomDragSegment] = zseg with
                        {
                            EndMs = zNewEnd,
                            ZoomEndMs = zNewEnd
                        };
                    }

                    var zNow = _segments[_zoomDragSegment];
                    UpdateDragReadout(zNow.StartMs, zNow.EndMs);
                    RedrawTimeline();
                    
                    double zFollowRelSec = (_zoomDragIsStart ? zNow.StartMs : zNow.EndMs) / 1000.0;
                    _ = SeekInternal(zFollowRelSec);
                    
                    e.Handled = true;
                    return;
                }

                if (_freezeDragMode != FreezeDragMode.None)
                {
                    // UNDO_02 — hoisted ABOVE the switch on purpose: all three drag modes change
                    // the freeze, and one snapshot per gesture must cover whichever one is running.
                    PushUndo(_freezeDragMode == FreezeDragMode.Move ? "move freeze" : "change freeze length",
                             "freeze-drag");

                    double px = e.GetPosition(canvas).X;
                    double holdStartNow = FreezeHoldStartOutSec();

                    switch (_freezeDragMode)
                    {
                        case FreezeDragMode.ResizeEnd:
                            _freezeDurationS = Math.Clamp(
                                OutXToOutSec(px, w) - holdStartNow, MinFreezeDurationS, MaxFreezeDurationS);
                            break;

                        case FreezeDragMode.ResizeStart:
                        {
                            double newHoldStartSec = Math.Clamp(OutXToBaseOutSec(px, w),
                                0, Math.Max(0, _freezeDragFixedEndOutSec - MinFreezeDurationS));
                            SetFreezeStartFromBaseOutSec(newHoldStartSec);
                            _freezeDurationS = Math.Clamp(
                                _freezeDragFixedEndOutSec - newHoldStartSec, MinFreezeDurationS, MaxFreezeDurationS);
                            break;
                        }

                        default:
                            SetFreezeStartFromBaseOutSec(OutXToBaseOutSec(px, w) - _freezeDragGrabOffsetSec);
                            break;
                    }

                    _freezeDurationS = Math.Round(_freezeDurationS, 2);
                    ClampFreezeIntoClip();
                    UpdateDragReadout(_freezeTimeMs - _trimStartMs,
                                      _freezeTimeMs - _trimStartMs + _freezeDurationS * 1000.0);
                    RedrawTimeline();
                    
                    _ = SeekInternal(_freezeTimeMs / 1000.0);
                    
                    e.Handled = true;
                    return;
                }

                if (_createDragArmed)
                {
                    if (!_createDragActive &&
                        Math.Abs(pointerMs - _createDragStartMs) >= CreateDragThresholdPx * msPerPx)
                    {
                        _createDragActive = true;
                    }

                    if (_createDragActive)
                    {
                        _createDragCurrentMs = pointerMs;
                        UpdateDragReadout(Math.Min(_createDragStartMs, _createDragCurrentMs),
                                          Math.Max(_createDragStartMs, _createDragCurrentMs));
                        RedrawTimeline();
                    }
                    e.Handled = true;
                    return;
                }

                if (_draggingSegmentIndex < 0 || _draggingSegmentIndex >= _segments.Count || _segDragMode == SegDragMode.None)
                    return;

                int idx = _draggingSegmentIndex;

                double lowerBound = 0;
                double upperBound = totalMs;
                int lowerBlockIdx = -1;
                int upperBlockIdx = -1;
                for (int j = 0; j < _segments.Count; j++)
                {
                    if (j == idx) continue;
                    if (_segments[j].EndMs <= _dragOrigStartMs)
                    {
                        double l = _segments[j].EndMs + SegGapMs;
                        if (l > lowerBound) { lowerBound = l; lowerBlockIdx = j; }
                    }
                    if (_segments[j].StartMs >= _dragOrigEndMs)
                    {
                        double u = _segments[j].StartMs - SegGapMs;
                        if (u < upperBound) { upperBound = u; upperBlockIdx = j; }
                    }
                }

                double newStart = _dragOrigStartMs;
                double newEnd = _dragOrigEndMs;

                upperBound = Math.Max(upperBound, lowerBound + SegMinWidthMs);

                bool hitLeftWall = false;
                bool hitRightWall = false;

                if (_segDragMode == SegDragMode.Move)
                {
                    double width = Math.Max(SegMinWidthMs, _dragOrigEndMs - _dragOrigStartMs);
                    double delta = pointerMs - _dragStartPointerMs;
                    double desiredStart = _dragOrigStartMs + delta;
                    newStart = Math.Clamp(desiredStart, lowerBound, Math.Max(lowerBound, upperBound - width));
                    newEnd = newStart + width;
                    
                    if (desiredStart <= lowerBound && lowerBound > 0 && lowerBlockIdx >= 0) hitLeftWall = true;
                    if (desiredStart >= upperBound - width && upperBound < totalMs && upperBlockIdx >= 0) hitRightWall = true;
                }
                else if (_segDragMode == SegDragMode.ResizeStart)
                {
                    double desiredStart = pointerMs;
                    newStart = Math.Clamp(desiredStart, lowerBound, Math.Max(lowerBound, _dragOrigEndMs - SegMinWidthMs));
                    newEnd = _dragOrigEndMs;
                    
                    if (desiredStart <= lowerBound && lowerBound > 0 && lowerBlockIdx >= 0) hitLeftWall = true;
                }
                else if (_segDragMode == SegDragMode.ResizeEnd)
                {
                    double desiredEnd = pointerMs;
                    newEnd = Math.Clamp(desiredEnd, Math.Min(upperBound, _dragOrigStartMs + SegMinWidthMs), upperBound);
                    newStart = _dragOrigStartMs;
                    
                    if (desiredEnd >= upperBound && upperBound < totalMs && upperBlockIdx >= 0) hitRightWall = true;
                }

                double snapTol = 8.0 * msPerPx;
                double playheadMs = _playheadMs;
                double NearestSnap(double ms)
                {
                    double best = ms, bestD = snapTol;
                    void Try(double t) { if (t < 0) return; double d = Math.Abs(ms - t); if (d < bestD) { bestD = d; best = t; } }
                    Try(0); Try(totalMs); Try(playheadMs);
                    for (int j = 0; j < _segments.Count; j++) { if (j == idx) continue; Try(_segments[j].StartMs); Try(_segments[j].EndMs); }
                    return best;
                }
                double segWidth = newEnd - newStart;
                if (_segDragMode == SegDragMode.Move)
                {
                    double sS = NearestSnap(newStart), sE = NearestSnap(newEnd);
                    if (Math.Abs(sS - newStart) <= Math.Abs(sE - newEnd) && sS != newStart)
                        { newStart = Math.Clamp(sS, lowerBound, Math.Max(lowerBound, upperBound - segWidth)); newEnd = newStart + segWidth; }
                    else if (sE != newEnd)
                        { newEnd = Math.Clamp(sE, Math.Min(upperBound, lowerBound + segWidth), upperBound); newStart = newEnd - segWidth; }
                }
                else if (_segDragMode == SegDragMode.ResizeStart)
                    newStart = Math.Clamp(NearestSnap(newStart), lowerBound, Math.Max(lowerBound, newEnd - SegMinWidthMs));
                else if (_segDragMode == SegDragMode.ResizeEnd)
                    newEnd = Math.Clamp(NearestSnap(newEnd), Math.Min(upperBound, newStart + SegMinWidthMs), upperBound);

                if (hitLeftWall)
                    SetStatus($"Blocked on the left! Must keep a {SegGapMs/1000.0}s gap from the previous segment.");
                else if (hitRightWall)
                    SetStatus($"Blocked on the right! Must keep a {SegGapMs/1000.0}s gap from the next segment.");
                else
                    SetStatus(_segDragMode == SegDragMode.Move ? $"Moving segment #{idx + 1} — release to set." : $"Resizing segment #{idx + 1} — release to set.");

                PushUndo("resize segment", "seg-edge");   // UNDO_02
                _segments[idx] = _segments[idx] with { StartMs = newStart, EndMs = newEnd };
                UpdateDragReadout(newStart, newEnd);
                UpdateDraggingVisuals(idx, newStart, newEnd);
                double followRelSec = (_segDragMode == SegDragMode.ResizeEnd ? newEnd : newStart) / 1000.0;
                _ = SeekInternal(followRelSec);
                e.Handled = true;
            };
        }


        var playPause = this.FindControl<Button>("GranularPlayPause");
        playPause?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User toggled Play/Pause in Granular Speed Editor.");
            if (_isCurrentlyFrozen)
            {
                _isCurrentlyFrozen = false;
                return;
            }
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
        });


        WireDeletePartsButton();
        WireMemeButtons();          // MEME_06
        WireUndoRedo();            // UNDO_01

        var markStart = this.FindControl<Button>("MarkStartBtn");
        markStart?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark Start in Granular Speed Editor.");

            int currentMs = (int)(GetCurrentTime() * 1000);

            int? overlapIdx = FindSegmentAtPosition(currentMs);
            if (overlapIdx.HasValue)
            {
                var overlapping = _segments[overlapIdx.Value];
                ShowFeedback($"⚠ Inside segment #{overlapIdx.Value + 1}! Delete it first.");
                NotifyError($"Cannot mark here — overlaps segment #{overlapIdx.Value + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
                return;
            }

            _selectedSegmentIndex = -1;
            UpdateDeleteButtonVisibility();

            _pendingStartMs = currentMs;
            ShowFeedback($"START: {FormatMs(_pendingStartMs)}");
            RedrawTimeline();
        });

        var markEndBtn = this.FindControl<Button>("MarkEndBtn");
        markEndBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RuntimeLog.Info("UI", "User clicked Mark End in Granular Speed Editor.");

            int currentMs = (int)(GetCurrentTime() * 1000);

            if (_pendingStartMs < 0)
            {
                int? overlapIdx = FindSegmentAtPosition(currentMs);
                if (overlapIdx.HasValue)
                {
                    var overlapping = _segments[overlapIdx.Value];
                    ShowFeedback($"⚠ Inside segment #{overlapIdx.Value + 1}! Delete it first.");
                    NotifyError($"Cannot mark here — overlaps segment #{overlapIdx.Value + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
                    return;
                }
            }

            if (_pendingStartMs >= 0 && currentMs <= _pendingStartMs)
            {
                ShowFeedback("⚠ END can't be before START");
                NotifyError($"Cannot mark END at {FormatMs(currentMs)} — it must be AFTER the START at {FormatMs(_pendingStartMs)}.");
                return;
            }

            _pendingEndMs = currentMs;

            _selectedSegmentIndex = -1;

            if (_pendingStartMs < 0)
            {
                int prevEndMs = -1;
                foreach (var s in _segments)
                {
                    if (s.EndMs <= _pendingEndMs && (int)s.EndMs > prevEndMs)
                        prevEndMs = (int)s.EndMs;
                }

                if (prevEndMs < 0)
                {
                    _pendingStartMs = 0;
                }
                else
                {
                    _pendingStartMs = prevEndMs + 1000;
                    if (_pendingStartMs > _pendingEndMs)
                    {
                        _pendingStartMs = prevEndMs;
                    }
                }
            }

            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
                
                var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
                var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
                if (playIcon != null && pauseIcon != null)
                {
                    playIcon.IsVisible = true;
                    pauseIcon.IsVisible = false;
                }
            }

            NotifyUndoable($"Segment added at {FormatMs(_pendingEndMs)}", "MarkEndBtn");   // ANCHOR_01
            
            AddPendingSegment();

            if (_segments.Count > 0)
            {
                _selectedSegmentIndex = _segments.Count - 1;
            }
            UpdateDeleteButtonVisibility();
            RedrawTimeline();
        });

        var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
        if (speedSlider != null)
        {
            speedSlider.ValueChanged += (_, e) =>
            {
                _pendingSpeed = Math.Round(e / 10.0, 2);
                var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
                if (lbl != null) lbl.Text = $"{_pendingSpeed:0.0}x";

                if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
                {
                    var seg = _segments[_selectedSegmentIndex];
                    PushUndo("change speed", "seg-speed");   // UNDO_02
                    _segments[_selectedSegmentIndex] = seg with { Speed = _pendingSpeed };
                    RefreshSegmentList();
                    RedrawTimeline();
                }
            };
        }

        WireUpSpeedPresets(speedSlider);

        WireUpFreezeImage();

        var deleteSegBtn = this.FindControl<Button>("DeleteSegmentBtn");
        deleteSegBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            ExecuteDeleteSelectedSegment();
        });


        var clearBtn = this.FindControl<Button>("ClearAllSegmentsBtn");
        clearBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            UpdateClearAllPromptText();
            if (!FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmGranularClearAll)
            {
                clearBtn.Flyout?.Hide();
                ExecuteClearAllSegments();
            }
        });

        var confirmClearAll = this.FindControl<Button>("ConfirmClearAllSegmentsBtn");
        confirmClearAll?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            this.FindControl<Button>("ClearAllSegmentsBtn")?.Flyout?.Hide();
            ExecuteClearAllSegments();
        });

        var keepAll = this.FindControl<Button>("KeepAllSegmentsBtn");
        keepAll?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            this.FindControl<Button>("ClearAllSegmentsBtn")?.Flyout?.Hide();
            RuntimeLog.Info("UI", "User backed out of Clear All in Granular Speed Editor.");
        });

        var acceptBtn = this.FindControl<Button>("AcceptGranularBtn");
        if (acceptBtn != null) acceptBtn.Click += (s, e) => {
            RuntimeLog.Info("UI", "User clicked Accept in Granular Speed Editor.");
            Accepted = true;
            // UNDO_01 — the project has been handed to the Main App. Undoing into a state that was
            // never applied would show the user history that no longer matches their project.
            ClearUndoHistory("changes applied");
            Close();
        };

        var cancelBtn = this.FindControl<Button>("CancelGranularBtn");
        if (cancelBtn != null)
        {
            _cancelConfirmFlyout = cancelBtn.Flyout;
            cancelBtn.Click += (_, _) =>
            {
                if (cancelBtn.Flyout != null) return;
                RuntimeLog.Info("UI", "Cancel in Granular Speed Editor with no changes — closing without prompting.");
                Avalonia.Threading.Dispatcher.UIThread.Post(Close, Avalonia.Threading.DispatcherPriority.Background);
            };
        }
        UpdateCancelConfirmState();

        var confirmCancel = this.FindControl<Button>("ConfirmCancelGranularBtn");
        confirmCancel?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            var btn = this.FindControl<Button>("CancelGranularBtn");
            btn?.Flyout?.Hide();
            RuntimeLog.Info("UI", "User confirmed Cancel in Granular Speed Editor.");
            Avalonia.Threading.Dispatcher.UIThread.Post(Close, Avalonia.Threading.DispatcherPriority.Background);
        });

        UpdateTooltips();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, GranularKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyUpEvent, GranularKeyUpHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void WireUpFreezeImage()
    {
        var freezeImageToggle = this.FindControl<Button>("FreezeImageToggle");

        var freezePresets = new[] {
            this.FindControl<Button>("FreezePreset05"), this.FindControl<Button>("FreezePreset10"), this.FindControl<Button>("FreezePreset15"),
            this.FindControl<Button>("FreezePreset20"), this.FindControl<Button>("FreezePreset25"), this.FindControl<Button>("FreezePreset30")
        };
        double[] presetValues = { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };

        Avalonia.Media.IBrush PresetBrush(string key, string fallbackHex)
            => this.TryFindResource(key, ActualThemeVariant, out object? v) && v is Avalonia.Media.IBrush b
                ? b
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(fallbackHex));

        var selectedBg = PresetBrush("AppSelectedPresetBackground", "#14532d");
        var selectedBorder = PresetBrush("AppSelectedPresetBorder", "#22c55e");
        var selectedFg = PresetBrush("AppSelectedPresetForeground", "#86efac");

        void SetFreezePresetSelection(int selectedIndex)
        {
            for (int j = 0; j < freezePresets.Length; j++)
            {
                var preset = freezePresets[j];
                if (preset == null) continue;

                if (j == selectedIndex)
                {
                    preset.Classes.Remove("Primary");
                    preset.Background = selectedBg;
                    preset.BorderBrush = selectedBorder;
                    preset.Foreground = selectedFg;
                }
                else
                {
                    preset.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    preset.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    preset.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        }

        int stepperIndex = 0;
        int _freezePulseCount = 0;
        _freezePulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _freezePulseTimer.Tick += (_, _) =>
        {
            stepperIndex = (stepperIndex + 1) % freezePresets.Length;
            _freezePulseCount++;

            var hint1 = this.FindControl<TextBlock>("FreezeHintLabel");
            var hint2 = this.FindControl<TextBlock>("FreezeHintLabelBottom");
            double newOpacity = (stepperIndex % 2 == 0) ? 1.0 : 0.0;
            if (hint1 != null) hint1.Opacity = newOpacity;
            if (hint2 != null) hint2.Opacity = newOpacity;

            if (_freezePulseCount >= 20)
            {
                _freezePulseTimer?.Stop();
                if (hint1 != null) hint1.Opacity = 1.0;
                if (hint2 != null) hint2.Opacity = 1.0;
            }

            for (int j = 0; j < freezePresets.Length; j++)
            {
                var b = freezePresets[j];
                if (b == null) continue;

                bool isSelected = (Math.Abs(_selectedFreezePresetS - presetValues[j]) < 0.01);
                if (isSelected) continue;

                if (j == stepperIndex)
                {
                    b.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(120, 90, 26));
                    b.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(250, 197, 22));
                    b.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 247, 237));
                }
                else
                {
                    b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        };

        for (int i = 0; i < freezePresets.Length; i++)
        {
            var btn = freezePresets[i];
            var val = presetValues[i];
            int presetIndex = i;
            if (btn != null)
            {
                btn.Click += (_, _) =>
                {
                    _selectedFreezePresetS = val;

                    _freezePulseTimer?.Stop();

                    SetFreezePresetSelection(presetIndex);

                    var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                    if (hint != null) hint.IsVisible = false;
                    var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                    if (hintBottom != null) hintBottom.IsVisible = false;

                    SetFreezePromptControlsEnabled(true);

                    var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("FreezeValidationPopup");
                    if (popup != null) popup.IsOpen = false;

                    if (_freezeTimeMs >= 0)
                    {
                        PushUndo("change freeze length", "freeze-len");   // UNDO_02
                        _freezeDurationS = val;
                        RedrawTimeline();
                        FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked freeze preset button. Set freeze duration to {val}s.");
                        ShowFeedback($"FREEZE CREATED: {val:0.0}s");
                    }
                };
            }
        }

        if (freezeImageToggle != null)
        {
            freezeImageToggle.Click += async (_, _) =>
            {
                if (_freezeTimeMs < 0)
                {
                    bool promptPreset = (_selectedFreezePresetS < 0);

                    if (promptPreset)
                    {
                        ShowFeedback("SELECT FREEZE DURATION");
                        
                        _freezePulseCount = 0;
                        _freezePulseTimer?.Start();

                        var hint = this.FindControl<TextBlock>("FreezeHintLabel");
                        if (hint != null) hint.IsVisible = true;
                        var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
                        if (hintBottom != null) hintBottom.IsVisible = true;

                        SetFreezePromptControlsEnabled(false);

                        FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", "State Change: User clicked 'Freeze Image' toggle but no preset was selected. Showing hint + gentle pulse + greying out other controls.");
                    }

                    double currentAbsMs = (_videoHost?.IpcClient?.CurrentTime ?? 0) * 1000.0;
                    if (_videoHost != null && _videoHost.IpcClient != null) {
                        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                    }
                    if (currentAbsMs < _trimStartMs) currentAbsMs = _trimStartMs;
                    if (_trimEndMs > 0 && currentAbsMs > _trimEndMs) currentAbsMs = _trimEndMs;
                    PushUndo("set freeze");   // UNDO_01
                    _freezeTimeMs = currentAbsMs;

                    _freezeDurationS = promptPreset ? Infrastructure.SettingsManager.Instance.Defaults.DefaultFreezeDurationS : _selectedFreezePresetS;

                    var icon = this.FindControl<TextBlock>("FreezeImageToggleIcon");
                    var txt = this.FindControl<TextBlock>("FreezeImageToggleText");
                    if (icon != null) icon.Text = "🔓";
                    if (txt != null) txt.Text = "UNFREEZE IMAGE";
                    freezeImageToggle.Classes.Remove("Primary");
                    freezeImageToggle.Classes.Add("Danger");

                    RedrawTimeline();
                    UpdateDeleteButtonVisibility();
                    FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked 'Freeze Image' toggle. Button set to State 2 (Active/Red - UNFREEZE IMAGE).");

                    if (!promptPreset)
                    {
                        NotifyUndoable($"Freeze created ({_freezeDurationS:0.0}s)", "FreezeImageToggle");   // ANCHOR_01
                        for (int k = 0; k < presetValues.Length; k++)
                        {
                            if (Math.Abs(presetValues[k] - _selectedFreezePresetS) < 0.01)
                            {
                                SetFreezePresetSelection(k);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    ClearFreezeImage("FREEZE IMAGE REMOVED");
                    FortniteVideoSoftware.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked 'Unfreeze Image' toggle. Button released to State 1 (Default/Blue - FREEZE IMAGE). Existing freeze instance was deleted from the timeline.");
                }
            };
        }
    }

    private void WireUpSpeedPresets(FortniteVideoSoftware.App.Controls.SpinningWheelSlider? speedSlider)
    {
        SpeedPresetButtons.ConfigureBaseButton(
            this,
            _baseSpeed,
            $"Set speed to Main screen base speed {SpeedPresetButtons.FormatSpeed(_baseSpeed)}");

        SpeedPresetButtons.WirePresetButtons(this, _baseSpeed, s =>
        {
            SpeedPresetButtons.SetSpinningWheelValue(speedSlider, s);
            _pendingSpeed = s;
            var lbl = this.FindControl<TextBlock>("PendingSpeedLabel");
            if (lbl != null) lbl.Text = $"{s:0.0}x";

            if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
            {
                var seg = _segments[_selectedSegmentIndex];
                PushUndo("change speed");   // UNDO_02
                _segments[_selectedSegmentIndex] = seg with { Speed = s };
                RefreshSegmentList();
                RedrawTimeline();
            }
        });
    }

    /// <summary>
    /// Issue #6: Update visibility of DELETE SEGMENT and CLEAR ALL buttons.
    /// DELETE SEGMENT: visible only when a segment is selected.
    /// CLEAR ALL: visible when any segment exists.
    /// </summary>

    private string _openingSignature = "";
    private Avalonia.Controls.Primitives.FlyoutBase? _cancelConfirmFlyout;

    /// <summary>Everything a CANCEL would discard, flattened into one comparable string.</summary>
    private string BuildStateSignature()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_baseSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(_freezeTimeMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(_freezeDurationS.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        foreach (var s in _segments)
        {
            sb.Append(s.StartMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(s.EndMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(s.ZoomX?.ToString() ?? "-").Append(',')
              .Append(s.ZoomY?.ToString() ?? "-").Append(',')
              .Append(s.ZoomW?.ToString() ?? "-").Append(',')
              .Append(s.ZoomH?.ToString() ?? "-").Append(',')
              .Append(s.ZoomSlow ? "S" : "I").Append(',')
              .Append(s.ZoomStartMs?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append(',')
              .Append(s.ZoomEndMs?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append(';');
        }
        return sb.ToString();
    }

    /// <summary>True when CANCEL would actually throw work away.</summary>
    private bool IsDirty()
        => BuildStateSignature() != _openingSignature
           || _pendingStartMs >= 0 || _pendingEndMs >= 0;

    /// <summary>
    /// Attaches the confirm flyout only while dirty. Called from
    /// <see cref="UpdateDeleteButtonVisibility"/>, which already runs after essentially every
    /// mutation, so the state cannot go stale.
    /// </summary>
    private void UpdateCancelConfirmState()
    {
        var btn = this.FindControl<Button>("CancelGranularBtn");
        if (btn == null || _cancelConfirmFlyout == null) return;
        var wanted = IsDirty() ? _cancelConfirmFlyout : null;
        if (!ReferenceEquals(btn.Flyout, wanted)) btn.Flyout = wanted;
    }

    private void UpdateDeleteButtonVisibility()
    {
        UpdateCancelConfirmState();
        var deleteSegBtn = this.FindControl<Button>("DeleteSegmentBtn");
        var clearAllBtn = this.FindControl<Button>("ClearAllSegmentsBtn");

        bool segSelected = _selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count;

        if (deleteSegBtn != null)
            deleteSegBtn.IsVisible = segSelected;

        if (clearAllBtn != null)
            clearAllBtn.IsVisible = _segments.Count > 0 || _freezeTimeMs >= 0;

        var removeZoomBtn = this.FindControl<Button>("RemoveZoomBtn");
        if (removeZoomBtn != null)
            removeZoomBtn.IsVisible = segSelected && _segments[_selectedSegmentIndex].ZoomW.HasValue;

        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        if (zoomBtn != null)
        {
            zoomBtn.IsVisible = true;
            if (!segSelected && _zoomModeActive) ExitZoomMode();
        }
        SyncZoomModeChecksFromSegment();
    }

    /// <summary>
    /// LANES_02 — commits a segment swept out by dragging across the upper lane.
    ///
    /// Routes through the SAME validation the MARK START / MARK END buttons use rather than
    /// inserting directly: the 1000ms neighbour gap, the overlap ban and the minimum length are
    /// export-correctness rules, not UI politeness, and a second creation path that skipped them
    /// would produce filter graphs the buttons could never produce.
    /// </summary>
    private void CreateSegmentFromDrag(double startMs, double endMs)
    {
        double dur = GetDuration();
        if (dur <= 0) return;

        double totalMs = dur * 1000.0;
        int start = (int)Math.Round(Math.Clamp(startMs, 0, totalMs));
        int end = (int)Math.Round(Math.Clamp(endMs, 0, totalMs));

        if (end - start < SegMinWidthMs)
        {
            NotifyError($"That block would be too short — drag out at least {SegMinWidthMs}ms.");
            return;
        }

        _pendingStartMs = start;
        _pendingEndMs = end;

        int before = _segments.Count;
        AddPendingSegment();

        if (_segments.Count > before)
        {
            int newIdx = _segments.FindIndex(sg => sg.StartMs == start && sg.EndMs == end);
            if (newIdx >= 0)
            {
                SelectSegment(newIdx);
                Notify($"Segment #{newIdx + 1} added and selected: {FormatMs(start)} – {FormatMs(end)} @ {_segments[newIdx].Speed:0.0}x.");
                return;
            }
        }

        RefreshSegmentList();
        UpdateDeleteButtonVisibility();
    }

    private void AddPendingSegment()
    {
        PushUndo("add segment");   // UNDO_01 — before the list changes
        if (_pendingStartMs < 0 || _pendingEndMs < 0)
        {
            NotifyError("Mark a START and END time first.");
            return;
        }

        double start = Math.Min(_pendingStartMs, _pendingEndMs);
        double end   = Math.Max(_pendingStartMs, _pendingEndMs);

        bool snapped = false;
        string snapMsg = "";

        foreach (var seg in _segments)
        {
            if (start < seg.EndMs && end > seg.StartMs)
            {
                NotifyError($"Cannot add segment: Overlaps existing segment [{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}].");
                return;
            }
            
            bool tooCloseBefore = seg.EndMs <= start && start - seg.EndMs < SegGapMs;
            bool tooCloseAfter  = seg.StartMs >= end && seg.StartMs - end < SegGapMs;
            
            if (tooCloseBefore)
            {
                start = seg.EndMs + SegGapMs;
                snapped = true;
                snapMsg = $"Pushed the start to {FormatMs(start)} to keep a safe {SegGapMs/1000.0}s distance from the previous segment.";
            }
            if (tooCloseAfter)
            {
                end = seg.StartMs - SegGapMs;
                snapped = true;
                if (tooCloseBefore) snapMsg = $"Squeezed the segment to fit exactly between the two neighbours ({FormatMs(start)} – {FormatMs(end)}).";
                else snapMsg = $"Pushed the end to {FormatMs(end)} to keep a safe {SegGapMs/1000.0}s distance from the next segment.";
            }
        }
        
        if (end - start < 10)
        {
            NotifyError($"Not enough room! After keeping the required {SegGapMs/1000.0}s gap, the segment would be too small to create.");
            return;
        }

        double speed = _baseSpeed;
        _pendingSpeed = _baseSpeed;
        var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
        var speedLbl = this.FindControl<TextBlock>("PendingSpeedLabel");
        if (speedSlider != null) SpeedPresetButtons.SetSpinningWheelValue(speedSlider, _baseSpeed);
        if (speedLbl != null) speedLbl.Text = $"{_baseSpeed:0.0}x";
        PushUndo("add segment");   // UNDO_02
        _segments.Add(new SpeedSegment((int)start, (int)end, speed));
        _segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        _pendingStartMs = -1;
        _pendingEndMs   = -1;
        RefreshSegmentList();
        
        if (snapped)
        {
            NotifyError(snapMsg);
        }
        else
        {
            Notify($"Segment added: {FormatMs(start)} – {FormatMs(end)} @ {speed:0.0}x");
        }
    }

    private void RefreshSegmentList()
    {
        var panel = this.FindControl<ListBox>("SegmentsPanel");
        if (panel == null) return;
        panel.Items.Clear();

        var countLbl = this.FindControl<TextBlock>("SegmentCountLabel");
        if (countLbl != null)
            countLbl.Text = _segments.Count == 0 ? "No segments" : $"{_segments.Count} segment{(_segments.Count == 1 ? "" : "s")}";

        for (int i = 0; i < _segments.Count; i++)
        {
            int idx = i;
            var seg = _segments[i];
            bool isSelected = idx == _selectedSegmentIndex;

            var border = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isSelected ? "#3d4f63" : "#1e293b")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isSelected ? "#fde047" : "#334155")),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 1),
                Padding = new Thickness(7, 3),
                Focusable = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(border, "Click to select this segment");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var info = new TextBlock
            {
                Text = $"{FormatClock(seg.StartMs)} → {FormatClock(seg.EndMs)}   {seg.Speed:0.0}x{(seg.ZoomW.HasValue ? "  🔍" : "")}",
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")),
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(10.5),
                FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };

            var delBtn = new Button
            {
                Content = "✕",
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Background = Infrastructure.ThemeResources.Brush(this, "AppDangerDeepBorderBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#521818"))),   // TONE_01
                Foreground = Avalonia.Media.Brushes.White,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 0, 0)
            };
            Avalonia.Automation.AutomationProperties.SetName(delBtn, $"Delete segment {idx + 1} of {_segments.Count}");
            ToolTip.SetTip(delBtn, "Delete this segment");
            delBtn.Click += (_, e) =>
            {
                e.Handled = true;
                RequestDeleteSegment(idx);
            };

            // ZOOMLIVE_02 — the right-hand pane is now EXACTLY the timeline. It used to run its
            // own copy of the selection logic that never moved the playhead and never synced the
            // zoom ramp radios, so clicking a row and clicking a block did different things.
            void SelectThisSegment()
            {
                SelectSegment(idx, jumpPlayhead: true);
                SetStatus($"Selected segment #{idx + 1}. Change speed, press DELETE to remove, or drag its zoom box to re-aim it.");
            }

            border.PointerEntered += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#26364a"));
            };
            border.PointerExited += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1e293b"));
            };
            border.GotFocus += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38bdf8"));
            };
            border.LostFocus += (_, _) =>
            {
                if (_selectedSegmentIndex != idx)
                    border.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#334155"));
            };
            border.PointerPressed += (_, _) => SelectThisSegment();
            border.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter || e.Key == Avalonia.Input.Key.Space)
                {
                    SelectThisSegment();
                    e.Handled = true;
                }
            };

            Grid.SetColumn(info, 0);
            Grid.SetColumn(delBtn, 1);
            grid.Children.Add(info);
            grid.Children.Add(delBtn);
            border.Child = grid;
            panel.Items.Add(border);
        }
    }

    /// <summary>
    /// FOCUS_01 — grabbing a freeze popsicle takes focus and starts the matching edge drag.
    ///
    /// <para>
    /// ⚠️ THE PRESS CAPTURES THE LANE CANVAS, NOT THE MARKER. This looks wrong and is the only
    /// thing that works: <see cref="RedrawTimeline"/> rebuilds the whole marker overlay on every
    /// drag step, so a pointer captured by the marker is captured by a control that is destroyed
    /// microseconds later — the drag dies on the first frame. The canvas outlives every redraw, and
    /// its existing PointerMoved / the window's PointerReleased already know how to run a
    /// <see cref="FreezeDragMode"/> to completion. So this handler's whole job is to set the mode,
    /// take focus, and hand the gesture over.
    /// </para>
    /// <para>
    /// Start marker resizes from the start, end marker resizes from the end — matching the band's
    /// own edge grips exactly, so grabbing the popsicle and grabbing the edge beneath it do the
    /// same thing. Moving the hold whole is the band's BODY, as it is for every speed block.
    /// </para>
    /// </summary>
    private void AttachFreezeMarkerInteractions(Control marker, FreezeMarkerEnd which, Canvas timelineCanvas)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (_freezeDragMode == FreezeDragMode.None) MainWindow.SetTimelineCameraHover(marker, false);
        };
        marker.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(marker).Properties;
            if (props.IsRightButtonPressed) { ClearTimelineSelection(); e.Handled = true; return; }
            if (!props.IsLeftButtonPressed) return;

            double w = timelineCanvas.Bounds.Width;
            if (w <= 0 || _freezeTimeMs < 0) return;

            double holdStart = FreezeHoldStartOutSec();
            double holdEnd = holdStart + _freezeDurationS;

            var grabbed = which;
            if (grabbed == FreezeMarkerEnd.None) return;

            FocusFreezeMarker(grabbed);

            _freezeDragMode = grabbed == FreezeMarkerEnd.Start
                ? FreezeDragMode.ResizeStart
                : FreezeDragMode.ResizeEnd;
            _freezeDragFixedEndOutSec = holdEnd;
            _freezeDragGrabOffsetSec = 0;

            marker.Focus();
            MainWindow.SetTimelineCameraHover(marker, true);
            e.Pointer.Capture(timelineCanvas);
            SetStatus(grabbed == FreezeMarkerEnd.Start
                ? "Dragging the freeze START — release to set."
                : "Dragging the freeze END — release to set.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    /// <summary>
    /// FOCUS_01 — gives one freeze marker focus, taking it away from everything else.
    /// Focus is exclusive across the whole timeline: one object at a time, always.
    /// </summary>
    private void FocusFreezeMarker(FreezeMarkerEnd which)
    {
        _isFreezeCameraSelected = which != FreezeMarkerEnd.None;
        _freezeFocus = which;
        _zoomFocus = null;
        if (_selectedSegmentIndex >= 0)
        {
            _selectedSegmentIndex = -1;
            RefreshSegmentList();
        }
        UpdateDeleteButtonVisibility();
    }

    /// <summary>
    /// FOCUS_01 — DROPS FOCUS FROM EVERYTHING ON THE TIMELINE.
    ///
    /// <para>
    /// A selected object stays selected until the user says otherwise, and there are exactly three
    /// ways to say it: Esc, a right-click anywhere, or selecting something else. All three land
    /// here, so they cannot drift apart — and any object added to this lane later has one obvious
    /// place to be cleared from.
    /// </para>
    /// </summary>
    private void ClearTimelineSelection()
    {
        bool hadSomething = _isFreezeCameraSelected
                            || _freezeFocus != FreezeMarkerEnd.None
                            || _zoomFocus != null
                            || _selectedSegmentIndex >= 0;

        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _zoomFocus = null;
        _selectedSegmentIndex = -1;
        _freezeDragMode = FreezeDragMode.None;
        _zoomDragSegment = -1;
        _isDraggingZoomMarker = false;

        if (!hadSomething) return;

        UpdateDeleteButtonVisibility();
        RefreshSegmentList();
        RedrawTimeline();
        SetStatus("Nothing selected.");
    }

    /// <summary>
    /// FOCUS_01 — selects a speed block and syncs every control that reflects the selection.
    ///
    /// <para>
    /// Extracted because the click path did all of this inline and the drag-to-create path did
    /// none of it, which is exactly why a freshly swept-out block came back unselected: the block
    /// existed, but nothing had told the slider, the delete button or the marching ants about it.
    /// </para>
    /// </summary>
    private void SelectSegment(int index) => SelectSegment(index, jumpPlayhead: true);

    /// <summary>
    /// ZOOMLIVE_02 — THE ONE WAY A SEGMENT BECOMES SELECTED. Everything funnels here.
    ///
    /// <para>
    /// There used to be FOUR selection paths — this method, <c>SelectSegmentAt</c>, the timeline's
    /// own pointer handler and the right-hand list row's click — and they did different things.
    /// Clicking a row did not move the playhead; clicking the timeline did not sync the Slow/Instant
    /// radios; only one of them closed zoom mode. So "select a segment" meant four different things
    /// depending on where you clicked, which is exactly the complaint this change answers.
    /// </para>
    /// <para>
    /// THREE THINGS ALWAYS HAPPEN NOW: the block is selected, the playhead jumps to its first frame
    /// and PAUSES there (you cannot aim a zoom box at a moving picture), and if the block carries a
    /// zoom its editing box re-opens exactly as it was registered — position, size and ramp mode.
    /// A block with no zoom just gets selected; the box does not appear uninvited.
    /// </para>
    /// <para>
    /// <paramref name="jumpPlayhead"/> is false only for callers that are ALREADY driving the
    /// playhead themselves — a drag in progress, or an undo restore — where seeking would fight
    /// them.
    /// </para>
    /// </summary>
    private void SelectSegment(int index, bool jumpPlayhead)
    {
        if (index < 0 || index >= _segments.Count) return;

        bool changed = index != _selectedSegmentIndex;

        // ZOOMLIVE_07 — CLOSE THE OLD BOX WHILE THE OLD INDEX IS STILL CURRENT.
        // ExitZoomMode may delete an abandoned auto-created block, and that decision has to be made
        // about the block being LEFT, not the one being selected. Doing it after re-pointing the
        // selection is how you delete the wrong segment.
        if (changed && _zoomModeActive)
        {
            int countBefore = _segments.Count;
            int orphanWas = _zoomCreatedSegmentIndex;

            ExitZoomMode();

            // ⚠️ If the cleanup removed a block that sat BEFORE the one being selected, every index
            // after it shifted down by one — including the caller's. Selecting `index` unadjusted
            // would land on the block AFTER the one that was clicked.
            if (_segments.Count < countBefore && orphanWas >= 0 && orphanWas < index) index--;

            if (index >= _segments.Count) index = _segments.Count - 1;
            if (index < 0) return;
        }

        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _zoomFocus = null;
        _selectedSegmentIndex = index;
        _selectedMemeId = null;              // exactly one object on this timeline is ever selected

        var seg = _segments[index];
        _pendingSpeed = seg.Speed;

        var speedSlider = this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider");
        var speedLbl = this.FindControl<TextBlock>("PendingSpeedLabel");
        if (speedSlider != null && seg.Speed >= 0.01) SpeedPresetButtons.SetSpinningWheelValue(speedSlider, seg.Speed);
        if (speedLbl != null) speedLbl.Text = $"{seg.Speed:0.0}x";

        UpdateDeleteButtonVisibility();
        UpdateMemeButtonsState();
        SyncZoomModeChecksFromSegment();

        if (jumpPlayhead) JumpPlayheadToSegmentEdge(index, toStart: true);

        // ZOOMLIVE_02 — a zoomed block re-opens its box. Any box belonging to a DIFFERENT block was
        // already closed above, while its own index was still current.
        if (seg.ZoomW.HasValue && !_zoomModeActive) EnterZoomMode();

        RefreshSegmentList();
        RedrawTimeline();
    }

    /// <summary>
    /// ZOOMLIVE_02 — parks the playhead on the first (or last) frame of a block and STOPS there.
    ///
    /// <para>
    /// Pausing is not incidental. The whole reason to jump is so the user can see the frame they
    /// are aiming a zoom box at; a picture that keeps moving under the box makes the aim guesswork.
    /// </para>
    /// <para>
    /// ⚠️ The caret is set through the same sticky-hold path the meme drag uses, so the playback
    /// tick cannot immediately drag it back to wherever mpv happens to be mid-seek.
    /// </para>
    /// </summary>
    private void JumpPlayheadToSegmentEdge(int index, bool toStart)
    {
        if (index < 0 || index >= _segments.Count) return;
        try
        {
            var ipc = _videoHost?.IpcClient;
            if (ipc != null && !ipc.IsPaused) _ = ipc.SetPropertyAsync("pause", "yes");

            var seg = _segments[index];
            double relMs = Math.Max(0, (toStart ? seg.StartMs : seg.EndMs));
            SetPlayheadFromScrub(relMs);
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>
    /// FREEZE_DRAG — keeps the hold inside the clip and inside its own legal length.
    /// Called after every drag step, so a sweep off the end of the timeline parks at the end
    /// instead of storing a freeze the exporter would have to guess about.
    /// </summary>
    private void ClampFreezeIntoClip()
    {
        if (_freezeTimeMs < 0) return;
        double dur = GetDuration();
        if (dur <= 0) return;

        _freezeDurationS = Math.Clamp(_freezeDurationS, MinFreezeDurationS, MaxFreezeDurationS);
        _freezeTimeMs = Math.Clamp(_freezeTimeMs, _trimStartMs, _trimStartMs + dur * 1000.0);
    }


    private void MoveFreezeCameraByFrames(int frameDelta)
    {
        double duration = GetDuration();
        if (_freezeTimeMs < 0 || duration <= 0)
        {
            return;
        }

        double fps = 60.0;
        double deltaMs = (1000.0 / fps) * frameDelta;
        double minMs = _trimStartMs;
        double maxMs = _trimStartMs + duration * 1000.0;
        PushUndo("move freeze", "freeze-drag");   // UNDO_02
        _freezeTimeMs = Math.Clamp(_freezeTimeMs + deltaMs, minMs, maxMs);
        SeekGranularPreviewToFreezeMarker();
        RedrawTimeline();
        SetStatus($"Freeze moved to {FormatMs(_freezeTimeMs - _trimStartMs)}.");
    }

    /// <summary>
    /// FOCUS_01 — trims or extends the hold a frame at a time, for when the end marker has focus.
    /// The start stays where it is; only how long the frame is held changes.
    /// </summary>
    private void NudgeFreezeDurationByFrames(int frameDelta)
    {
        if (_freezeTimeMs < 0) return;

        const double Fps = 60.0;
        _freezeDurationS = Math.Round(
            Math.Clamp(_freezeDurationS + frameDelta / Fps, MinFreezeDurationS, MaxFreezeDurationS), 3);
        RedrawTimeline();
        SetStatus($"Freeze held for {_freezeDurationS:0.00}s.");
    }

    private void SeekGranularPreviewToFreezeMarker()
    {
        if (_videoHost?.IpcClient == null || _freezeTimeMs < 0)
        {
            return;
        }

        _isCurrentlyFrozen = false;
        _holdCaretOutSec = null;
        _freezeArmed = true;
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        _ = _videoHost.IpcClient.SendCommandAsync(
            "seek",
            (_freezeTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "absolute");
    }

    private bool _redrawQueued;


    private void UpdateDraggingVisuals(int segIndex, double newStartMs, double newEndMs)
    {
        var canvas = _segmentCanvas;
        if (canvas == null) return;
        double w = canvas.Bounds.Width;
        double dur = GetDuration();
        if (dur <= 0 || w <= 0) return;
        
        double x1 = SrcMsToX(newStartMs, w);
        double x2 = SrcMsToX(newEndMs, w);
        double segW = Math.Max(2, x2 - x1);
        
        foreach (Avalonia.Controls.Control child in canvas.Children)
        {
            if (child.Name == $"SegRect_{segIndex}" || child.Name == $"SegBorder_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x1);
                child.Width = segW;
            }
            else if (child.Name == $"SegEdgeStart_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x1 - 12);
            }
            else if (child.Name == $"SegEdgeEnd_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x2 - 12);
            }
        }
    }

    /// <summary>
    /// LANES_03 — fills the shared control's two lane slots with THIS window's content.
    ///
    /// The controls are created here rather than in XAML because they used to be named XAML
    /// elements that the rest of this file looks up by name; creating them with the SAME names and
    /// adding them to the shared host keeps every existing `FindControl` call working, so the
    /// drawing and drag pipelines did not have to be rewritten alongside the layout.
    /// </summary>
    private void BuildLaneContent()
    {
        var lanes = this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("GranularLanes");
        if (lanes?.LaneAHost == null || lanes.LaneBHost == null) return;

        var emptyLabel = new TextBlock
        {
            Name = "GranularEmptyLaneLabel",
            Text = "No Speed Segments Yet!",
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#94a3b8")),
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(12),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _emptyLaneLabel = emptyLabel;
        lanes.LaneAHost.Children.Add(emptyLabel);

        var segCanvas = new Avalonia.Controls.Canvas
        {
            Name = "GranularTimelineCanvas",
            IsHitTestVisible = true,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Focusable = true,
            ClipToBounds = false,
            MinHeight = LaneHeight
        };
        segCanvas.Classes.Add("TimelineSeekSurface");
        _segmentCanvas = segCanvas;
        lanes.LaneAHost.Children.Add(segCanvas);

        var thumbGrid = new Avalonia.Controls.Grid { Name = "GranularThumbnailLaneGrid", ClipToBounds = true };
        _thumbLaneGrid = thumbGrid;
        lanes.LaneBHost.Children.Add(thumbGrid);

        var thumbLoading = new Border
        {
            Name = "GranularThumbLoadingOverlay",
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#aa000000")),
            IsHitTestVisible = false,
            IsVisible = false,
            Child = new StackPanel
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 5,
                Children =
                {
                    new ProgressBar { IsIndeterminate = true, MinWidth = 60, MinHeight = 3 },
                    new TextBlock
                    {
                        Text = "Generating Frames...",
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    }
                }
            }
        };
        _thumbLoadingOverlay = thumbLoading;
        lanes.LaneBHost.Children.Add(thumbLoading);

        lanes.LaneASeekable = false;
        lanes.LaneBSeekable = true;

        lanes.SeekRequested += outSec =>
        {
            var tl = OutTimeline();
            double srcSec = tl.OutputToSourceRelative(outSec);

            _holdCaretOutSec = tl.IsHoldingFrameAt(outSec) ? outSec : (double?)null;

            _playheadMs = srcSec * 1000.0;
            UpdateCaret();
            _ = SeekInternal(srcSec);
        };
    }

    private TextBlock? _emptyLaneLabel;
    private Avalonia.Controls.Canvas? _segmentCanvas;
    private Avalonia.Controls.Grid? _thumbLaneGrid;
    private Border? _thumbLoadingOverlay;

    private void RedrawTimeline()
    {
        var canvas = _segmentCanvas;
        if (canvas == null) return;

        if (_redrawQueued) return;
        _redrawQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            canvas.Children.Clear();

            var emptyLabel = _emptyLaneLabel;
            if (emptyLabel != null)
                emptyLabel.IsVisible = _segments.Count == 0 && _freezeTimeMs < 0
                                       && _pendingStartMs < 0 && !_createDragActive;

            UpdateCaret();
            RelayoutFrameLane();
            double dur = GetDuration();
            double w = canvas.Bounds.Width;
            double h = Math.Max(canvas.Bounds.Height, LaneBlockHeight);
            if (dur <= 0 || w <= 0) return;

            var pendingZoomHeads = new List<(int Index, double StartX, double EndX)>();

            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                double x1 = SrcMsToX(seg.StartMs, w);
                double x2 = SrcMsToX(seg.EndMs,   w);
                bool isSelected = i == _selectedSegmentIndex;

                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Name = $"SegRect_{i}",
                    Width  = Math.Max(2, x2 - x1),
                    Height = h,
                    Fill   = new Avalonia.Media.SolidColorBrush(GetSegmentOverlayColor(seg)),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(rect, x1);
                Avalonia.Controls.Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);

                if (seg.ZoomW.HasValue)
                {
                    double zsMs = seg.ZoomStartMs ?? seg.StartMs;
                    double zeMs = seg.ZoomEndMs ?? seg.EndMs;
                    
                    double zx1 = SrcMsToX(zsMs, w);
                    double zx2 = SrcMsToX(zeMs, w);


                    pendingZoomHeads.Add((i, zx1, zx2));
                }

                if (isSelected)
                {
                    var antsBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047"));
                    double segW = Math.Max(2, x2 - x1);
                    
                    var borderRect = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Name = $"SegBorder_{i}",
                        Width = segW,
                        Height = h,
                        Stroke = antsBrush,
                        StrokeThickness = 1,
                        StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2),
                        StrokeDashOffset = _marchingAntsOffset,
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(borderRect, x1);
                    Avalonia.Controls.Canvas.SetTop(borderRect, 0);
                    canvas.Children.Add(borderRect);
                    _selectedSegmentBorderRef = borderRect;

                    AddSegmentEdgeMarker(canvas, i, isStart: true,  markerX: x1, h: h, canvasWidth: w, durationSeconds: dur, blockWidthPx: segW);
                    AddSegmentEdgeMarker(canvas, i, isStart: false, markerX: x2, h: h, canvasWidth: w, durationSeconds: dur, blockWidthPx: segW);
                }
                else
                {
                    if (_selectedSegmentBorderRef != null && _selectedSegmentIndex == -1)
                        _selectedSegmentBorderRef = null;
                }
            }


            if (_createDragActive)
            {
                double ax = SrcMsToX(Math.Min(_createDragStartMs, _createDragCurrentMs), w);
                double bx = SrcMsToX(Math.Max(_createDragStartMs, _createDragCurrentMs), w);
                var ghost = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = Math.Max(1, bx - ax),
                    Height = h,
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 255, 255, 255)),
                    Stroke = Avalonia.Media.Brushes.SeaGreen,
                    StrokeThickness = 2,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(ghost, ax);
                Avalonia.Controls.Canvas.SetTop(ghost, 0);
                canvas.Children.Add(ghost);
            }

            if (_pendingStartMs >= 0)
            {
                double px = SrcMsToX(_pendingStartMs, w);
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }

            if (_pendingEndMs >= 0)
            {
                double px = SrcMsToX(_pendingEndMs, w);
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }

            var markerOverlay = this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("GranularLanes")?.MarkerOverlayHost;
            markerOverlay?.Children.Clear();
            _freezeMarkerAnts.Clear();

            double MarkerStickHeight(double markerTopPx)
            {
                double overlayH = markerOverlay is { } mo && mo.Bounds.Height > 1
                    ? mo.Bounds.Height
                    : MarkerOverlayFallbackHeightPx;
                return overlayH - markerTopPx - MainWindow.TimelineCameraStickTopPx;
            }

            if (_freezeTimeMs >= 0)
            {
                double freezeRelMs = Math.Clamp(_freezeTimeMs - _trimStartMs, 0, dur * 1000.0);
                double freezeHoldPx = (_freezeDurationS / OutDurationSec()) * w;
                double freezeX = SrcMsToX(freezeRelMs, w) - freezeHoldPx;

                DecorateFrozenSpan(canvas, freezeX, freezeHoldPx, h, withLabel: false, withGrips: true);

                double startLeftPx = MainWindow.ClampTimelineCameraLeft(freezeX + LaneBorderInsetPx, w);
                double endLeftPx = MainWindow.ClampTimelineCameraLeft(freezeX + freezeHoldPx + LaneBorderInsetPx, w);
                double headStaggerPx = Math.Abs(endLeftPx - startLeftPx) >= FreezeMarkerHeadWidthPx
                    ? 0.0
                    : FreezeMarkerEndStaggerPx;

                Control BuildFreezeMarker(FreezeMarkerEnd which, double leftPx, string tip)
                {
                    var cam = MainWindow.CreateTimelineCameraIcon(
                        _isFreezeCameraSelected && _freezeFocus == which,
                        _marchingAntsOffset,
                        out var iconAnts,
                        out var lineAnts);
                    _freezeMarkerAnts.Add(iconAnts);
                    _freezeMarkerAnts.Add(lineAnts);
                    ToolTip.SetTip(cam, tip);
                    double camTop = which == FreezeMarkerEnd.End
                        ? FreezeMarkerOverlayTop + headStaggerPx
                        : FreezeMarkerOverlayTop;
                    Avalonia.Controls.Canvas.SetTop(cam, camTop);
                    Avalonia.Controls.Canvas.SetLeft(cam, leftPx);
                    MainWindow.StretchTimelineCameraStick(cam, MarkerStickHeight(camTop));
                    AttachFreezeMarkerInteractions(cam, which, canvas);
                    return cam;
                }

                string held = $"Freeze at {FormatMs(freezeRelMs)}, held {_freezeDurationS:0.00}s.";
                var startCam = BuildFreezeMarker(FreezeMarkerEnd.Start, startLeftPx,
                    held + "\nDrag to move where the hold begins. Drag the band's body to move the whole freeze.");
                var endCam = BuildFreezeMarker(FreezeMarkerEnd.End, endLeftPx,
                    held + "\nDrag to change how long the frame is held.");

                var markerParent = markerOverlay ?? canvas;
                if (headStaggerPx > 0)
                {
                    markerParent.Children.Add(startCam);
                    markerParent.Children.Add(endCam);
                }
                else
                {
                    markerParent.Children.Add(endCam);
                    markerParent.Children.Add(startCam);
                }
            }

            if (pendingZoomHeads.Count > 0)
            {
                var zoomParent = markerOverlay ?? canvas;

                foreach (var (index, zx1, zx2) in pendingZoomHeads)
                {
                    double zStartLeft = MainWindow.ClampTimelineCameraLeft(zx1 + LaneBorderInsetPx, w);
                    double zEndLeft = MainWindow.ClampTimelineCameraLeft(zx2 + LaneBorderInsetPx, w);
                    double zStagger = Math.Abs(zEndLeft - zStartLeft) >= FreezeMarkerHeadWidthPx
                        ? 0.0
                        : FreezeMarkerEndStaggerPx;

                    Control BuildZoomMarker(bool isStart, double leftPx, double topPx, string tip)
                    {
                        var zcam = MainWindow.CreateZoomTimelineCameraIcon(
                            _zoomFocus is { } zf && zf.Segment == index && zf.IsStart == isStart,
                            _marchingAntsOffset,
                            out var zIconAnts,
                            out var zLineAnts);
                        _freezeMarkerAnts.Add(zIconAnts);
                        _freezeMarkerAnts.Add(zLineAnts);
                        ToolTip.SetTip(zcam, tip);
                        Avalonia.Controls.Canvas.SetTop(zcam, topPx);
                        Avalonia.Controls.Canvas.SetLeft(zcam, leftPx);
                        MainWindow.StretchTimelineCameraStick(zcam, MarkerStickHeight(topPx));
                        AttachZoomMarkerInteractions(zcam, index, isStart, canvas);
                        return zcam;
                    }

                    var zStartCam = BuildZoomMarker(true, zStartLeft, FreezeMarkerOverlayTop,
                        $"Zoom on segment #{index + 1}.\nDrag to move where the zoom begins.");
                    var zEndCam = BuildZoomMarker(false, zEndLeft, FreezeMarkerOverlayTop + zStagger,
                        $"Zoom on segment #{index + 1}.\nDrag to move where the zoom ends.");

                    if (zStagger > 0)
                    {
                        zoomParent.Children.Add(zStartCam);
                        zoomParent.Children.Add(zEndCam);
                    }
                    else
                    {
                        zoomParent.Children.Add(zEndCam);
                        zoomParent.Children.Add(zStartCam);
                    }
                }
            }

            // MEME_06 — drawn LAST so the bands and their clown heads sit above the speed blocks
            // and the freeze band. A meme is the only thing on this ruler that is foreign footage
            // rather than a treatment of the gameplay, so it reads correctly on top.
            DrawMemeBands(canvas, markerOverlay, w, h, MarkerStickHeight);
        });
    }


    // ══════════════════════════════════════════════════════════════════════════════════════
    // MEME_06 — DRAGGING A MEME, AND WHY IT USES ITS OWN RULER.
    //
    // This is the same trap FREEZE_DRAG documents, and it bites harder here. Ask "which gameplay
    // moment is under this pixel" of a ruler that CONTAINS the meme, and every pixel inside the
    // meme block answers with the SAME instant — the anchor — so the block pins itself and will not
    // move. Worse, the block's own length shifts everything after it, so the pointer and the block
    // chase each other.
    //
    // The fix is a ruler that holds everything EXCEPT the meme being dragged: the speed segments,
    // the freeze, the cuts and every OTHER meme. That ruler is fixed for the whole gesture — the
    // only thing changing is the excluded meme's anchor — so "where did the user point" has a
    // stable answer from press to release.
    //
    // ⚠️ NOT BaseTimeline(). That one also drops the freeze, which is right for dragging the freeze
    // and wrong here: a meme must still be positioned relative to a freeze that exists.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>MEME_06 — the ruler used for the whole of one meme drag. Built at press, dropped at release.</summary>
    private FortniteVideoSoftware.Core.Media.OutputTimeline? _memeDragTimeline;

    /// <summary>MEME_06 — the dragged meme's block on the LIVE ruler, seeded at press for the pointer maths.</summary>
    private double _memeDragBlockStartOutSec;
    private double _memeDragBlockLenOutSec;

    /// <summary>
    /// MEME_06 (DRAG_FIX) — where the dragged block STARTS on the live ruler, RIGHT NOW.
    ///
    /// <para>
    /// This used to be read from the value cached at press. That is wrong the instant the meme
    /// moves: the block start is the pivot <see cref="OutXToMemeDragSec"/> compensates around, so a
    /// stale pivot mis-classifies every pointer position between the old start and the new one. The
    /// visible symptom is the band jumping a full meme-length away from the cursor when the drag
    /// reverses direction, then snapping back — the "unstable" behaviour.
    /// </para>
    /// <para>
    /// The drag ruler holds everything except this meme, so the block's start on the LIVE ruler is
    /// exactly where its anchor lands on the drag ruler: the live ruler IS the drag ruler with a
    /// block of this length spliced in at that point. No extra timeline build is needed.
    /// </para>
    /// </summary>
    private double MemeDragBlockStartOutSec()
    {
        if (_draggingMemeId == null || _memeDragTimeline == null) return _memeDragBlockStartOutSec;
        int idx = _memes.FindIndex(m => m.Id == _draggingMemeId);
        if (idx < 0) return _memeDragBlockStartOutSec;
        return _memeDragTimeline.SourceToOutput(_memes[idx].AtSourceSecRelative);
    }

    private FortniteVideoSoftware.Core.Media.OutputTimeline BuildMemeDragTimeline(string excludeId)
    {
        double durSec = Math.Max(0.001, GetDuration());
        var segs = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment>(_segments);
        if (_freezeTimeMs >= 0 && _freezeDurationS > 0)
        {
            double relStart = _freezeTimeMs - _trimStartMs;
            segs.Add(new FortniteVideoSoftware.Core.Media.SpeedSegment(
                relStart, relStart + _freezeDurationS * 1000.0, 0.0));
        }

        var others = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.MemePlacement>();
        foreach (var m in _memes) if (m.Id != excludeId) others.Add(m);

        return FortniteVideoSoftware.Core.Media.OutputTimeline.Create(
            durSec * 1000.0, segs, 1.0, 0,
            FortniteVideoSoftware.Core.Media.MemePlacement.ToInsertions(others),
            CutsForTimeline());
    }

    /// <summary>
    /// MEME_06 — a canvas X, expressed in seconds on the DRAG ruler (the one without this meme).
    ///
    /// Directly modelled on <see cref="OutXToBaseOutSec"/>: the canvas is drawn against the LIVE
    /// ruler, so a pointer past the block's start carries the block's length in it and that length
    /// has to come back out before the value means anything on the drag ruler.
    /// </summary>
    private double OutXToMemeDragSec(double x, double w)
    {
        if (w <= 0) return 0;
        double outSec = Math.Clamp((x / w) * OutDurationSec(), 0, OutDurationSec());

        double blockStart = MemeDragBlockStartOutSec();
        if (_memeDragBlockLenOutSec <= 0 || outSec <= blockStart) return outSec;
        return Math.Max(blockStart,
                        outSec - Math.Min(_memeDragBlockLenOutSec, outSec - blockStart));
    }

    /// <summary>
    /// MEME_06 — pointer wiring for the meme BAND. The two clown heads get no drag handlers at all,
    /// which is the deliberate difference from the freeze and zoom popsicles: a freeze and a zoom
    /// each have two independent decisions to make, whereas a meme's length is the meme file's own
    /// length. Offering a resize grip would advertise a control that cannot do anything.
    ///
    /// Capture goes to the CANVAS, never to the band: every drag step calls RedrawTimeline, which
    /// tears the band out of the visual tree and builds a replacement, and a pointer captured to a
    /// control that gets unparented loses capture mid-gesture (the lesson recorded on
    /// AttachZoomMarkerInteractions).
    /// </summary>
    private void AttachMemeBandInteractions(Control band, string memeId, Avalonia.Controls.Canvas timelineCanvas)
    {
        band.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;

            int idx = _memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;

            double w = Math.Max(1, timelineCanvas.Bounds.Width);

            // Cache the block's live position BEFORE anything moves — OutXToMemeDragSec needs it.
            _memeDragBlockStartOutSec = 0;
            _memeDragBlockLenOutSec = 0;
            foreach (var r in OutTimeline().InsertionOutputRanges())
            {
                if (r.Id != memeId) continue;
                _memeDragBlockStartOutSec = r.StartOutputSec;
                _memeDragBlockLenOutSec = Math.Max(0, r.EndOutputSec - r.StartOutputSec);
                break;
            }

            _memeDragTimeline = BuildMemeDragTimeline(memeId);
            _memeDragLastX = -1;                       // (DRAG_FIX)
            _draggingMemeId = memeId;

            // MEME_07 — a cutaway must not fire while the user is holding the block. The director
            // already refuses to fire on a seek, and the drag seeks constantly, but saying so
            // explicitly is cheaper than relying on that.
            if (_memePreview != null) _memePreview.Suspended = true;

            // MEME_08 — the drag scrubs the picture to the frame the meme will land on, and a
            // still frame cannot be read off a moving picture. Paused for the gesture, restored
            // on release exactly as it was found.
            _memeDragWasPlaying = _videoHost?.IpcClient != null && !_videoHost.IpcClient.IsPaused;
            if (_memeDragWasPlaying) _ = _videoHost!.IpcClient!.SetPropertyAsync("pause", "yes");
            _selectedMemeId = memeId;

            // Exactly one object on this timeline is ever selected.
            _selectedSegmentIndex = -1;
            _isFreezeCameraSelected = false;
            UpdateDeleteButtonVisibility();
            UpdateMemeButtonsState();

            double anchorOnDragRuler = _memeDragTimeline.SourceToOutput(_memes[idx].AtSourceSecRelative);
            _memeDragGrabOffsetOutSec = OutXToMemeDragSec(e.GetPosition(timelineCanvas).X, w) - anchorOnDragRuler;

            e.Pointer.Capture(timelineCanvas);
            SetStatus("Moving the meme — release to set. Its length cannot change; it is the meme's own length.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    /// <summary>
    /// MEME_06 — one step of a meme drag, called from the canvas pointer-moved handler.
    /// Returns true when it consumed the event.
    /// </summary>
    private bool PumpMemeDrag(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Canvas canvas)
    {
        if (_draggingMemeId == null || _memeDragTimeline == null) return false;

        double w = Math.Max(1, canvas.Bounds.Width);

        // (DRAG_FIX) Sub-pixel pointer noise cannot move a meme, but it can still cost a timeline
        // rebuild and a full canvas teardown. Swallow it before any of that runs.
        double px = e.GetPosition(canvas).X;
        if (_memeDragLastX >= 0 && Math.Abs(px - _memeDragLastX) < 0.5)
        {
            e.Handled = true;
            return true;
        }
        _memeDragLastX = px;

        // UNDO_02 — one snapshot per gesture, coalesced on the key so a drag is a single undo step.
        PushUndo("move meme", "meme-drag");

        double targetOnDragRuler = Math.Max(0, OutXToMemeDragSec(e.GetPosition(canvas).X, w) - _memeDragGrabOffsetOutSec);
        double newSourceSec = _memeDragTimeline.OutputToSourceRelative(targetOnDragRuler);

        if (MoveMemeTo(_draggingMemeId, newSourceSec))
        {
            InvalidateMemeTimelines();
            RedrawTimeline();
            ShowMemeLandingFrame(_draggingMemeId);   // MEME_08
        }

        // MEME_09 — SAY WHY IT STOPPED. A meme cannot interrupt a speed block, so dragging one
        // across a long slow-mo stretch legitimately pins it at the edge. Silence there is
        // indistinguishable from the app having frozen, which is exactly how it was reported.
        var blocker = _memeDragBlockedBy;
        if (blocker != null)
        {
            SetStatus($"A meme cannot interrupt a {blocker.Speed:0.0}x block " +
                      $"({FormatMs(blocker.StartMs)}–{FormatMs(blocker.EndMs)}) — " +
                      "it is resting against its edge. Drag past the block to carry on.");
        }
        else
        {
            SetStatus("Moving the meme — release to set. Its length cannot change; it is the meme's own length.");
        }

        e.Handled = true;
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // MEME_07 — THE PREVIEW ACTUALLY PLAYS THE MEME.
    // ══════════════════════════════════════════════════════════════════════════════════════

    private void BuildMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => _videoHost?.IpcClient,
            () => _videoPath,
            () => _trimStartMs / 1000.0,
            SetMemeSwapOverlay,
            "Granular");

        // The gameplay's own soundtrack is not the meme's. Nothing else plays audio in this
        // window, so there is nothing to pause here — the hooks exist so the three windows that
        // DO have companion audio all wire the same two events.
        _memePreview.MemeEnded += () => { _holdCaretOutSec = null; UpdateCaret(); };

        _memePreview.SetMemes(_memes);
    }

    /// <summary>MEME_07 — the black-screen notice shown across the two file swaps.</summary>
    private void SetMemeSwapOverlay(bool visible, string message)
    {
        var overlay = this.FindControl<Border>("MemeSwapOverlay");
        var text = this.FindControl<TextBlock>("MemeSwapOverlayText");
        if (text != null && !string.IsNullOrEmpty(message)) text.Text = message;
        if (overlay != null) overlay.IsVisible = visible;
    }

    /// <summary>
    /// MEME_07 — where the caret sits while a meme is on screen.
    ///
    /// <para>
    /// This window's ruler is OUTPUT time, and a meme is the one thing on it that has a real width
    /// there, so the caret can do the honest thing and travel across the block as the meme plays.
    /// <c>SourceToOutput</c> of the anchor returns the moment the block ENDS (the documented
    /// boundary behaviour that <see cref="FreezeHoldStartOutSec"/> corrects for in the same way),
    /// so the block's start is that value minus the meme's length.
    /// </para>
    /// </summary>
    private void HoldCaretDuringMeme()
    {
        var d = _memePreview;
        if (d == null) return;

        try
        {
            double anchorRelSec = Math.Max(0, d.AnchorAbsSourceSec - (_trimStartMs / 1000.0));
            double blockEndOut = OutTimeline().SourceToOutput(anchorRelSec);
            double blockStartOut = Math.Max(0, blockEndOut - d.MemeDurationSec);

            _holdCaretOutSec = blockStartOut + Math.Clamp(d.MemeElapsedSec, 0, d.MemeDurationSec);
            UpdateCaret();
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>
    /// MEME_07 — hands the current placements to the director and, when they actually changed,
    /// stalls the window behind the blocking notice while everything downstream is rebuilt.
    ///
    /// <para>
    /// Called from ADD MEME, REMOVE MEME and the end of a drag. NOT from the middle of a drag: the
    /// timeline is rebuilt on every pointer move there already, and a blocking overlay that
    /// appeared mid-gesture would swallow the pointer and strand the drag.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task RefreshMemePreviewAsync(string what, bool stall = true)
    {
        var d = _memePreview;
        if (d == null) return;

        bool changed = d.SetMemes(_memes);
        if (!changed) return;

        // ══════════════════════════════════════════════════════════════════════════════════
        // MEME_09 — A MOVE IS NOT A REBUILD, AND MUST NOT BE DRESSED AS ONE.
        //
        // Adding or removing a meme changes the FINISHED LENGTH, so the ruler, the film strip and
        // every position after it genuinely have to be rebuilt — that earns the blocking notice.
        // MOVING one changes nothing about the length; only where the block sits. Raising a
        // full-window PLEASE WAIT curtain plus a 220ms settle every time the user nudged the band
        // by a few pixels is what made a simple drag feel like the app had seized up. Silent
        // refresh for a move.
        // ══════════════════════════════════════════════════════════════════════════════════
        if (!stall)
        {
            await d.AbortAsync();
            InvalidateMemeTimelines();
            RedrawTimeline();
            return;
        }

        var overlay = this.FindControl<Border>("MemeRebuildOverlay");
        var text = this.FindControl<TextBlock>("MemeRebuildOverlayText");
        if (text != null) text.Text = what;

        _memeRebuildStallActive = true;
        if (overlay != null) overlay.IsVisible = true;
        try
        {
            // A cutaway running while the user edits the placements is reasoning about a list that
            // no longer exists. Put the gameplay back first, then rebuild.
            await d.AbortAsync();

            InvalidateMemeTimelines();
            RedrawTimeline();
            await BuildFrameLaneAsync();

            // Land the preview on a frame that certainly still exists after the re-time, so the
            // first scrub after the stall starts from a truthful position.
            var ipc = _videoHost?.IpcClient;
            if (ipc != null)
            {
                await SeekInternal(_playheadMs / 1000.0);
                d.NotifySeek();
            }

            // Let the render thread actually put a frame up before the curtain lifts, otherwise
            // the overlay clears onto the black it was hiding.
            await System.Threading.Tasks.Task.Delay(220);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Meme preview rebuild failed: {ex.Message}");
        }
        finally
        {
            if (overlay != null) overlay.IsVisible = false;
            _memeRebuildStallActive = false;
            UpdateCaret();
        }
    }

    /// <summary>
    /// MEME_08 — PUT THE PICTURE ON THE FRAME THE MEME WILL INTERRUPT.
    ///
    /// <para>
    /// Called on every step of a meme drag and once more on release. Two different positions are
    /// being set here and they are easy to confuse:
    /// </para>
    /// <list type="bullet">
    ///   <item>the CARET goes to the block's START in OUTPUT seconds — the instant in the finished
    ///         video at which the meme begins. <c>SourceToOutput</c> of the anchor returns the
    ///         moment the block ENDS (the documented boundary behaviour), so its length comes
    ///         back off, exactly as <see cref="FreezeHoldStartOutSec"/> does for a freeze;</item>
    ///   <item>the PICTURE goes to the anchor in CLIP-RELATIVE SOURCE seconds — the gameplay frame
    ///         the meme cuts away from.</item>
    /// </list>
    /// <para>
    /// Seeks are coalesced by <see cref="SeekInternal"/> (it keeps only the newest target while one
    /// is in flight), so this is safe to call at pointer-move rate.
    /// </para>
    /// </summary>
    private void ShowMemeLandingFrame(string memeId)
    {
        int idx = _memes.FindIndex(m => m.Id == memeId);
        if (idx < 0) return;
        var meme = _memes[idx];

        try
        {
            double blockEndOut = OutTimeline().SourceToOutput(meme.AtSourceSecRelative);
            _holdCaretOutSec = Math.Max(0, blockEndOut - meme.DurationSec);
            _memeCaretSticky = true;
            _playheadMs = Math.Max(0, meme.AtSourceSecRelative * 1000.0);
            UpdateCaret();

            if (_videoHost?.IpcClient != null) _ = SeekInternal(meme.AtSourceSecRelative);
            _memePreview?.NotifySeek();
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>MEME_06 — ends a meme drag. Safe to call when none is running.</summary>
    private bool EndMemeDrag(Avalonia.Input.PointerEventArgs e)
    {
        if (_draggingMemeId == null) return false;

        string? movedId = _draggingMemeId;
        int idx = _memes.FindIndex(m => m.Id == _draggingMemeId);
        if (idx >= 0)
        {
            RuntimeLog.Info("MEME",
                $"Moved '{System.IO.Path.GetFileName(_memes[idx].FilePath)}' to {_memes[idx].AtSourceSecRelative:0.###}s source-relative.");
        }

        _draggingMemeId = null;
        _memeDragTimeline = null;
        _memeDragBlockStartOutSec = 0;
        _memeDragBlockLenOutSec = 0;
        _memeDragLastX = -1;                           // (DRAG_FIX)
        _memeDragBlockedBy = null;                     // MEME_09

        e.Pointer.Capture(null);
        EndUndoGesture();   // UNDO_02
        InvalidateMemeTimelines();
        RedrawTimeline();

        // MEME_08 — settle on the exact landing frame one last time, so the frame on screen at
        // release is the frame the meme will interrupt, not whichever one the last throttled seek
        // happened to reach.
        if (movedId != null) ShowMemeLandingFrame(movedId);

        if (_memePreview != null) _memePreview.Suspended = false;

        // MEME_08 — hand playback back exactly as it was found. The caret stays parked on the
        // landing frame only while paused, so resuming releases it on its own.
        if (_memeDragWasPlaying && _videoHost?.IpcClient != null)
        {
            _memeCaretSticky = false;
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
        }
        _memeDragWasPlaying = false;

        // MEME_09 — stall:false. A move does not change the finished length, so there is nothing
        // to wait for and no reason to blank the window.
        _ = RefreshMemePreviewAsync("", stall: false);

        e.Handled = true;
        return true;
    }

    /// <summary>
    /// MEME_06 — draws every meme: a purple band across the output span it occupies, and a clown
    /// popsicle at each end whose hairline crosses the ruler at the exact instant.
    ///
    /// The band is what carries the drag. The heads are decoration and hit-test transparent, so a
    /// press anywhere on the block — including on a head — lands on the band and moves the whole
    /// thing, which is the only gesture a meme has.
    /// </summary>
    private void DrawMemeBands(
        Avalonia.Controls.Canvas canvas,
        Avalonia.Controls.Panel? markerOverlay,
        double w,
        double h,
        System.Func<double, double> markerStickHeight)
    {
        if (_memes.Count == 0 || w <= 0) return;

        double outDur = OutDurationSec();
        if (outDur <= 0.0001) return;

        var memeColour = Infrastructure.ThemeResources.Colour(this, "AppMemeColor", Avalonia.Media.Color.FromRgb(124, 58, 237));
        var bandFill = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromArgb(120, memeColour.R, memeColour.G, memeColour.B));
        var bandFillSelected = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromArgb(185, memeColour.R, memeColour.G, memeColour.B));

        foreach (var range in OutTimeline().InsertionOutputRanges())
        {
            var placement = _memes.FirstOrDefault(m => m.Id == range.Id);
            if (placement == null) continue;

            double x1 = Math.Clamp((range.StartOutputSec / outDur) * w, 0, w);
            double x2 = Math.Clamp((range.EndOutputSec / outDur) * w, 0, w);
            double bandW = Math.Max(2, x2 - x1);
            bool isSelected = _selectedMemeId == range.Id;

            // The VISUAL band is drawn at its true width — it must not lie about how much of the
            // finished video the meme occupies.
            var band = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = isSelected ? bandFillSelected : bandFill,
                Width = bandW,
                Height = h,
                IsHitTestVisible = false,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
            };
            Avalonia.Controls.Canvas.SetLeft(band, x1);
            Avalonia.Controls.Canvas.SetTop(band, 0);
            canvas.Children.Add(band);

            // ══════════════════════════════════════════════════════════════════════════════
            // MEME_09 — THE GRAB AREA IS SEPARATE FROM THE PAINT, AND HAS A FLOOR.
            //
            // A meme occupies its own length in OUTPUT seconds, so on a long clip it is a sliver:
            // a 5-second meme in a 10-minute video is 0.8% of the ruler — about 8px on a 1000px
            // timeline, and the user has to hit it while it is the only draggable thing in that
            // 8px. That is the "hard to hit" half of the complaint. The invisible hit rectangle is
            // held to MemeGrabMinWidthPx and centred on the band, so a short meme is as easy to
            // grab as a long one while the coloured band still shows the truth.
            // ══════════════════════════════════════════════════════════════════════════════
            double grabW = Math.Max(bandW, MemeGrabMinWidthPx);
            double grabX = Math.Clamp(x1 + (bandW - grabW) / 2.0, 0, Math.Max(0, w - grabW));

            var grab = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = Avalonia.Media.Brushes.Transparent,
                Width = grabW,
                Height = h,
                IsHitTestVisible = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
            };
            string memeName = System.IO.Path.GetFileName(placement.FilePath);
            ToolTip.SetTip(grab,
                $"Meme: {memeName} ({placement.DurationSec:0.0}s)\n" +
                "Drag this band to move the whole meme. Its start and end cannot be dragged apart — " +
                "the length is the meme's own.");
            Avalonia.Controls.Canvas.SetLeft(grab, grabX);
            Avalonia.Controls.Canvas.SetTop(grab, 0);
            AttachMemeBandInteractions(grab, range.Id, canvas);
            canvas.Children.Add(grab);

            var markerParent = markerOverlay ?? canvas;
            double startLeft = MainWindow.ClampTimelineCameraLeft(x1 + LaneBorderInsetPx, w);
            double endLeft = MainWindow.ClampTimelineCameraLeft(x2 + LaneBorderInsetPx, w);
            double stagger = Math.Abs(endLeft - startLeft) >= FreezeMarkerHeadWidthPx ? 0.0 : FreezeMarkerEndStaggerPx;

            Control BuildMemeHead(double leftPx, double topPx, string tip)
            {
                var head = MainWindow.CreateMemeTimelineCameraIcon(
                    isSelected, _marchingAntsOffset, out var iconAnts, out var lineAnts);
                _freezeMarkerAnts.Add(iconAnts);
                _freezeMarkerAnts.Add(lineAnts);
                ToolTip.SetTip(head, tip);
                // Decoration only — the band underneath owns the gesture.
                head.IsHitTestVisible = false;
                Avalonia.Controls.Canvas.SetTop(head, topPx);
                Avalonia.Controls.Canvas.SetLeft(head, leftPx);
                MainWindow.StretchTimelineCameraStick(head, markerStickHeight(topPx));
                return head;
            }

            var startHead = BuildMemeHead(startLeft, FreezeMarkerOverlayTop,
                $"{memeName} starts here.\nDrag the purple band to move the whole meme.");
            var endHead = BuildMemeHead(endLeft, FreezeMarkerOverlayTop + stagger,
                $"{memeName} ends here, and the gameplay carries on.\nThis end cannot be dragged on its own.");

            if (stagger > 0)
            {
                markerParent.Children.Add(startHead);
                markerParent.Children.Add(endHead);
            }
            else
            {
                markerParent.Children.Add(endHead);
                markerParent.Children.Add(startHead);
            }
        }
    }

    private bool _isDraggingZoomMarker;

    /// <summary>
    /// ZOOMPOP_01 — pointer wiring for a zoom popsicle. Deliberately a mirror of
    /// <see cref="AttachFreezeMarkerInteractions"/>, and the three things it mirrors are the three
    /// things that were wrong before:
    ///
    /// <para>
    /// 1. THE GRABBED EDGE IS <paramref name="isStart"/>, NOT WHATEVER THE POINTER IS NEAREST. The
    /// head is 52px wide and centred on its own instant, so on a short zoom span the START head
    /// physically reaches past the midpoint of the span. Any positional inference resolves it to
    /// the far edge, which drives the wrong grip while the head under the cursor sits still — the
    /// user reads that as stutter/stick. See GRAB_01 on the freeze path for the same failure.
    /// </para>
    /// <para>
    /// 2. CAPTURE GOES TO THE CANVAS, NOT TO THE MARKER. Every drag step calls RedrawTimeline,
    /// which tears this control out of the visual tree and builds a replacement; a pointer captured
    /// to the control loses capture the moment it is unparented, and the drag dies mid-gesture. The
    /// canvas survives the redraw, so the move/release handlers there carry the gesture through.
    /// </para>
    /// <para>
    /// 3. FOCUS IS EXCLUSIVE. Pressing a zoom head takes focus away from the freeze heads and hands
    /// the segment its selection, so exactly one object on the timeline is ever selected.
    /// </para>
    /// </summary>
    private void AttachZoomMarkerInteractions(Control marker, int segIndex, bool isStart, Avalonia.Controls.Canvas timelineCanvas)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (_zoomDragSegment < 0) MainWindow.SetTimelineCameraHover(marker, false);
        };
        marker.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(marker).Properties;
            if (props.IsRightButtonPressed) { ClearTimelineSelection(); e.Handled = true; return; }
            if (!props.IsLeftButtonPressed) return;
            if (segIndex < 0 || segIndex >= _segments.Count) return;
            if (timelineCanvas.Bounds.Width <= 0) return;

            FocusZoomMarker(segIndex, isStart);

            _zoomDragSegment = segIndex;
            _zoomDragIsStart = isStart;
            _isDraggingZoomMarker = true;

            marker.Focus();
            MainWindow.SetTimelineCameraHover(marker, true);
            e.Pointer.Capture(timelineCanvas);

            // ZOOMLIVE_03 — the picture jumps to the END YOU GRABBED, not to the block's start.
            // Grabbing the END marker to fine-tune where the zoom stops, and being shown the frame
            // where it STARTS, is the wrong frame for the decision being made.
            JumpPlayheadToZoomEdge(segIndex, isStart);

            SetStatus(isStart
                ? "Dragging the zoom START — the picture follows it. Release to set."
                : "Dragging the zoom END — the picture follows it. Release to set.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    /// <summary>
    /// ZOOMLIVE_05 — KEEP A ZOOM INSIDE THE BLOCK THAT OWNS IT.
    ///
    /// <para>
    /// <c>ZoomStartMs</c> / <c>ZoomEndMs</c> are deliberately independent of the block's own edges —
    /// that is what the two magnifiers exist to control. The consequence nobody asks for: shrink or
    /// move the block afterwards and the zoom span can end up partly or wholly OUTSIDE it. The
    /// exporter would then be handed a zoom phase covering source time this block never renders,
    /// and the preview and the export would disagree about when the zoom happens.
    /// </para>
    /// <para>
    /// Called on every block move/resize release. It only ever narrows, never widens, so a user who
    /// deliberately zoomed a sub-range keeps their sub-range unless the block shrank past it.
    /// </para>
    /// </summary>
    private void ClampZoomInsideItsBlock(int index)
    {
        if (index < 0 || index >= _segments.Count) return;
        var seg = _segments[index];
        if (!seg.ZoomW.HasValue) return;
        if (!seg.ZoomStartMs.HasValue && !seg.ZoomEndMs.HasValue) return;

        double zs = Math.Clamp(seg.ZoomStartMs ?? seg.StartMs, seg.StartMs, seg.EndMs);
        double ze = Math.Clamp(seg.ZoomEndMs   ?? seg.EndMs,   seg.StartMs, seg.EndMs);
        // An inverted span collapses onto its start — the block shrank past the whole zoom range.
        // (Was written as a tuple swap that self-assigned `zs`, which is what CS1717 caught.)
        if (ze < zs) ze = zs;

        if (Math.Abs(zs - (seg.ZoomStartMs ?? seg.StartMs)) < 0.5
            && Math.Abs(ze - (seg.ZoomEndMs ?? seg.EndMs)) < 0.5) return;

        _segments[index] = seg with { ZoomStartMs = zs, ZoomEndMs = ze };
        RuntimeLog.Info("Granular",
            $"Zoom span on segment #{index + 1} pulled back inside its block: " +
            $"{FormatMs(zs)}–{FormatMs(ze)} (block {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)}).");
    }

    /// <summary>
    /// ZOOMLIVE_04 — the right-click menu on a coloured timeline block.
    ///
    /// <para>
    /// It used to hold a single "Delete Segment" entry. The two zoom actions are the ones a user
    /// reaches for most and had no home: EDIT ZOOM opens the box on this block, REMOVE ZOOM strips
    /// the zoom and KEEPS the speed change. Both are hidden on a block that has no zoom rather than
    /// shown greyed out — a menu of things you cannot do is noise.
    /// </para>
    /// <para>
    /// ⚠️ DELETE BLOCK REMOVES THE WHOLE THING, speed and zoom together. That is deliberate and it
    /// is why REMOVE ZOOM sits directly above it: a zoomed slow-motion block is one object, and
    /// "delete" on one object means the object.
    /// </para>
    /// </summary>
    private void ShowSegmentContextMenu(Avalonia.Controls.Canvas canvas, int segIndex)
    {
        if (segIndex < 0 || segIndex >= _segments.Count) return;

        SelectSegment(segIndex, jumpPlayhead: true);

        // Control, not MenuItem: a real Separator goes in this list, and Avalonia does not
        // reinterpret a MenuItem whose header is "-" the way WPF does.
        var items = new System.Collections.Generic.List<Avalonia.Controls.Control>();
        bool hasZoom = _segments[segIndex].ZoomW.HasValue;

        if (hasZoom)
        {
            var edit = new Avalonia.Controls.MenuItem
            {
                Header = "Edit Zoom",
                Icon = new TextBlock { Text = "\U0001F50D", Margin = new Avalonia.Thickness(0) }
            };
            edit.Click += (_, _) =>
            {
                if (_selectedSegmentIndex < 0) return;
                if (!_zoomModeActive) EnterZoomMode();
                Notify("Drag the box to re-aim it. Every change is saved as you go.");
            };
            items.Add(edit);

            var removeZoom = new Avalonia.Controls.MenuItem
            {
                Header = "Remove Zoom (keep the speed)",
                Icon = new TextBlock { Text = "\U0001F6AB", Margin = new Avalonia.Thickness(0) }
            };
            removeZoom.Click += (_, _) =>
            {
                if (_zoomModeActive) ExitZoomMode();
                RemoveZoomFromSelectedSegment();
            };
            items.Add(removeZoom);

            items.Add(new Avalonia.Controls.Separator());
        }

        var del = new Avalonia.Controls.MenuItem
        {
            Header = hasZoom ? "Delete Block (speed AND zoom)" : "Delete Segment",
            Icon = new TextBlock { Text = "\U0001F5D1", Margin = new Avalonia.Thickness(0) }
        };
        del.Click += (_, _) => { if (_selectedSegmentIndex >= 0) ExecuteDeleteSelectedSegment(); };
        items.Add(del);

        var menu = new Avalonia.Controls.ContextMenu { ItemsSource = items };
        menu.Open(canvas);
    }

    /// <summary>
    /// ZOOMLIVE_03 — parks the picture on the first or last frame of a segment's ZOOM span.
    ///
    /// <para>
    /// The zoom's own start/end are <see cref="SpeedSegment.ZoomStartMs"/> / <c>ZoomEndMs</c>, which
    /// are independent of the block's edges — that is the entire point of the two magnifiers. When
    /// they are unset the zoom covers the whole block, so the block's edges ARE the zoom's edges.
    /// </para>
    /// </summary>
    private void JumpPlayheadToZoomEdge(int segIndex, bool toStart)
    {
        if (segIndex < 0 || segIndex >= _segments.Count) return;
        try
        {
            var seg = _segments[segIndex];
            double ms = toStart
                ? (seg.ZoomStartMs ?? seg.StartMs)
                : (seg.ZoomEndMs ?? seg.EndMs);

            var ipc = _videoHost?.IpcClient;
            if (ipc != null && !ipc.IsPaused) _ = ipc.SetPropertyAsync("pause", "yes");

            SetPlayheadFromScrub(Math.Max(0, ms));
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>
    /// ZOOMPOP_01 / FOCUS_01 — gives one zoom popsicle focus and takes it from everything else.
    /// The owning segment is selected too, because the zoom span belongs to that segment and every
    /// control that edits the zoom reads <c>_selectedSegmentIndex</c>.
    /// </summary>
    private void FocusZoomMarker(int segIndex, bool isStart)
    {
        // ZOOMLIVE_03 — jumpPlayhead:false is load-bearing. The magnifier press seeks to the ZOOM
        // edge a moment later; letting the selection seek to the BLOCK start first would show the
        // wrong frame and fire a second seek for nothing.
        if (_selectedSegmentIndex != segIndex) SelectSegment(segIndex, jumpPlayhead: false);
        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _zoomFocus = (segIndex, isStart);
        UpdateDeleteButtonVisibility();
    }

    /// <summary>
    /// Grabbable vertical edge marker for the SELECTED speed segment — visual and drag
    /// behavior copied from the Main App's MARK START/END trim markers (24px hitbox,
    /// 3px SeaGreen stick, SizeWestEast cursor, hover highlight).
    /// The press routes into the EXISTING segment drag pipeline (ResizeStart/ResizeEnd,
    /// capture to the canvas), so the 1000ms neighbour gap, 200ms minimum width, the
    /// RuntimeLog "settled" entry, status text, segment list refresh, live preview
    /// speed mapping, Main App recovery persistence on Accept, and the FFmpeg export
    /// all flow through the exact same code path as block-edge resizing.
    /// </summary>
    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("GranularDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.GranularKey,
            "Preview Monitor — Granular Speed Editor",
            () => this.FindControl<Avalonia.Controls.Viewbox>("GranularPreviewViewbox"));

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("GranularPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => SetStatus(why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }

    /// <summary>
    /// UXQA_02: the detach button lives over the video, so while the user is drawing or adjusting a
    /// zoom box it is both a target that steals drags and visual clutter on the exact surface being
    /// worked on. Hide it for the duration; zoom mode is a focused sub-task and popping the monitor
    /// out mid-draw is not something anyone needs.
    /// </summary>
    private void UpdateDetachButtonForZoomMode()
    {
        var btn = this.FindControl<Button>("GranularDetachPreviewBtn");
        if (btn == null) return;
        btn.IsVisible = !_zoomModeActive || (_previewDetach?.IsDetached == true);
    }

    private void AddSegmentEdgeMarker(Avalonia.Controls.Canvas canvas, int segIndex, bool isStart, double markerX, double h, double canvasWidth, double durationSeconds, double blockWidthPx)
    {
        const double OuterReach = 12.0;
        double innerReach = Math.Clamp(blockWidthPx / 2.0, 0.0, OuterReach);
        double boxWidth = OuterReach + innerReach;
        double boxLeft = isStart ? markerX - OuterReach : markerX - innerReach;
        double stickOffset = isStart ? OuterReach : innerReach;

        var hitBox = new Avalonia.Controls.Border
        {
            Name = isStart ? $"SegEdgeStart_{segIndex}" : $"SegEdgeEnd_{segIndex}",
            Width = boxWidth,
            Height = h,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
            ZIndex = 110
        };
        var stick = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = Avalonia.Media.Brushes.SeaGreen,
            Width = 3,
            Height = h,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(stickOffset - 1.5, 0, 0, 0)
        };
        hitBox.Child = stick;
        ToolTip.SetTip(hitBox, isStart
            ? "Drag left/right to move this segment's START"
            : "Drag left/right to move this segment's END");

        Avalonia.Controls.Canvas.SetLeft(hitBox, boxLeft);
        Avalonia.Controls.Canvas.SetTop(hitBox, 0);

        hitBox.PointerEntered += (_, _) => { hitBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 46, 139, 87)); stick.Fill = Avalonia.Media.Brushes.MediumSeaGreen; };
        hitBox.PointerExited += (_, _) => { hitBox.Background = Avalonia.Media.Brushes.Transparent; stick.Fill = Avalonia.Media.Brushes.SeaGreen; };

        hitBox.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
            if (segIndex < 0 || segIndex >= _segments.Count) return;

            // ══════════════════════════════════════════════════════════════════════════
            // ZOOMLIVE_03 — MARKER PRECEDENCE. ONE MODE AT A TIME.
            //
            // A selected zoomed block puts FOUR grabbable things within a few pixels of each other:
            // this block's own START/END edges, and the zoom's two magnifiers. Where they overlap,
            // "whichever is nearer" is a coin toss precisely in the common case, so the mode
            // decides instead: while the zoom box is open the magnifiers own the clicks, and the
            // block's own edges stand down. Close the box (ZOOM-IN again, or Escape) and the edges
            // come straight back.
            // ══════════════════════════════════════════════════════════════════════════
            if (_zoomModeActive && segIndex == _selectedSegmentIndex)
            {
                SetStatus("The zoom box is open, so the magnifiers own this edge. Press ZOOM-IN or Escape to resize the block itself.");
                e.Handled = true;
                return;
            }

            var seg = _segments[segIndex];

            _selectedSegmentIndex = segIndex;
            _draggingSegmentIndex = segIndex;
            _segDragMode = isStart ? SegDragMode.ResizeStart : SegDragMode.ResizeEnd;
            _dragOrigStartMs = seg.StartMs;
            _dragOrigEndMs = seg.EndMs;
            double totalMs = durationSeconds * 1000.0;
            _dragStartPointerMs = canvasWidth > 0
                ? Math.Clamp((e.GetPosition(canvas).X / canvasWidth) * totalMs, 0, totalMs)
                : 0;

            e.Pointer.Capture(canvas);
            SetStatus($"Resizing segment #{segIndex + 1} — release to set.");
            e.Handled = true;
        };

        canvas.Children.Add(hitBox);
    }

    private bool _zoomModeActive;
    private enum ZoomDrag { None, Draw, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private ZoomDrag _zoomDrag = ZoomDrag.None;
    private Avalonia.Point _zoomDragStart;
    private Avalonia.Rect _zoomStartRect;
    private Avalonia.Rect _zoomUiRect;
    private bool _hasZoomBox;
    private readonly Avalonia.Controls.Shapes.Rectangle[] _zoomDim = new Avalonia.Controls.Shapes.Rectangle[4];
    private Avalonia.Controls.Shapes.Rectangle? _zoomBoxRect;
    private readonly Avalonia.Controls.Shapes.Rectangle[] _zoomHandles = new Avalonia.Controls.Shapes.Rectangle[4];
    private Avalonia.Controls.Border? _zoomTutorial;
    private DispatcherTimer? _zoomTutorialTimer;
    private const double ZoomHandlePx = 16;
    private const double ZoomHandleVisualPx = 13.6;
    private const double MaxZoomUpscale = 8.0;

    /// <summary>ZOOM_02 — how tight the auto-placed box starts. 2x = half the usable width.</summary>
    private const double DefaultZoomFactor = 2.0;

    /// <summary>
    /// OPTION C — stops the user parking two SLOW zooms close enough that the export has to take
    /// a glide away from one of them.
    ///
    /// WHY: a Slow zoom borrows 0.5s of footage before it to glide in and 0.5s after it to glide
    /// out. Two Slow zooms therefore need a full 1.0s between them
    /// (<see cref="FortniteVideoSoftware.Core.Media.GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms"/>)
    /// or neither of them can have a ramp on the facing side — Option A makes both snap in that
    /// case, which is correct but is not what someone who ticked "Slow" was going for.
    /// This clamp means they never reach that state by dragging in the first place.
    ///
    /// PRECEDENT: the editor already enforces exactly this kind of rule for speed blocks — a hard
    /// <see cref="SegGapMs"/> (1000ms) "social distance". This is the same idea for zooms, at the
    /// same distance, so it should feel familiar rather than new.
    ///
    /// SCOPE, deliberately narrow:
    ///   * only applies when the zoom being dragged is SLOW **and** the neighbour is SLOW. An
    ///     Instant zoom borrows nothing, so there is nothing to protect and clamping against it
    ///     would just remove freedom for no benefit.
    ///   * only clamps against the nearest zoom on the side being dragged.
    ///   * returns the value unchanged when it is already legal, so normal dragging is untouched.
    /// Flipping a zoom to Slow AFTER placing it can still produce a tight pair — that path is
    /// handled by Option A in the export maths (both simply snap), never by a broken half-zoom hop.
    /// </summary>
    private double ClampZoomEdgeAgainstSlowNeighbours(int segIndex, double proposedMs, bool isStart)
    {
        if (segIndex < 0 || segIndex >= _segments.Count) return proposedMs;
        var self = _segments[segIndex];
        if (!self.ZoomSlow) return proposedMs;

        double requiredGapMs =
            FortniteVideoSoftware.Core.Media.GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms * 1000.0;

        double clamped = proposedMs;

        for (int i = 0; i < _segments.Count; i++)
        {
            if (i == segIndex) continue;
            var other = _segments[i];
            if (!other.ZoomW.HasValue || !other.ZoomH.HasValue) continue;
            if (!other.ZoomSlow) continue;

            double otherStart = other.ZoomStartMs ?? other.StartMs;
            double otherEnd = other.ZoomEndMs ?? other.EndMs;

            if (isStart)
            {
                if (otherEnd <= clamped) clamped = Math.Max(clamped, otherEnd + requiredGapMs);
            }
            else
            {
                if (otherStart >= clamped) clamped = Math.Min(clamped, otherStart - requiredGapMs);
            }
        }

        if (Math.Abs(clamped - proposedMs) > 0.5)
        {
            SetStatus($"Held {FormatClock(requiredGapMs)} clear of the next slow zoom — closer than that and neither one can glide.");
        }

        return clamped;
    }

    /// <summary>
    /// ZOOMLIVE_05 — is there room for THIS zoom to become a gliding one?
    ///
    /// Mirrors <see cref="ClampZoomEdgeAgainstSlowNeighbours"/> exactly, but asks the yes/no
    /// question instead of moving an edge. Both must agree; if the gap constant changes, it changes
    /// for both because they read the same one.
    /// </summary>
    private bool SlowZoomHasRoom(int segIndex, out double requiredGapSec)
    {
        requiredGapSec = FortniteVideoSoftware.Core.Media.GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms;
        if (segIndex < 0 || segIndex >= _segments.Count) return true;

        var self = _segments[segIndex];
        if (!self.ZoomW.HasValue) return true;

        double gapMs = requiredGapSec * 1000.0;
        double selfStart = self.ZoomStartMs ?? self.StartMs;
        double selfEnd   = self.ZoomEndMs   ?? self.EndMs;

        for (int i = 0; i < _segments.Count; i++)
        {
            if (i == segIndex) continue;
            var other = _segments[i];
            if (!other.ZoomW.HasValue || !other.ZoomH.HasValue || !other.ZoomSlow) continue;

            double otherStart = other.ZoomStartMs ?? other.StartMs;
            double otherEnd   = other.ZoomEndMs   ?? other.EndMs;

            // Overlapping, or separated by less than one ramp's worth of air, in either direction.
            if (selfStart - otherEnd < gapMs && otherStart - selfEnd < gapMs) return false;
        }
        return true;
    }

    private double ZoomAspect => _isMobileFormat ? (2.0 / 3.0) : (16.0 / 9.0);

    private void WireZoomControls()
    {
        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        if (zoomBtn != null) zoomBtn.Click += (_, __) =>
        {
            // GUIDE_01 — ZOOM-IN needs a marked range for the same reason DELETE PARTS does, and
            // gets the same guided walkthrough instead of a dead click or a silent auto-created
            // block the user never asked for.
            if (GuideWhenNothingMarked("ZOOM-IN")) return;
            ToggleZoomMode();
        };

        var removeZoomBtn = this.FindControl<Button>("RemoveZoomBtn");
        if (removeZoomBtn != null) removeZoomBtn.Click += (_, __) => RemoveZoomFromSelectedSegment();

        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        if (canvas != null)
        {
            canvas.PointerPressed += ZoomCanvas_PointerPressed;
            canvas.PointerMoved += ZoomCanvas_PointerMoved;
            canvas.PointerReleased += ZoomCanvas_PointerReleased;
        }

        var slowCb = this.FindControl<RadioButton>("SlowZoomCheck");
        var instCb = this.FindControl<RadioButton>("InstantZoomCheck");
        if (slowCb != null) slowCb.IsCheckedChanged += (_, __) => OnZoomModeChanged(fromSlow: true);
        if (instCb != null) instCb.IsCheckedChanged += (_, __) => OnZoomModeChanged(fromSlow: false);

        var helpBtn = this.FindControl<Button>("HelpButton");
        var helpClose = this.FindControl<Button>("HelpCloseButton");
        var helpOverlay = this.FindControl<Avalonia.Controls.Grid>("HelpOverlay");
        if (helpBtn != null) helpBtn.Click += (_, __) => { if (helpOverlay != null) helpOverlay.IsVisible = true; };
        if (helpClose != null) helpClose.Click += (_, __) => { if (helpOverlay != null) helpOverlay.IsVisible = false; };
        if (helpOverlay != null)
            helpOverlay.PointerPressed += (s, e) => { if (ReferenceEquals(e.Source, helpOverlay)) helpOverlay.IsVisible = false; };

        var helpShowMe = this.FindControl<Button>("HelpShowMeButton");
        if (helpShowMe != null) helpShowMe.Click += (_, __) =>
        {
            if (helpOverlay != null) helpOverlay.IsVisible = false;
            Controls.CoachOverlay.Replay(this);
        };

        SyncZoomModeChecksFromSegment();

        var portraitGrid = this.FindControl<Avalonia.Controls.Grid>("GranularPortraitDimmingGrid");
        if (portraitGrid != null) portraitGrid.IsVisible = _isMobileFormat;
    }

    private void UpdateDragReadout(double startMs, double endMs)
    {
        var badge = this.FindControl<Border>("DragReadoutBadge");
        var txt = this.FindControl<TextBlock>("DragReadoutText");
        if (badge == null || txt == null) return;
        double lenS = Math.Max(0, endMs - startMs) / 1000.0;
        txt.Text = $"Start {FormatMs(startMs)}    End {FormatMs(endMs)}    Length {lenS:0.00}s";

        bool stylePanelUp = this.FindControl<Border>("ZoomStylePanel")?.IsVisible == true;
        if (stylePanelUp)
        {
            badge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            badge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            badge.Margin = new Thickness(0, 0, 0, 12);
        }

        badge.IsVisible = true;
    }

    private void HideDragReadout()
    {
        var badge = this.FindControl<Border>("DragReadoutBadge");
        if (badge != null) badge.IsVisible = false;
    }

    /// <summary>
    /// ⚠️ ISSUE_01 — THE ONE GUARDED DOOR FOR EVERY "DELETE THIS BLOCK" THAT IS NOT THE BUTTON.
    ///
    /// There turned out to be THREE ways to delete a speed block, and the confirmation only ever
    /// covered one of them:
    ///   1. the DELETE SEGMENT button        — guarded (its Flyout)
    ///   2. the Delete / Backspace key       — was NOT guarded, called RemoveAt outright
    ///   3. the little red ✕ on each list row — was NOT guarded, called RemoveAt outright
    /// Same irreversible action, three doors, one lock. Doors 2 and 3 now come through here.
    ///
    /// It deliberately does NOT build its own dialog. It selects the block being deleted (so the
    /// prompt names the right one) and then opens the REAL flyout that hangs off DeleteSegmentBtn,
    /// so the wording and the KEEP IT escape are guaranteed identical to the button for ever.
    /// ⚠️ IF THE CONFIRMATION CANNOT BE SHOWN, NOTHING IS DELETED. "Guard unavailable" must never
    ///    resolve to "delete anyway".
    /// </summary>
    private void RequestDeleteSegment(int index)
    {
        if (index < 0 || index >= _segments.Count) return;

        if (_selectedSegmentIndex != index) SelectSegmentAt(index);
        ExecuteDeleteSelectedSegment();
    }

    private void ExecuteDeleteSelectedSegment()
    {
        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count) return;

        var seg = _segments[_selectedSegmentIndex];
        RuntimeLog.Info("UI", $"User deleted a speed segment in the Granular Speed Editor ({FormatMs(seg.StartMs)} to {FormatMs(seg.EndMs)}).");
        PushUndo("delete segment");   // UNDO_01
        _segments.RemoveAt(_selectedSegmentIndex);
        _selectedSegmentIndex = -1;
        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        SetStatus("Selected segment deleted.");
        NotifyUndoable("Segment deleted", "DeleteSegmentBtn");   // ANCHOR_01
    }

    /// <summary>
    /// FREEZE_CLEAR_01 — the ONE teardown for the frozen frame, used by the UNFREEZE toggle and by
    /// CLEAR ALL SEGMENTS.
    ///
    /// <para>
    /// It is a method rather than the inline block it used to be because the teardown is nine
    /// separate pieces of state, not one: the mark itself, the chosen preset, the two focus fields,
    /// the drag mode, the toggle button's icon/label/Danger class, the pulse timer, both hint
    /// labels, the six preset buttons' manually-painted brushes, and the controls that the
    /// "pick a duration" prompt greys out. A second caller that clears only <c>_freezeTimeMs</c>
    /// leaves the button reading UNFREEZE IMAGE over a timeline with no freeze on it, and leaves
    /// MARK START / Play / the speed slider disabled if the prompt was open — which is a dead UI.
    /// </para>
    /// <para>
    /// Every lookup is by control NAME, so this does not need the locals from
    /// <c>WireUpFreezeImage</c> and can be called from anywhere in the window.
    /// </para>
    /// </summary>
    private void ClearFreezeImage(string? feedback)
    {
        _freezeTimeMs = -1;
        _selectedFreezePresetS = -1.0;
        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _freezeDragMode = FreezeDragMode.None;
        _freezeMarkerAnts.Clear();

        var icon = this.FindControl<TextBlock>("FreezeImageToggleIcon");
        var txt = this.FindControl<TextBlock>("FreezeImageToggleText");
        if (icon != null) icon.Text = "\U0001F4F8";
        if (txt != null) txt.Text = " FREEZE IMAGE ";

        var toggle = this.FindControl<Button>("FreezeImageToggle");
        if (toggle != null)
        {
            toggle.Classes.Remove("Danger");
            if (!toggle.Classes.Contains("Primary")) toggle.Classes.Add("Primary");
        }

        if (!string.IsNullOrEmpty(feedback)) ShowFeedback(feedback!);

        _freezePulseTimer?.Stop();
        var hint = this.FindControl<TextBlock>("FreezeHintLabel");
        if (hint != null) hint.IsVisible = false;
        var hintBottom = this.FindControl<TextBlock>("FreezeHintLabelBottom");
        if (hintBottom != null) hintBottom.IsVisible = false;

        foreach (var name in new[] { "FreezePreset05", "FreezePreset10", "FreezePreset15",
                                     "FreezePreset20", "FreezePreset25", "FreezePreset30" })
        {
            var b = this.FindControl<Button>(name);
            if (b == null) continue;
            b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
            b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
            b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
        }

        SetFreezePromptControlsEnabled(true);
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
    }

    /// <summary>
    /// FREEZE_CLEAR_01 — the prompt-time enable/disable set, promoted from a local function inside
    /// <c>WireUpFreezeImage</c> so <see cref="ClearFreezeImage"/> can re-enable what the
    /// "pick a duration" prompt turned off. Nothing about the list changed.
    /// </summary>
    private void SetFreezePromptControlsEnabled(bool enabled)
    {
        var controlsToToggle = new Control?[] {
            this.FindControl<Button>("MarkStartBtn"),
            this.FindControl<Button>("MarkEndBtn"),
            this.FindControl<Button>("GranularPlayPause"),
            this.FindControl<FortniteVideoSoftware.App.Controls.SpinningWheelSlider>("PendingSpeedSlider"),
            this.FindControl<StackPanel>("SpeedPresetsPanel"),
            this.FindControl<Button>("DeleteSegmentBtn"),
            this.FindControl<Button>("ClearAllSegmentsBtn"),
        };
        foreach (var c in controlsToToggle)
        {
            if (c is Avalonia.Input.InputElement input) input.IsEnabled = enabled;
        }
    }

    /// <summary>
    /// FREEZE_CLEAR_01 — CLEAR ALL SEGMENTS now clears the FROZEN FRAME too.
    ///
    /// <para>
    /// ⚠️ THIS IS A DELIBERATE REVERSAL of the earlier behaviour, and the confirmation copy in
    /// <c>UpdateClearAllPromptText</c> was reversed with it — it used to end "Your frozen frame and
    /// your video file are not touched." Do not restore that sentence without also restoring the
    /// carve-out here; a prompt that promises the freeze survives while the code deletes it is
    /// worse than either behaviour on its own.
    /// </para>
    /// <para>
    /// The reason for the reversal: the freeze IS a segment as far as the exporter is concerned —
    /// <c>BuildExportSpeedSegments()</c> synthesises it as <c>SpeedSegment(t, t+d, 0.0)</c> — and it
    /// is the one block on the lane that CHANGES THE LENGTH of the finished video. "Clear all" that
    /// leaves the single length-changing block behind does not put the clip back to normal, which
    /// is the only thing the button claims to do.
    /// </para>
    /// </summary>
    private void ExecuteClearAllSegments()
    {
        bool hadFreeze = _freezeTimeMs >= 0;
        RuntimeLog.Info("UI", $"User cleared ALL {_segments.Count} speed segment(s){(hadFreeze ? " and the frozen frame" : "")} in the Granular Speed Editor.");

        PushUndo("clear all");   // UNDO_01 — the most destructive action here, and the one most
                                 // worth being able to take back.
        _segments.Clear();
        _selectedSegmentIndex = -1;
        _zoomFocus = null;
        _zoomDragSegment = -1;
        _isDraggingZoomMarker = false;
        _pendingStartMs = -1;
        _pendingEndMs = -1;

        if (hadFreeze) ClearFreezeImage(null);

        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        SetStatus(hadFreeze
            ? "All segments, the frozen frame and pending selections cleared."
            : "All segments and pending selections cleared.");
        // UNDOHINT_01 — the most destructive action in the window is also the one that most needs
        // the user to know it is reversible.
        NotifyUndoable(hadFreeze ? "Cleared everything, including the frozen frame" : "Cleared all segments",
            "ClearAllSegmentsBtn");   // ANCHOR_01
    }


    /// <summary>ISSUE_01 — tells the user exactly how much is about to be erased.</summary>
    private void UpdateClearAllPromptText()
    {
        var t = this.FindControl<TextBlock>("ClearAllDetailText");
        if (t == null) return;

        int n = _segments.Count;
        int zooms = 0;
        foreach (var s2 in _segments)
        {
            if (s2.ZoomW.HasValue && s2.ZoomH.HasValue) zooms++;
        }
        bool hasFreeze = _freezeTimeMs >= 0;

        if (n == 0 && !hasFreeze)
        {
            t.Text = "There are no speed blocks or frozen frames to erase. Only a half-finished MARK START / MARK END selection would be reset.";
            return;
        }

        if (n == 0)
        {
            t.Text = $"The frozen frame ({_freezeDurationS:0.00}s held) will be erased and the whole clip goes back to normal speed. A half-finished MARK START / MARK END selection is also reset. Your video file is not touched.";
            return;
        }

        string extras = zooms > 0 ? $", including {zooms} zoom(s)," : "";
        string freeze = hasFreeze ? $" The frozen frame ({_freezeDurationS:0.00}s held) is erased with them." : "";
        t.Text = $"All {n} speed block(s) on the timeline{extras} will be erased and the whole clip goes back to normal speed.{freeze} A half-finished MARK START / MARK END selection is also reset. Your video file is not touched.";
    }

    private bool _syncingZoomChecks;

    /// <summary>true when the user has selected the SLOW (gradual) zoom ramp; false = INSTANT.</summary>
    private bool ZoomSlowSelected => this.FindControl<RadioButton>("SlowZoomCheck")?.IsChecked == true;

    private void OnZoomModeChanged(bool fromSlow)
    {
        if (_syncingZoomChecks) return;
        var slowCb = this.FindControl<RadioButton>("SlowZoomCheck");
        var instCb = this.FindControl<RadioButton>("InstantZoomCheck");
        if (slowCb == null || instCb == null) return;

        bool slow = fromSlow ? (slowCb.IsChecked == true) : (instCb.IsChecked != true);
        _syncingZoomChecks = true;
        slowCb.IsChecked = slow;
        instCb.IsChecked = !slow;
        _syncingZoomChecks = false;

        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
        {
            var seg = _segments[_selectedSegmentIndex];
            if (seg.ZoomW.HasValue && seg.ZoomSlow != slow)
            {
                // ═════════════════════════════════════════════════════════════════════
                // ZOOMLIVE_05 — INSTANT -> SLOW IS NOT ALWAYS LEGAL, AND IT NEVER WAS.
                //
                // A gliding zoom needs clear air around it: two slow zooms closer together than
                // ZoomRampRequiredGapBetweenSlowZooms cannot both complete their ramps, so the
                // magnifier drag has always been clamped against neighbouring SLOW zooms. Flipping
                // the radio was exempt from that check only because the mode could not be changed
                // after the ✅ — now that it can, the same rule has to apply here or the user can
                // reach an arrangement the drag would have refused to create.
                //
                // It REFUSES rather than silently sliding the block: the user asked for a ramp
                // mode, not for their zoom to move somewhere else.
                // ═════════════════════════════════════════════════════════════════════
                if (slow && !SlowZoomHasRoom(_selectedSegmentIndex, out double needSec))
                {
                    _syncingZoomChecks = true;
                    slowCb.IsChecked = false;
                    instCb.IsChecked = true;
                    _syncingZoomChecks = false;
                    NotifyError($"This zoom is too close to another gliding zoom — they need {needSec:0.0}s between them, " +
                                "or neither can glide. Move one of them apart first, or leave this one instant.");
                    RuntimeLog.Info("Granular",
                        $"Refused INSTANT→SLOW on segment #{_selectedSegmentIndex + 1}: less than {needSec:0.###}s clear of another slow zoom.");
                    return;
                }

                PushUndo("change zoom style");   // UNDO_02
                _segments[_selectedSegmentIndex] = seg with { ZoomSlow = slow };
                RuntimeLog.Info("Granular", $"Zoom ramp mode → {(slow ? "SLOW" : "INSTANT")} on segment #{_selectedSegmentIndex + 1}.");
                RefreshSegmentList();
                RedrawTimeline();
            }
        }
    }

    /// <summary>Reflect the ramp mode into the checkboxes: an existing zoom keeps its own mode;
    /// a segment with no zoom yet (or no selection) shows the global Settings default.</summary>
    private void SyncZoomModeChecksFromSegment()
    {
        bool slow;
        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count
            && _segments[_selectedSegmentIndex].ZoomW.HasValue)
            slow = _segments[_selectedSegmentIndex].ZoomSlow;
        else
            slow = Infrastructure.SettingsManager.Instance.Defaults.DefaultZoomSlow;
        var slowCb = this.FindControl<RadioButton>("SlowZoomCheck");
        var instCb = this.FindControl<RadioButton>("InstantZoomCheck");
        _syncingZoomChecks = true;
        if (slowCb != null) slowCb.IsChecked = slow;
        if (instCb != null) instCb.IsChecked = !slow;
        _syncingZoomChecks = false;
    }

    /// <summary>The video's actual letterboxed rect inside the overlay canvas (aspect-fit).</summary>
    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// ⚠️ ZOOM_09 — IS THE VIDEO RECT REAL, OR IS IT THE NOT-LAID-OUT FALLBACK?
    ///
    /// <see cref="GetVideoDisplayRect"/> returns `Rect(0,0,max(1,cw),max(1,ch))` when the canvas
    /// has not been through a layout pass yet. That fallback is a SQUARE 1x1, and a square is a
    /// perfectly valid-looking rectangle — so every coordinate conversion downstream keeps working
    /// and quietly produces garbage.
    ///
    /// THIS IS NOT HYPOTHETICAL. It is what a real session logged:
    ///     Zoom Placed on segment #1 ... W=284 H=240 src=854x480 mobile=True
    /// The auto-placed box should have been 160x240 (a 2:3 rectangle). 284x240 is 1.18:1 — not 2:3
    /// at all. The arithmetic reproduces exactly from a 1x1 vid: the box works out as 1/3 x 1/2 of
    /// a unit square, and committing multiplies those by the source size, giving 854/3 = 284 and
    /// 480/2 = 240. The zoom was written against a video rectangle that did not exist yet.
    ///
    /// ANY code that converts between canvas pixels and source pixels MUST check this first.
    /// The threshold is 4, not 1: a 1- or 2-pixel canvas is layout noise, never a real preview.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private static bool IsVideoRectUsable(Avalonia.Rect vid)
        => vid.Width >= 4 && vid.Height >= 4;

    private Avalonia.Rect GetVideoDisplayRect(Avalonia.Controls.Canvas canvas)
    {
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        double srcAspect = sh > 0 ? (double)sw / sh : 16.0 / 9.0;
        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        if (cw <= 1 || ch <= 1) return new Avalonia.Rect(0, 0, Math.Max(1, cw), Math.Max(1, ch));
        double vidW, vidH;
        if (cw / ch > srcAspect) { vidH = ch; vidW = ch * srcAspect; }
        else { vidW = cw; vidH = cw / srcAspect; }
        return new Avalonia.Rect((cw - vidW) / 2.0, (ch - vidH) / 2.0, vidW, vidH);
    }

    /// <summary>
    /// ZOOMLIVE_01 — ZOOM-IN is now a TOGGLE, not a one-way door.
    ///
    /// It used to refuse and scold ("press the ✅…") because a zoom session was a transaction that
    /// had to be closed. There is no transaction any more: the box writes itself to the segment as
    /// it is dragged, so pressing the button again simply puts the box away, with the work kept.
    /// </summary>
    private void ToggleZoomMode()
    {
        if (_zoomModeActive) { ExitZoomMode(); return; }

        if (!EnsureZoomTargetSegment()) return;

        EnterZoomMode();
    }

    private bool _zoomSessionCreatedSegment;

    /// <summary>
    /// ZOOMLIVE_07 — WHICH block ZOOM-IN auto-created, by index.
    ///
    /// ⚠️ ExitZoomMode must NOT read `_selectedSegmentIndex` to find it. Selecting a different
    /// block calls ExitZoomMode as part of switching, and by then `_selectedSegmentIndex` is
    /// already the NEW block — so cleaning up "the selected one" would delete the block the user
    /// just clicked on instead of the empty one they abandoned.
    /// </summary>
    private int _zoomCreatedSegmentIndex = -1;

    /// <summary>ZOOMLIVE_01 — the tactile half of a committed box: a sound.</summary>
    private void PulseZoomConfirmFeedback()
    {
        try
        {
            UiSoundEffect.PlayMark();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    // ZOOMLIVE_01 — CancelZoomMode IS GONE, AND ITS ABSENCE IS THE POINT.
    //
    // It stripped the zoom off the block (or deleted the block outright) because Escape used to
    // mean "abandon this transaction". With the box committing itself as it is dragged there is no
    // transaction to abandon, and a key that silently deletes finished work is exactly the
    // behaviour this whole change set exists to remove. The three survivors do the job honestly:
    //   ExitZoomMode                 — put the box away, keep the work
    //   RemoveZoomFromSelectedSegment — delete the zoom, keep the speed block
    //   Delete / ✕ / right-click      — delete the whole block
    // DO NOT REINSTATE A KEY THAT DESTROYS A ZOOM WITHOUT ASKING.

    /// <summary>
    /// ZOOM_04 — takes the zoom off the selected block, leaving the block itself alone.
    /// This is the ONLY way to change an existing zoom: ZOOM-IN refuses to reopen a block that
    /// already has one (see EnsureZoomTargetSegment), so editing is remove-then-redo by design.
    /// </summary>
    private void RemoveZoomFromSelectedSegment()
    {
        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count) return;
        var seg = _segments[_selectedSegmentIndex];
        if (!seg.ZoomW.HasValue) { NotifyError("That block has no zoom to remove."); return; }

        PushUndo("apply zoom");   // UNDO_02
        _segments[_selectedSegmentIndex] = seg with
        {
            ZoomX = null, ZoomY = null, ZoomW = null, ZoomH = null,
            ZoomOrigRes = null, ZoomStartMs = null, ZoomEndMs = null
        };
        ClearLiveZoomCrop();
        RuntimeLog.Info("Granular", $"Zoom removed from segment #{_selectedSegmentIndex + 1}.");
        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        Notify("Zoom removed. Press ZOOM-IN to set a new one.");
    }

    /// <summary>
    /// IDEA_3 — "let a zoom exist without making a speed block first".
    ///
    /// THE PROBLEM: a zoom is stored ON a <see cref="SpeedSegment"/> (ZoomX/Y/W/H live there and
    /// are threaded through ChunkSpec into the FFmpeg graph), so the data model genuinely needs a
    /// segment to hang the zoom on. The UI used to expose that internal requirement directly: the
    /// ZOOM-IN button was HIDDEN until a block was selected, so users had to work out on their own
    /// that "make a speed block you don't want, then zoom it". Nobody guesses that.
    ///
    /// THE FIX: keep the data model exactly as it is, and make the UI create the container itself.
    /// The user asks for a zoom; the app quietly provides something for it to live on.
    ///
    /// Order of preference — least surprising first:
    ///   1. A block is already selected → use it (unchanged behaviour).
    ///   2. The playhead is sitting inside an existing block → select that one. This is what the
    ///      user means when they scrub to a moment and press ZOOM-IN.
    ///   3. Nothing there → create a block at the BASE speed (i.e. no speed change at all, so the
    ///      zoom is the only visible effect) starting at the playhead, and say so in the status bar
    ///      so the new block on the timeline is never a mystery.
    ///
    /// Returns false only when a block genuinely cannot be placed, with the reason in the status
    /// bar. It NEVER silently does nothing — that was the old failure mode.
    /// </summary>
    /// <summary>
    /// ZOOM_05 — turns a MARK START / MARK END span into the block the zoom lives on.
    ///
    /// Enforces exactly the same three rules the manual "add segment" path does, because a block
    /// created here is an ordinary block in every other respect: it must be at least
    /// <see cref="SegMinWidthMs"/> long, it must not overlap an existing block, and it must keep
    /// <see cref="SegGapMs"/> clear of its neighbours. Each failure explains itself in plain words
    /// rather than silently producing something odd.
    /// </summary>
    private bool CreateZoomBlockFromPendingMarks()
    {
        double start = Math.Min(_pendingStartMs, _pendingEndMs);
        double end   = Math.Max(_pendingStartMs, _pendingEndMs);

        bool snapped = false;
        string snapMsg = "";

        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            if (start < seg.EndMs && end > seg.StartMs)
            {
                NotifyError($"Cannot create zoom: Overlaps existing block #{i + 1} [{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}].");
                return false;
            }
            bool tooCloseBefore = seg.EndMs <= start && start - seg.EndMs < SegGapMs;
            bool tooCloseAfter  = seg.StartMs >= end && seg.StartMs - end < SegGapMs;
            
            if (tooCloseBefore)
            {
                start = seg.EndMs + SegGapMs;
                snapped = true;
                snapMsg = $"Pushed the start to {FormatMs(start)} to keep a safe {SegGapMs/1000.0}s distance from the previous segment.";
            }
            if (tooCloseAfter)
            {
                end = seg.StartMs - SegGapMs;
                snapped = true;
                if (tooCloseBefore) snapMsg = $"Squeezed the segment to fit exactly between the two neighbours ({FormatMs(start)} – {FormatMs(end)}).";
                else snapMsg = $"Pushed the end to {FormatMs(end)} to keep a safe {SegGapMs/1000.0}s distance from the next segment.";
            }
        }

        if (end - start < SegMinWidthMs)
        {
            NotifyError($"Not enough room! After keeping the required {SegGapMs/1000.0}s gap, the zoom segment would be too small to create.");
            return false;
        }

        var created = new SpeedSegment(start, end, _baseSpeed);
        PushUndo("apply zoom");   // UNDO_02
        _segments.Add(created);
        _segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        int newIndex = _segments.FindIndex(x => ReferenceEquals(x, created));
        SelectSegmentAt(newIndex < 0 ? _segments.Count - 1 : newIndex);
        
        if (snapped)
        {
            NotifyError(snapMsg);
        }
        _zoomSessionCreatedSegment = true;
        _zoomCreatedSegmentIndex = _selectedSegmentIndex;   // ZOOMLIVE_07

        _pendingStartMs = -1;
        _pendingEndMs = -1;

        RefreshSegmentList();
        RedrawTimeline();
        RuntimeLog.Info("Granular", $"ZOOM-IN created a block from the marked span {FormatMs(start)} – {FormatMs(end)} at base speed {_baseSpeed:0.0}x.");
        SetStatus($"Zoom will cover your marked span, {FormatClock(start)} to {FormatClock(end)}.");
        return true;
    }

    private bool EnsureZoomTargetSegment()
    {
        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
        {
            if (_segments[_selectedSegmentIndex].ZoomW.HasValue)
            {
                NotifyError($"Block #{_selectedSegmentIndex + 1} already has a zoom. Press REMOVE ZOOM to clear it first, or drag its markers on the timeline to change WHEN it happens.");
                return false;
            }
            return true;
        }

        if (_pendingStartMs >= 0 && _pendingEndMs >= 0)
        {
            return CreateZoomBlockFromPendingMarks();
        }

        if (_pendingStartMs >= 0 && _pendingEndMs < 0)
        {
            NotifyError("Mark an END first — press MARK END where the zoom should stop, then press ZOOM-IN.");
            return false;
        }

        double dur = GetDuration();
        if (dur <= 0)
        {
            NotifyError("Load a video first.");
            return false;
        }

        int playheadMs = (int)Math.Round(GetCurrentTime() * 1000.0);
        int timelineEndMs = (int)Math.Round(dur * 1000.0);

        for (int i = 0; i < _segments.Count; i++)
        {
            if (playheadMs >= _segments[i].StartMs && playheadMs <= _segments[i].EndMs)
            {
                SelectSegmentAt(i);
                SetStatus("Zoom will be added to the block under the playhead.");
                return true;
            }
        }

        double start = playheadMs;
        double end = Math.Min(timelineEndMs, start + DefaultZoomBlockMs);

        foreach (var seg in _segments)
        {
            if (seg.StartMs >= start) end = Math.Min(end, seg.StartMs - SegGapMs);
            if (seg.EndMs <= start) start = Math.Max(start, seg.EndMs + SegGapMs);
        }
        end = Math.Min(end, timelineEndMs);

        if (end - start < SegMinWidthMs)
        {
            NotifyError($"Not enough free space here for a zoom — move the playhead away from the nearby block (a {SegGapMs}ms gap is required) and try again.");
            return false;
        }

        var created = new SpeedSegment(start, end, _baseSpeed);
        PushUndo("apply zoom");   // UNDO_02
        _segments.Add(created);
        _segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        int newIndex = _segments.FindIndex(s => ReferenceEquals(s, created));
        SelectSegmentAt(newIndex < 0 ? _segments.Count - 1 : newIndex);
        _zoomSessionCreatedSegment = true;
        _zoomCreatedSegmentIndex = _selectedSegmentIndex;   // ZOOMLIVE_07

        RuntimeLog.Info("Granular",
            $"Zoom container auto-created at base speed {_baseSpeed:0.0}x: {FormatMs(start)}–{FormatMs(end)}.");
        SetStatus($"Added a {(end - start) / 1000.0:0.0}s block at normal speed to hold the zoom — drag its edges to change when the zoom happens.");
        return true;
    }

    /// <summary>
    /// IDEA_3 helper — selects a block and brings the rest of the UI in line with it, without the
    /// status-bar text the list-row click path writes.
    /// </summary>
    /// <summary>
    /// ZOOMLIVE_02 — kept as a name several call sites already use; it is now one line.
    /// The two implementations had drifted apart (this one never synced the ramp radios and never
    /// moved the playhead), which is precisely the class of bug a second selection path invites.
    /// </summary>
    private void SelectSegmentAt(int index) => SelectSegment(index, jumpPlayhead: true);

    /// <summary>
    /// APPLY ZOOM-IN commit path. On the GPU preview path, stall the UI behind a
    /// blocking "Applying changes..." overlay while the simulated crop is primed in
    /// mpv (buffering round-trip), per project_structure.txt Section 8 item (b).
    /// On CPU-only machines this is a plain ExitZoomMode (no overlay, no simulation).
    /// </summary>
    private async System.Threading.Tasks.Task CommitZoomAndPrimePreviewAsync()
    {
        // ZOOMLIVE_01 — ⚠️ THIS USED TO CALL ExitZoomMode() AND MUST NOT.
        // It ran exactly once, from the ✅, so closing the box was the right ending. It now runs on
        // EVERY drag release, so closing here would slam the box shut the instant the user let go
        // of it — they could never make a second adjustment. Leaving zoom mode is now only ever a
        // deliberate act: the ZOOM-IN toggle, Escape, or selecting a different block.
        if (!_gpuLiveZoomPreview) return;

        var busy = this.FindControl<Border>("ZoomApplyBusyOverlay");
        if (busy != null) busy.IsVisible = true;
        try
        {
            UpdateLiveZoomCrop();

            string want = _lastLiveCrop;
            for (int i = 0; i < 20 && want.Length > 0; i++)
            {
                var applied = _videoHost?.IpcClient?.GetPropertyString("video-crop");
                if (!string.IsNullOrEmpty(applied) && applied != "no") break;
                await System.Threading.Tasks.Task.Delay(50);
            }
            await System.Threading.Tasks.Task.Delay(150);
            RuntimeLog.Info("Granular", "APPLY ZOOM-IN: GPU live zoom preview primed.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"GPU live zoom prime failed (falling back to box overlay): {ex.Message}");
        }
        finally
        {
            if (busy != null) busy.IsVisible = false;
        }
    }

    /// <summary>Removes any simulated live crop from the mpv preview.</summary>
    private void ClearLiveZoomCrop()
    {
        if (!_gpuLiveZoomPreview || _lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = _videoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    /// <summary>
    /// GPU live zoom preview engine. Mirrors GranularSpeedBuilder's export math 1:1:
    /// steal windows (>=0.5s available, capped at 1.0s, vs neighboring zooms only),
    /// linear ramp progress, zVal = 1 + (targetZ - 1) * p with the view center lerping
    /// from frame center to the zoom-box center. The equivalent visible source region
    /// is applied via mpv `video-crop` ("WxH+X+Y"); mpv's own aspect-fit letterboxing
    /// then matches the export's force_original_aspect_ratio=decrease + pad stage.
    /// NOTE: near frame edges the export shows black padding where this preview clamps
    /// the crop inside the frame — an accepted, minor visual difference.
    /// </summary>
    private void UpdateLiveZoomCrop()
    {
        if (!_gpuLiveZoomPreview || _videoHost?.IpcClient == null) return;
        if (_zoomModeActive) { ClearLiveZoomCrop(); return; }

        double tSec = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _trimStartMs) / 1000.0;
        double durSec = Math.Max(0.1, ((_trimEndMs > 0 ? _trimEndMs : _videoHost.IpcClient.Duration * 1000.0) - _trimStartMs) / 1000.0);

        var (psw, psh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        var result = FortniteVideoSoftware.Core.Media.ZoomPreviewSimulator.Compute(
            _segments, tSec, durSec, _isMobileFormat, psw, psh);

        if (!result.HasCrop) { ClearLiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = _videoHost.IpcClient.SetPropertyAsync("video-crop", result.Crop);
    }

    private void EnterZoomMode()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        if (canvas == null) return;

        ClearLiveZoomCrop();

        _zoomModeActive = true;
        UpdateDetachButtonForZoomMode();
        EnsureZoomVisuals(canvas);
        canvas.IsVisible = true;
        canvas.IsHitTestVisible = true;

        var stylePanel = this.FindControl<Border>("ZoomStylePanel");
        if (stylePanel != null) stylePanel.IsVisible = true;


        var seg = _segments[_selectedSegmentIndex];
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (seg.ZoomW.HasValue && seg.ZoomH.HasValue && seg.ZoomX.HasValue && seg.ZoomY.HasValue && sw > 0 && sh > 0)
        {
            double sx = vid.Width / sw, sy = vid.Height / sh;
            _zoomUiRect = new Avalonia.Rect(vid.X + seg.ZoomX.Value * sx, vid.Y + seg.ZoomY.Value * sy,
                                            seg.ZoomW.Value * sx, seg.ZoomH.Value * sy);
            _hasZoomBox = true;
            _zoomBoxTouched = true;   // ZOOMLIVE_01 — a stored zoom is real by definition
        }
        else
        {
            PlaceDefaultZoomBoxWhenLaidOut(canvas);
        }

        SyncZoomModeChecksFromSegment();      // ZOOMLIVE_01 — Slow/Instant reflects THIS segment
        RenderZoomBox();
        MaybeShowZoomTutorial(canvas);
        SetStatus(_zoomBoxTouched
            ? "Editing this zoom. Drag the box to re-aim it, or its corners to resize. Every change is saved as you go."
            : "Drag the box to aim it, or its corners to resize. The zoom starts the moment you touch it.");
    }

    private void ExitZoomMode()
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        var zoomBtn = this.FindControl<Button>("ZoomSegmentBtn");
        _zoomModeActive = false;
        UpdateDetachButtonForZoomMode();
        _zoomDrag = ZoomDrag.None;
        if (canvas != null) canvas.IsVisible = false;

        var stylePanel = this.FindControl<Border>("ZoomStylePanel");
        if (stylePanel != null) stylePanel.IsVisible = false;

        HideZoomTutorial();
        if (zoomBtn != null)
        {
            zoomBtn.Content = "🔍 ZOOM-IN";
            zoomBtn.Classes.Remove("Success");
            if (!zoomBtn.Classes.Contains("ZoomAction")) zoomBtn.Classes.Add("ZoomAction");
        }

        if (_zoomFactorBadge != null) _zoomFactorBadge.IsVisible = false;

        // ZOOMLIVE_01 — LEAVING WITHOUT EVER TOUCHING THE BOX.
        // ZOOM-IN creates a 1x block to hang the zoom on when nothing is selected. If the user
        // never touched the box there is no zoom, so that block is an invisible artefact of a
        // button press — it has no speed change and no zoom, and it would sit on the timeline
        // forever. Nothing was ever committed, so nothing is lost by removing it.
        int orphan = _zoomCreatedSegmentIndex;
        if (_zoomSessionCreatedSegment && !_zoomBoxTouched
            && orphan >= 0 && orphan < _segments.Count
            && !_segments[orphan].ZoomW.HasValue)
        {
            RuntimeLog.Info("Granular",
                $"Zoom cancelled before it was aimed — removing the empty block it would have used (#{orphan + 1}).");
            _segments.RemoveAt(orphan);

            // ZOOMLIVE_07 — removing an earlier element shifts every index after it. The selection
            // may already point at a DIFFERENT block (this runs as part of switching selection), so
            // it is repaired rather than blanked.
            if (_selectedSegmentIndex == orphan) _selectedSegmentIndex = -1;
            else if (_selectedSegmentIndex > orphan) _selectedSegmentIndex--;

            RefreshSegmentList();
            RedrawTimeline();
            UpdateDeleteButtonVisibility();
        }

        _hasZoomBox = false;
        _zoomBoxTouched = false;
        _zoomSessionCreatedSegment = false;
        _zoomCreatedSegmentIndex = -1;
    }

    private void EnsureZoomVisuals(Avalonia.Controls.Canvas canvas)
    {
        if (_zoomBoxRect != null) return;
        var dimBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000"));
        for (int i = 0; i < 4; i++)
        {
            _zoomDim[i] = new Avalonia.Controls.Shapes.Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            canvas.Children.Add(_zoomDim[i]);
        }
        _zoomBoxRect = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = ZoomBrush(),
            StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            Fill = Avalonia.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        canvas.Children.Add(_zoomBoxRect);
        for (int i = 0; i < 4; i++)
        {
            _zoomHandles[i] = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = ZoomHandleVisualPx, Height = ZoomHandleVisualPx,
                Fill = ZoomBrush(),
                Stroke = Avalonia.Media.Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false
            };
            canvas.Children.Add(_zoomHandles[i]);
        }

        for (int i = 0; i < 2; i++)
        {
            var shade = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#99000000")),
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = -1
            };
            _portraitShade[i] = shade;
            canvas.Children.Add(shade);
        }

        for (int i = 0; i < 2; i++)
        {
            var edge = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 2,
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38bdf8")),
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = 5
            };
            _portraitEdges[i] = edge;
            canvas.Children.Add(edge);
        }

        // ZOOMLIVE_01 — THE FLOATING ✅ IS GONE. Do not put it back.
        // It was the only way to commit a zoom, and it was ceremony: PointerReleased already wrote
        // the box into the segment on every draw/move/resize, so pressing it re-committed values
        // that were already stored. What it really did was make the feature feel one-shot — a zoom
        // could not be re-opened afterwards, because the button implied a transaction that had
        // closed. Registration is now the gesture itself (see _zoomBoxTouched).

        _zoomFactorText = new TextBlock
        {
            Foreground = Infrastructure.ThemeResources.Brush(this, "AppOnAccentTextBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ffffff"))),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(14),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        _zoomFactorBadge = new Border
        {
            Background = ZoomBrush(),
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Thickness(9, 3),
            IsHitTestVisible = false,
            IsVisible = false,
            ZIndex = 20,
            Child = _zoomFactorText
        };
        canvas.Children.Add(_zoomFactorBadge);
    }

    // ZOOMLIVE_01 — ZoomConfirmSizePx, ZoomConfirmBlinkDuration and the blink timer were all
    // scaffolding for the floating ✅ and went with it. The pulse existed to tell the user their
    // box had not been saved yet; there is no longer such a state to warn about.

    /// <summary>
    /// ZOOMLIVE_01 — HAS THIS BOX BEEN TOUCHED YET?
    ///
    /// <para>
    /// Pressing ZOOM-IN drops a default box in the middle of the picture. That box is a SUGGESTION,
    /// not a zoom: until the user drags or resizes it, nothing is written to the segment and the
    /// box is drawn faint. Otherwise pressing ZOOM-IN and walking away would silently zoom the
    /// video into its own middle — a zoom the user never aimed and never saw the point of.
    /// </para>
    /// <para>
    /// ⚠️ It also decides what LEAVING zoom mode means. If ZOOM-IN auto-created a speed block for
    /// this zoom and the box was never touched, that block is removed on exit; leaving it behind
    /// accumulates invisible 1x blocks the user never asked for and cannot see.
    /// </para>
    /// </summary>
    private bool _zoomBoxTouched;
    private Border? _zoomFactorBadge;
    private TextBlock? _zoomFactorText;

    /// <summary>IDEA_6 — left/right markers for the surviving portrait slice. Null until
    /// <see cref="EnsureZoomVisuals"/> runs; only ever visible when _isMobileFormat is true.</summary>
    private readonly Avalonia.Controls.Shapes.Rectangle?[] _portraitEdges = new Avalonia.Controls.Shapes.Rectangle?[2];

    /// <summary>IDEA_6 — shading over the two columns portrait mode discards.</summary>
    private readonly Avalonia.Controls.Shapes.Rectangle?[] _portraitShade = new Avalonia.Controls.Shapes.Rectangle?[2];

    /// <summary>
    /// IDEA_6 — positions the portrait boundary markers. Uses the SAME expression as the clamp in
    /// ZoomCanvas_PointerMoved (vid.Height * 2/3, centred) so the line the user sees is exactly the
    /// wall the drag hits. If those two ever disagree, the guide is lying — keep them together.
    /// </summary>
    private void RenderPortraitBoundary(Avalonia.Controls.Canvas canvas)
    {
        if (_portraitEdges[0] == null || _portraitEdges[1] == null
            || _portraitShade[0] == null || _portraitShade[1] == null) return;

        void HideAll()
        {
            _portraitEdges[0]!.IsVisible = false;
            _portraitEdges[1]!.IsVisible = false;
            _portraitShade[0]!.IsVisible = false;
            _portraitShade[1]!.IsVisible = false;
        }

        if (!_isMobileFormat) { HideAll(); return; }

        var vid = GetVideoDisplayRect(canvas);
        if (vid.Width <= 0 || vid.Height <= 0) { HideAll(); return; }

        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        double portraitW = vid.Height * (2.0 / 3.0);
        double left = vid.X + (vid.Width - portraitW) / 2.0;
        double right = left + portraitW;

        void Shade(Avalonia.Controls.Shapes.Rectangle r, double x, double w)
        {
            r.IsVisible = true;
            r.Width = Math.Max(0, w);
            r.Height = Math.Max(0, ch);
            Avalonia.Controls.Canvas.SetLeft(r, Math.Max(0, x));
            Avalonia.Controls.Canvas.SetTop(r, 0);
        }

        Shade(_portraitShade[0]!, 0, left);
        Shade(_portraitShade[1]!, right, cw - right);

        void Place(Avalonia.Controls.Shapes.Rectangle edge, double x)
        {
            edge.IsVisible = true;
            edge.Height = vid.Height;
            Avalonia.Controls.Canvas.SetLeft(edge, x - 1);
            Avalonia.Controls.Canvas.SetTop(edge, vid.Y);
        }

        Place(_portraitEdges[0]!, left);
        Place(_portraitEdges[1]!, right);
    }

    private bool _isZoomRenderPending = false;
    /// <summary>
    /// ZOOM_09 — places the auto box once the canvas genuinely has a size, then commits it so a box
    /// the user never touches still exports. Retries once at Background priority for the case where
    /// even the Loaded pass has not produced a rect (a detached preview mid-reattach, for example).
    /// </summary>
    private void PlaceDefaultZoomBoxWhenLaidOut(Avalonia.Controls.Canvas canvas, bool isRetry = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_zoomModeActive) return;
            if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count) return;
            if (_segments[_selectedSegmentIndex].ZoomW.HasValue) return;

            if (!IsVideoRectUsable(GetVideoDisplayRect(canvas)))
            {
                if (!isRetry) PlaceDefaultZoomBoxWhenLaidOut(canvas, isRetry: true);
                else RuntimeLog.Fail("Granular", "Could not place the default zoom box — the preview never reported a size.");
                return;
            }

            // ZOOMLIVE_01 — ⚠️ THIS USED TO CALL CommitZoomToSegment("Placed") AND MUST NOT.
            // Committing here means merely PRESSING ZOOM-IN zooms the video into its own middle,
            // with no aiming and no consent. The box is placed and drawn faint; the first drag or
            // resize is what writes it to the segment.
            _zoomUiRect = BuildDefaultZoomRect(canvas);
            _hasZoomBox = true;
            _zoomBoxTouched = false;
            RenderZoomBox();
        }, isRetry ? Avalonia.Threading.DispatcherPriority.Background
                   : Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// ZOOM_02 — the box that appears the instant ZOOM-IN is pressed: DefaultZoomFactor tight,
    /// centred in the usable area.
    /// ⚠️ CENTRED ON <see cref="ZoomBoundsUi"/>, NOT ON THE VIDEO. In portrait the usable area is
    /// the 2:3 centre strip; centring on the full frame would look right and be wrong, because the
    /// strip is narrower than the picture and the box would straddle the discarded columns.
    /// The height guard matters at extreme aspect ratios: a 2x-wide box in portrait is 1.5x as tall
    /// as it is wide, so on a very short source the width has to give way to keep it inside.
    /// </summary>
    private Avalonia.Rect BuildDefaultZoomRect(Avalonia.Controls.Canvas canvas)
    {
        var bounds = ZoomBoundsUi(canvas);
        var vid = GetVideoDisplayRect(canvas);
        double aspect = ZoomAspect;

        double w = bounds.Width / DefaultZoomFactor;
        double h = w / aspect;
        if (h > bounds.Height) { h = bounds.Height; w = h * aspect; }

        double minW = MinZoomWidthUi(vid.Width / Math.Max(1, GetSourceW()));
        if (w < minW) { w = Math.Min(minW, bounds.Width); h = w / aspect; }

        return new Avalonia.Rect(
            bounds.X + (bounds.Width - w) / 2.0,
            bounds.Y + (bounds.Height - h) / 2.0,
            w, h);
    }

    // ZOOMLIVE_01 — BestZoomCornerIndex parked the floating ✅ in the most open corner. Removed
    // with the button it served.

    private void RenderZoomBox()
    {
        if (_isZoomRenderPending) return;
        _isZoomRenderPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isZoomRenderPending = false;
            var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
            if (canvas == null || _zoomBoxRect == null) return;
            double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;

            RenderPortraitBoundary(canvas);

            if (!_hasZoomBox)
            {
                _zoomBoxRect.IsVisible = false;
                foreach (var h in _zoomHandles) h.IsVisible = false;
                for (int i = 0; i < 4; i++) if (_zoomDim[i] != null) { _zoomDim[i].Width = 0; _zoomDim[i].Height = 0; }
                if (_zoomFactorBadge != null) _zoomFactorBadge.IsVisible = false;
                return;
            }

            var r = _zoomUiRect;
            _zoomBoxRect.IsVisible = true;
            _zoomBoxRect.Width = r.Width; _zoomBoxRect.Height = r.Height;
            Avalonia.Controls.Canvas.SetLeft(_zoomBoxRect, r.X);
            Avalonia.Controls.Canvas.SetTop(_zoomBoxRect, r.Y);

            void Dim(int i, double x, double y, double w, double h)
            {
                var d = _zoomDim[i]; if (d == null) return;
                d.Width = Math.Max(0, w); d.Height = Math.Max(0, h);
                Avalonia.Controls.Canvas.SetLeft(d, x); Avalonia.Controls.Canvas.SetTop(d, y);
            }
            Dim(0, 0, 0, cw, r.Y);
            Dim(1, 0, r.Bottom, cw, ch - r.Bottom);
            Dim(2, 0, r.Y, r.X, r.Height);
            Dim(3, r.Right, r.Y, cw - r.Right, r.Height);

            var corners = new[] { new Avalonia.Point(r.X, r.Y), new Avalonia.Point(r.Right, r.Y),
                                  new Avalonia.Point(r.X, r.Bottom), new Avalonia.Point(r.Right, r.Bottom) };
            for (int i = 0; i < 4; i++)
            {
                _zoomHandles[i].IsVisible = true;
                Avalonia.Controls.Canvas.SetLeft(_zoomHandles[i], corners[i].X - ZoomHandleVisualPx / 2);
                Avalonia.Controls.Canvas.SetTop(_zoomHandles[i], corners[i].Y - ZoomHandleVisualPx / 2);
            }

            var vidRect = GetVideoDisplayRect(canvas);

            // ZOOMLIVE_01 — an UNTOUCHED default box is a suggestion, so it is drawn faint. The
            // first drag or resize both commits it and makes it solid, which is the only feedback
            // the user needs about the difference between "proposed" and "live".
            _zoomBoxRect.Opacity = _zoomBoxTouched ? 1.0 : 0.45;
            foreach (var h in _zoomHandles) h.Opacity = _zoomBoxTouched ? 1.0 : 0.45;

            if (_zoomFactorBadge != null && _zoomFactorText != null)
            {
                double factor = ZoomFactorOf(r, ZoomBoundsUi(canvas));
                _zoomFactorText.Text = $"{factor:0.0}x";
                _zoomFactorBadge.IsVisible = true;
                _zoomFactorBadge.Measure(new Avalonia.Size(cw, ch));
                double bw = _zoomFactorBadge.DesiredSize.Width, bh = _zoomFactorBadge.DesiredSize.Height;
                double fx = Math.Clamp(r.X + (r.Width - bw) / 2, 2, Math.Max(2, cw - bw - 2));
                double fy = r.Y - bh - 8;
                if (fy < 2) fy = Math.Min(r.Bottom + 8, Math.Max(2, ch - bh - 2));
                Avalonia.Controls.Canvas.SetLeft(_zoomFactorBadge, fx);
                Avalonia.Controls.Canvas.SetTop(_zoomFactorBadge, fy);
            }
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void ZoomCanvas_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!_zoomModeActive || sender is not Avalonia.Controls.Canvas canvas) return;
        if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        HideZoomTutorial();

        var p = e.GetPosition(canvas);
        _zoomDragStart = p;
        _zoomStartRect = _zoomUiRect;

        if (_hasZoomBox)
        {
            var r = _zoomUiRect;
            double hz = ZoomHandlePx;
            bool NearCorner(Avalonia.Point c) => Math.Abs(p.X - c.X) <= hz && Math.Abs(p.Y - c.Y) <= hz;
            if (NearCorner(new Avalonia.Point(r.X, r.Y))) _zoomDrag = ZoomDrag.ResizeTL;
            else if (NearCorner(new Avalonia.Point(r.Right, r.Y))) _zoomDrag = ZoomDrag.ResizeTR;
            else if (NearCorner(new Avalonia.Point(r.X, r.Bottom))) _zoomDrag = ZoomDrag.ResizeBL;
            else if (NearCorner(new Avalonia.Point(r.Right, r.Bottom))) _zoomDrag = ZoomDrag.ResizeBR;
            else if (r.Contains(p)) { _zoomDrag = ZoomDrag.Move; canvas.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand); }
            else _zoomDrag = ZoomDrag.Draw;
        }
        else _zoomDrag = ZoomDrag.Draw;

        e.Pointer.Capture(canvas);
        e.Handled = true;
    }

    /// <summary>§7B/§7C: cursor to show while hovering (not dragging) the zoom rubber-band.</summary>
    private Avalonia.Input.StandardCursorType ZoomHoverCursor(Avalonia.Point p)
    {
        if (_hasZoomBox)
        {
            var r = _zoomUiRect;
            double hz = ZoomHandlePx;
            bool Near(Avalonia.Point c) => Math.Abs(p.X - c.X) <= hz && Math.Abs(p.Y - c.Y) <= hz;
            if (Near(new Avalonia.Point(r.X, r.Y)) || Near(new Avalonia.Point(r.Right, r.Bottom)))
                return Avalonia.Input.StandardCursorType.TopLeftCorner;
            if (Near(new Avalonia.Point(r.Right, r.Y)) || Near(new Avalonia.Point(r.X, r.Bottom)))
                return Avalonia.Input.StandardCursorType.TopRightCorner;
            if (r.Contains(p)) return Avalonia.Input.StandardCursorType.Hand;
        }
        return Avalonia.Input.StandardCursorType.Cross;
    }

    private void ZoomCanvas_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_zoomModeActive || sender is not Avalonia.Controls.Canvas canvas) return;

        if (_zoomDrag == ZoomDrag.None)
        {
            canvas.Cursor = new Avalonia.Input.Cursor(ZoomHoverCursor(e.GetPosition(canvas)));
            return;
        }

        var vid = GetVideoDisplayRect(canvas);
        var bounds = ZoomBoundsUi(canvas);

        var p = e.GetPosition(canvas);
        double aspect = ZoomAspect;

        double scale = vid.Width / Math.Max(1, GetSourceW());
        double minW = MinZoomWidthUi(scale);
        double minH = minW / aspect;

        if (_zoomDrag == ZoomDrag.Draw || _zoomDrag == ZoomDrag.ResizeBR || _zoomDrag == ZoomDrag.ResizeTR
            || _zoomDrag == ZoomDrag.ResizeBL || _zoomDrag == ZoomDrag.ResizeTL)
        {
            Avalonia.Point anchor = _zoomDrag switch
            {
                ZoomDrag.ResizeBR => _zoomStartRect.TopLeft,
                ZoomDrag.ResizeTR => new Avalonia.Point(_zoomStartRect.X, _zoomStartRect.Bottom),
                ZoomDrag.ResizeBL => new Avalonia.Point(_zoomStartRect.Right, _zoomStartRect.Y),
                ZoomDrag.ResizeTL => _zoomStartRect.BottomRight,
                _ => _zoomDragStart
            };
            double w = Math.Abs(p.X - anchor.X);
            double h = w / aspect;
            if (w < minW) { w = minW; h = minH; }
            double x = p.X >= anchor.X ? anchor.X : anchor.X - w;
            double y = p.Y >= anchor.Y ? anchor.Y : anchor.Y - h;
            _zoomUiRect = ClampToVideo(new Avalonia.Rect(x, y, w, h), bounds, aspect, minW, minH);
            _hasZoomBox = true;
        }
        else if (_zoomDrag == ZoomDrag.Move)
        {
            double dx = p.X - _zoomDragStart.X, dy = p.Y - _zoomDragStart.Y;
            double maxX = Math.Max(bounds.X, bounds.Right - _zoomStartRect.Width);
            double maxY = Math.Max(bounds.Y, bounds.Bottom - _zoomStartRect.Height);
            double nx = Math.Clamp(_zoomStartRect.X + dx, bounds.X, maxX);
            double ny = Math.Clamp(_zoomStartRect.Y + dy, bounds.Y, maxY);
            _zoomUiRect = new Avalonia.Rect(nx, ny, _zoomStartRect.Width, _zoomStartRect.Height);
        }

        if (!_zoomDragMoved) HideZoomStylePanel();
        _zoomDragMoved = true;
        RenderZoomBox();
        e.Handled = true;
    }

    /// <summary>
    /// ZOOM_07 — did the pointer actually MOVE between press and release, or was this a bare click?
    /// </summary>
    private bool _zoomDragMoved;

    /// <summary>ZOOM_11 — closes the Slow/Instant picker. Safe to call when it is already closed.</summary>
    private void HideZoomStylePanel()
    {
        var stylePanel = this.FindControl<Border>("ZoomStylePanel");
        if (stylePanel != null) stylePanel.IsVisible = false;
    }

    private void ZoomCanvas_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (sender is Avalonia.Controls.Canvas canvas) { e.Pointer.Capture(null); canvas.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross); }
        if (!_zoomModeActive) return;
        bool wasDraw = _zoomDrag == ZoomDrag.Draw;
        bool wasResize = _zoomDrag is ZoomDrag.ResizeTL or ZoomDrag.ResizeTR or ZoomDrag.ResizeBL or ZoomDrag.ResizeBR;
        bool moved = _zoomDragMoved;
        _zoomDrag = ZoomDrag.None;
        _zoomDragMoved = false;
        if (!_hasZoomBox) return;

        if (wasDraw && !moved)
        {
            _zoomUiRect = _zoomStartRect;
            RenderZoomBox();
            SetStatus("Drag to draw a new box, or drag the one that is there to move it.");
            e.Handled = true;
            return;
        }

        // ZOOMLIVE_01 — THE GESTURE IS THE COMMIT. First touch also promotes the suggested box
        // into a real zoom, which is what `_zoomBoxTouched` records.
        bool firstTouch = !_zoomBoxTouched;
        _zoomBoxTouched = true;
        _zoomSessionCreatedSegment = false;   // the block now has a zoom on it; it has earned its place

        CommitZoomToSegment(wasDraw ? "Created" : wasResize ? "Resized" : "Moved");

        if (firstTouch)
        {
            PulseZoomConfirmFeedback();
            RenderZoomBox();                  // repaint at full opacity
        }

        // ⚠️ ON RELEASE ONLY, NEVER PER POINTER MOVE. Priming the simulated crop stalls the window
        // behind a blocking overlay while mpv buffers; running it during a drag would reproduce
        // exactly the stutter that MEME_08/DRAG_FIX had to remove from the meme drag.
        _ = CommitZoomAndPrimePreviewAsync();

        e.Handled = true;
    }

    /// <summary>
    /// Clamp a candidate box into the video rect, preserving aspect and floor.
    ///
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// ⚠️ ZOOM_06 — THE Math.Max ON EACH AXIS IS LOAD-BEARING. DO NOT "SIMPLIFY" IT AWAY.
    ///
    /// This method used to call `Math.Clamp(r.X, vid.X, vid.Right - w)` directly, and that CRASHED
    /// THE WHOLE APPLICATION:
    ///     ArgumentException: '600.2810304449649' cannot be greater than 600.2810304449648
    /// Math.Clamp throws when min > max, and here they differed by ONE UNIT IN THE LAST PLACE.
    ///
    /// HOW TWO IDENTICAL NUMBERS DISAGREE. Drag the box wider than the area it is allowed to fill
    /// and the first line pins `w` to exactly `vid.Width`. The maximum X is then `vid.Right - w`,
    /// and `vid.Right` is itself stored as `vid.X + vid.Width`. In exact arithmetic
    /// (vid.X + vid.Width) - vid.Width is vid.X. In binary floating point it can land one ulp
    /// BELOW it — so min (vid.X) becomes greater than max, and Math.Clamp throws rather than
    /// returning either. A pointer-move handler is the worst possible place for that: it fires on
    /// every mouse movement, the throw escapes into Avalonia's input loop, and the process dies
    /// mid-drag with the user's segment work unsaved.
    ///
    /// THE SECOND CASE THIS ALSO FIXES: on a very small preview, or a low-resolution source in
    /// portrait, the quality floor `minW` can legitimately exceed the usable width. Then
    /// `vid.Right - w` is genuinely, largely below `vid.X` — not a rounding hair — and the old code
    /// would have thrown just the same. Pinning to `vid.X` puts the oversized box at the left edge
    /// of the usable area, which is the only sane answer.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private Avalonia.Rect ClampToVideo(Avalonia.Rect r, Avalonia.Rect vid, double aspect, double minW, double minH)
    {
        double w = Math.Min(r.Width, vid.Width);
        double h = w / aspect;
        if (h > vid.Height) { h = vid.Height; w = h * aspect; }
        if (w < minW) { w = minW; h = minH; }

        double maxX = Math.Max(vid.X, vid.Right - w);
        double maxY = Math.Max(vid.Y, vid.Bottom - h);
        double x = Math.Clamp(r.X, vid.X, maxX);
        double y = Math.Clamp(r.Y, vid.Y, maxY);
        return new Avalonia.Rect(x, y, w, h);
    }

    private int GetSourceW() => FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution).w;

    /// <summary>
    /// ZOOM_01 — the width of the frame the VIEWER finally sees, IN SOURCE PIXELS.
    ///
    /// ⚠️ UNITS_01 — THIS RETURNED AN OUTPUT-PIXEL COUNT WHERE EVERY CALLER USES SOURCE PIXELS.
    /// It handed back `CoordinateConstants.PortraitW` (1080) in portrait — the width of the
    /// FINISHED FILE. What the viewer actually sees is the surviving slice of the SOURCE, which is
    /// 720px on a 1920x1080 capture and 960px on a 2560x1440 one: `InternalW / scale`, the same
    /// quantity `ZoomPreviewSimulator.PortraitSurvivingWidth` computes. Feeding an output width
    /// into a source-pixel divisor inflated the minimum zoom box by exactly the ratio between them
    /// (1080/720 = 1.5x), so the `MaxZoomUpscale` quality floor bit 1.5x too early and the real
    /// ceiling was 5.33x, not the 8x this constant declares. It is also what produced the W=134
    /// box in the drift report — the user drew smaller and the floor silently snapped it up.
    /// Landscape was always correct, which is why it went unnoticed.
    /// </summary>
    private double ZoomOutputWidthSource()
    {
        if (!_isMobileFormat) return Math.Max(1, GetSourceW());

        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (sw <= 0 || sh <= 0) return FortniteVideoSoftware.Core.Media.CoordinateConstants.PortraitW;

        double scale = Math.Max(
            FortniteVideoSoftware.Core.Media.CoordinateConstants.InternalW / (double)sw,
            FortniteVideoSoftware.Core.Media.CoordinateConstants.InternalH / (double)sh);
        return FortniteVideoSoftware.Core.Media.CoordinateConstants.InternalW / scale;
    }

    /// <summary>
    /// ZOOM_01 — the smallest legal box width, expressed on the CANVAS in UI pixels.
    /// <paramref name="uiPerSourcePx"/> is the video's on-screen scale (vid.Width / sourceW).
    /// </summary>
    private double MinZoomWidthUi(double uiPerSourcePx)
        => (ZoomOutputWidthSource() / MaxZoomUpscale) * uiPerSourcePx;

    /// <summary>
    /// ZOOM_02 — the rectangle the box is allowed to live in, on the canvas.
    /// In portrait that is NOT the whole picture: it is the 2:3 centre strip the export keeps
    /// (the "brick wall"). Centring the auto box on the full frame instead of on this strip would
    /// drop half of it into the shaded columns portrait throws away.
    /// This calculation was duplicated inline in ZoomCanvas_PointerMoved; both now call here so the
    /// draw clamp and the auto-placement can never disagree about where the wall is.
    /// </summary>
    private Avalonia.Rect ZoomBoundsUi(Avalonia.Controls.Canvas canvas)
    {
        var vid = GetVideoDisplayRect(canvas);
        if (!_isMobileFormat) return vid;
        double portraitW = vid.Height * (2.0 / 3.0);
        return new Avalonia.Rect(vid.X + (vid.Width - portraitW) / 2.0, vid.Y, portraitW, vid.Height);
    }

    /// <summary>
    /// ZOOM_03 — how many times closer the box is than the un-zoomed picture. Measured against the
    /// USABLE width (the portrait strip in mobile format), because that is what actually fills the
    /// screen — measuring against the full frame would report a portrait zoom as weaker than it is.
    /// </summary>
    private double ZoomFactorOf(Avalonia.Rect boxUi, Avalonia.Rect boundsUi)
        => boxUi.Width < 1 ? 1.0 : boundsUi.Width / boxUi.Width;

    private static int Even(int v) => v % 2 == 0 ? v : v - 1;

    private void CommitZoomToSegment(string action)
    {
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        if (canvas == null || _selectedSegmentIndex < 0 || _selectedSegmentIndex >= _segments.Count) return;
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (!IsVideoRectUsable(vid) || sw <= 0 || sh <= 0)
        {
            RuntimeLog.Info("Granular", "Zoom commit skipped — the preview has no usable size yet.");
            return;
        }

        double sx = sw / vid.Width, sy = sh / vid.Height;
        int zx = Even(Math.Clamp((int)Math.Round((_zoomUiRect.X - vid.X) * sx), 0, sw - 2));
        int zy = Even(Math.Clamp((int)Math.Round((_zoomUiRect.Y - vid.Y) * sy), 0, sh - 2));
        int zw = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Width * sx), 2, sw - zx));
        int zh = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Height * sy), 2, sh - zy));

        int minWsrc = Even((int)Math.Round(ZoomOutputWidthSource() / MaxZoomUpscale));
        if (minWsrc >= 2 && zw < minWsrc)
        {
            int cx = zx + zw / 2, cy = zy + zh / 2;
            int fixedW = Math.Min(minWsrc, Even(sw));
            int fixedH = Even((int)Math.Round(fixedW / ZoomAspect));
            if (fixedH > sh) { fixedH = Even(sh); fixedW = Even((int)Math.Round(fixedH * ZoomAspect)); }

            int newX = Even(Math.Clamp(cx - fixedW / 2, 0, Math.Max(0, sw - fixedW)));
            int newY = Even(Math.Clamp(cy - fixedH / 2, 0, Math.Max(0, sh - fixedH)));

            RuntimeLog.Info("Granular",
                $"Zoom box was below the {MaxZoomUpscale:0}x quality floor (W={zw}) — snapped up to {fixedW}x{fixedH}.");
            zx = newX; zy = newY; zw = fixedW; zh = fixedH;
        }

        var seg = _segments[_selectedSegmentIndex];
        bool slow = ZoomSlowSelected;
        PushUndo("apply zoom");   // UNDO_02
        _segments[_selectedSegmentIndex] = seg with
        {
            ZoomX = zx, ZoomY = zy, ZoomW = zw, ZoomH = zh, ZoomOrigRes = $"{sw}x{sh}", ZoomSlow = slow
        };

        RuntimeLog.Info("Granular", $"Zoom {action} on segment #{_selectedSegmentIndex + 1} (start {FormatMs(seg.StartMs)}): X={zx} Y={zy} W={zw} H={zh} src={sw}x{sh} mobile={_isMobileFormat} ramp={(slow ? "SLOW" : "INSTANT")}.");
        RefreshSegmentList();
        RedrawTimeline();
    }

    private const string ZoomTutorialCounterFile = "zoom_tutorial.txt";

    private static int ReadZoomTutorialCount()
        => FortniteVideoSoftware.Core.Infrastructure.UiStateStore.ReadInt(ZoomTutorialCounterFile);

    private static void WriteZoomTutorialCount(int n)
        => FortniteVideoSoftware.Core.Infrastructure.UiStateStore.WriteInt(ZoomTutorialCounterFile, n);

    private static bool _zoomTutorialShownThisSession = false;
    private void MaybeShowZoomTutorial(Avalonia.Controls.Canvas canvas)
    {
        if (_zoomTutorialShownThisSession) return;
        if (ReadZoomTutorialCount() >= 3) return;
        _zoomTutorialShownThisSession = true;
        WriteZoomTutorialCount(ReadZoomTutorialCount() + 1);

        if (_zoomTutorial == null)
        {
            _zoomTutorial = new Avalonia.Controls.Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B0000000")),
                BorderBrush = ZoomBrush(),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 12),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "CLICK AND DRAG TO DRAW ZOOM BOX",
                    Foreground = ZoomBrush(),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(16)
                }
            };
            canvas.Children.Add(_zoomTutorial);
        }
        _zoomTutorial.IsVisible = true;
        _zoomTutorial.Opacity = 1;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_zoomTutorial == null || !_zoomModeActive) return;
            _zoomTutorial.Measure(Avalonia.Size.Infinity);
            double tw = _zoomTutorial.DesiredSize.Width;
            Avalonia.Controls.Canvas.SetLeft(_zoomTutorial, Math.Max(0, (canvas.Bounds.Width - tw) / 2));
            Avalonia.Controls.Canvas.SetTop(_zoomTutorial, 40);

            _zoomTutorialTimer?.Stop();
            _zoomTutorialTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _zoomTutorialTimer.Tick += (_, __) => HideZoomTutorial();
            _zoomTutorialTimer.Start();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void HideZoomTutorial()
    {
        _zoomTutorialTimer?.Stop();
        _zoomTutorialTimer = null;
        if (_zoomTutorial != null) { _zoomTutorial.IsVisible = false; }
    }

    /// <summary>§5 Live playhead sync: show the yellow box only while the caret is inside a zoomed segment.</summary>
    // ══════════════════════════════════════════════════════════════════════════════════════
    // CUTS_03 — THE EDITOR THAT MAKES THE CUTS NOW HONOURS THEM.
    //
    // DELETE PARTS removed the footage from the export and from the timeline drawing, but this
    // window's own mpv preview knew nothing about it and played straight through the deleted
    // section — so the one screen where a user checks their cut was the one screen that showed
    // them the thing they had just cut out.
    //
    // Unlike the Music Wizard's phase-3 preview, this player is NOT driven from an output clock:
    // mpv runs forward through the source at its own pace and this tick only intervenes for
    // freezes. There is therefore nothing to make it step over a cut on its own, and it needs the
    // same explicit watchdog the Main App uses.
    //
    // ⚠️ `_cuts` are TRIM-RELATIVE ms in this window (see the field's comment) while mpv reports
    // ABSOLUTE source seconds, so `_trimStartMs` has to be added back before comparing. Getting
    // that wrong would make the skip fire in the wrong place, or never.
    //
    // Fire-and-forget on the seek, and TRUE returned so the caller abandons the rest of the tick:
    // this must never block the interface thread waiting on mpv (ZOOMHANG_01).
    // ══════════════════════════════════════════════════════════════════════════════════════
    private bool SkipPreviewOutOfCut()
    {
        try
        {
            if (_cuts.Count == 0) return false;
            var ipc = _videoHost?.IpcClient;
            if (ipc == null) return false;

            // A freeze is deliberately parked on one frame; a scrub is the user's own hand on the
            // playhead. Neither is playback wandering into a cut, and yanking the position out
            // from under either would fight the user.
            if (_isCurrentlyFrozen || _isCanvasScrubbing) return false;

            double nowMs = ipc.CurrentTime * 1000.0;
            foreach (var cut in _cuts)
            {
                double absStartMs = cut.StartMs + _trimStartMs;
                double absEndMs = cut.EndMs + _trimStartMs;
                if (nowMs <= absStartMs + 1 || nowMs >= absEndMs - 1) continue;

                double trimEndMs = _trimEndMs > 0 ? _trimEndMs : absEndMs;
                double toSec = Math.Min(absEndMs, trimEndMs) / 1000.0;

                _ = ipc.SetPropertyAsync("time-pos",
                    toSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                _memePreview?.NotifySeek();   // MEME_07 — a jump, not playback
                return true;
            }
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }

        return false;
    }

    private void UpdateZoomPlayheadOverlay()
    {
        if (_zoomModeActive) return;
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("ZoomOverlayCanvas");
        if (canvas == null || _videoHost?.IpcClient == null) return;

        if (_gpuLiveZoomPreview)
        {
            if (canvas.IsVisible) canvas.IsVisible = false;
            return;
        }

        double tRelMs = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _trimStartMs);
        SpeedSegment? active = null;
        foreach (var s in _segments)
            if (s.ZoomW.HasValue && tRelMs >= s.StartMs && tRelMs <= s.EndMs) { active = s; break; }

        if (active == null) { if (canvas.IsVisible) { canvas.IsVisible = false; } return; }

        EnsureZoomVisuals(canvas);
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FortniteVideoSoftware.Core.Media.CoordinateMath.GetResolutionInts(_originalResolution);
        if (sw <= 0 || sh <= 0) return;
        double scx = vid.Width / sw, scy = vid.Height / sh;
        _zoomUiRect = new Avalonia.Rect(vid.X + active.ZoomX!.Value * scx, vid.Y + active.ZoomY!.Value * scy,
                                        active.ZoomW!.Value * scx, active.ZoomH!.Value * scy);
        _hasZoomBox = true;
        canvas.IsVisible = true;
        canvas.IsHitTestVisible = false;
        RenderZoomBox();
        foreach (var h in _zoomHandles) h.IsVisible = false;
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoHost?.IpcClient == null) return;

        // ══════════════════════════════════════════════════════════════════════════════════
        // MEME_07 — BEFORE EVERYTHING, INCLUDING THE CUT SKIP.
        //
        // The director may have swapped the meme file into this very host, in which case
        // CurrentTime, Duration and IsEof all describe the MEME and not the gameplay. Running
        // the rest of this tick against them would seek the cut-skip somewhere random, trip the
        // trim-end stop, arm the freeze at the wrong instant and re-crop the picture. Holding
        // the caret is the ONLY thing allowed to happen while a meme is on screen.
        // ══════════════════════════════════════════════════════════════════════════════════
        _memePreview?.Tick();
        if (_memePreview != null && _memePreview.IsActive)
        {
            HoldCaretDuringMeme();
            return;
        }

        // CUTS_03 — before anything else this tick does. If playback has wandered into footage the
        // user deleted, nothing else on this tick is meaningful: the caret, the zoom overlay and
        // the freeze arming would all be reasoning about a frame that is not in the video.
        if (SkipPreviewOutOfCut()) return;

        if (_videoHost.IpcClient.VideoWidth > 0 && _videoHost.IpcClient.VideoHeight > 0)
        {
            string liveRes = $"{_videoHost.IpcClient.VideoWidth}x{_videoHost.IpcClient.VideoHeight}";
            if (liveRes != _originalResolution) _originalResolution = liveRes;
        }
        UpdateZoomPlayheadOverlay();
        UpdateLiveZoomCrop();

        double t = _videoHost.IpcClient.CurrentTime;
        double fullDur = _videoHost.IpcClient.Duration;

        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : fullDur;
        if (t >= trimEndSec && !_videoHost.IpcClient.IsPaused)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", trimEndSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        double trimStartSec = _trimStartMs / 1000.0;
        double relTime = Math.Max(0, t - trimStartSec);
        double trimDurSec = Math.Max(0.1, trimEndSec - trimStartSec);

        if (fullDur > 0)
        {
            if (!_isTimelineDrawn)
            {
                RedrawTimeline();
                _isTimelineDrawn = true;
            }
        }

        double currentRelMs = relTime * 1000.0;
        double currentAbsMs = currentRelMs + _trimStartMs;

        double prevFreezeTickAbsMs = _prevFreezeTickAbsMs;
        _prevFreezeTickAbsMs = currentAbsMs;

        if (_freezeTimeMs >= 0 && currentAbsMs < _freezeTimeMs - 40) _freezeArmed = true;

        if (_freezeTimeMs >= 0 && _freezeArmed && !_isCurrentlyFrozen && !_videoHost.IpcClient.IsPaused)
        {
            if (prevFreezeTickAbsMs >= 0
                && prevFreezeTickAbsMs <= _freezeTimeMs + 60
                && currentAbsMs >= _freezeTimeMs)
            {
                _isCurrentlyFrozen = true;
                _freezeArmed = false;
                _freezeStartTime = DateTime.UtcNow;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", (_freezeTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _memePreview?.NotifySeek();   // MEME_07 — the freeze parked it; not playback
                return;
            }
        }
        else if (_isCurrentlyFrozen)
        {
            double heldFor = (DateTime.UtcNow - _freezeStartTime).TotalSeconds;
            if (heldFor >= _freezeDurationS)
            {
                _isCurrentlyFrozen = false;
                _holdCaretOutSec = null;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            else
            {
                if (!_isCanvasScrubbing)
                {
                    _holdCaretOutSec = FreezeHoldStartOutSec() + Math.Clamp(heldFor, 0, _freezeDurationS);
                    UpdateCaret();
                }
                return;
            }
        }

        if (!_videoHost.IpcClient.IsPaused && _segments.Count > 0)
        {
            double targetSpeed = GetEditorSpeedForPosition(currentRelMs);
            if (Math.Abs(targetSpeed - _lastAppliedSpeed) > 0.001)
            {
                _lastAppliedSpeed = targetSpeed;
                _ = _videoHost.IpcClient.SetPropertyAsync("speed",
                    targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }


        if (trimDurSec > 0 && !_isCanvasScrubbing)
        {
            // MEME_08 — a meme drag parked the caret deliberately and the player is paused, so
            // there is no playback position to sync to. The moment it plays again, normal service.
            if (_memeCaretSticky && _videoHost.IpcClient.IsPaused)
            {
                // deliberately left where the drag put it
            }
            else
            {
                _memeCaretSticky = false;
                _holdCaretOutSec = null;
                _playheadMs = relTime * 1000.0;
                UpdateCaret();
            }
        }

        var timeMapper = FortniteVideoSoftware.Core.Media.GranularSpeedBuilder.CreateTimeMapper(_trimEndMs - _trimStartMs, _segments, _baseSpeed, _trimStartMs);
        double editedTimeSec = timeMapper(t);
        if (_isCurrentlyFrozen)
        {
            editedTimeSec = FreezeHoldStartOutSec() + Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, _freezeDurationS);
        }
        bool isVoicePaused = _videoHost.IpcClient.IsPaused;
        _voiceOverPlayer.UpdatePlayback(isVoicePaused, t >= trimEndSec, editedTimeSec, timeMapper, _isCurrentlyFrozen);
    }

    /// <summary>
    /// LANES_01 — moves the playhead because the USER dragged, and seeks the video to match.
    /// Distinct from the playback-driven update above, which must NOT seek (that would fight the
    /// player). <paramref name="msFromTrimStart"/> is trim-relative, like everything else here.
    /// </summary>
    private void SetPlayheadFromScrub(double msFromTrimStart)
    {
        double dur = GetDuration();
        if (dur <= 0) return;

        _memeCaretSticky = false;   // MEME_08 — the user took the playhead back
        _holdCaretOutSec = null;
        _playheadMs = Math.Clamp(msFromTrimStart, 0, dur * 1000.0);
        UpdateCaret();

        if (_videoHost?.IpcClient != null) _ = SeekInternal(_playheadMs / 1000.0);
        _memePreview?.NotifySeek();
    }


    private string? _thumbStripFile;
    private CancellationTokenSource? _thumbCts;
    private Avalonia.Media.Imaging.Bitmap? _thumbBitmap;
    private Avalonia.Controls.Canvas? _frameLaneHost;

    private async Task BuildFrameLaneAsync()
    {
        var laneGrid = _thumbLaneGrid;
        var loading = _thumbLoadingOverlay;
        if (laneGrid == null) return;

        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath)) return;

        double dur = GetDuration();
        if (dur <= 0) return;

        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        var token = cts.Token;

        if (loading != null) loading.IsVisible = true;
        try
        {
            string ffmpeg = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            string temp = FortniteVideoSoftware.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory;

            void MountLane(Avalonia.Media.Imaging.Bitmap bmp)
            {
                DeleteThumbStrip();
                _thumbBitmap = bmp;
                _frameLaneHost = new Avalonia.Controls.Canvas { ClipToBounds = true };
                laneGrid.Children.Clear();
                laneGrid.Children.Add(_frameLaneHost);
                _frameLaneHost.SizeChanged += (_, _) => RelayoutFrameLane();
                if (loading != null) loading.IsVisible = false;
                RelayoutFrameLane();
            }

            var warmed = FortniteVideoSoftware.App.Services.FilmstripPrewarm.TryTake(
                _videoPath, _trimStartMs / 1000.0, dur);
            if (warmed != null)
            {
                MountLane(warmed);
                RuntimeLog.Info("Granular", "Film lane served from the background prewarm.");
                return;
            }

            bool streamed = await ThumbnailStripGenerator.StreamAsync(
                ffmpeg, _videoPath, _trimStartMs / 1000.0, dur, token,
                onReady: wb => MountLane(wb),
                onFrame: RelayoutFrameLane,
                logTag: "Granular");

            if (token.IsCancellationRequested) return;
            if (streamed) return;

            string? strip = await ThumbnailStripGenerator.GenerateAsync(
                ffmpeg, _videoPath, temp,
                _trimStartMs / 1000.0, dur, token, logTag: "Granular");

            if (token.IsCancellationRequested || strip == null) return;

            MountLane(new Avalonia.Media.Imaging.Bitmap(strip));
            _thumbStripFile = strip;
        }
        catch (OperationCanceledException) { }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Could not build the film-frame lane: {ex.Message}");
        }
        finally
        {
            if (loading != null) loading.IsVisible = false;
            if (ReferenceEquals(_thumbCts, cts)) _thumbCts = null;
            try { cts.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }

    /// <summary>
    /// TIME_02 / F4 — lays the SOURCE-LINEAR thumbnail strip out along the OUTPUT-TIME axis.
    ///
    /// <para>
    /// One slot per <see cref="FortniteVideoSoftware.Core.Media.OutputTimeline"/> chunk. Each slot is
    /// a clipping Canvas placed at the chunk's OUTPUT position and width; inside it the strip is
    /// scaled and offset so that exactly the chunk's SOURCE window fills the slot. A half-speed
    /// segment therefore shows its frames spread over twice the width, and a freeze shows the held
    /// frame stretched across the whole hold — which is what the exported file looks like.
    /// </para>
    /// <para>
    /// A freeze chunk covers a hair of source time by construction, so scaling by its true source
    /// width would blow the image up astronomically. One frame's worth of source time is estimated
    /// from the strip geometry instead (frames are laid out at the strip's own height and a 16:9
    /// aspect), which shows the frozen frame rather than a smear.
    /// </para>
    /// <para>
    /// Degrades safely: on ANY failure the lane falls back to the plain stretched strip, because a
    /// mis-drawn background must never block editing.
    /// </para>
    /// </summary>
    private void RelayoutFrameLane()
    {
        var host = _frameLaneHost;
        var bmp = _thumbBitmap;
        if (host == null || bmp == null) return;

        try
        {
            host.Children.Clear();
            double w = host.Bounds.Width, h = host.Bounds.Height;
            if (w <= 0 || h <= 0) return;

            double srcDur = Math.Max(0.001, GetDuration());
            double outDur = OutDurationSec();
            var chunks = OutTimeline().Chunks;
            if (chunks.Count == 0) return;

            double stripPxW = Math.Max(1, bmp.PixelSize.Width);
            double stripPxH = Math.Max(1, bmp.PixelSize.Height);
            double frameSec = Math.Max(0.02, srcDur * (stripPxH * 16.0 / 9.0) / stripPxW);

            double accOut = 0;
            foreach (var ch in chunks)
            {
                double outLen = ch.OutputLengthSec;
                double x = (accOut / outDur) * w;
                double slotW = (outLen / outDur) * w;
                accOut += outLen;
                if (slotW <= 0.5) continue;

                double s1, s2;
                if (ch.IsFreeze)
                {
                    s1 = Math.Clamp(ch.SourceStartSec, 0, Math.Max(0, srcDur - frameSec));
                    s2 = Math.Min(srcDur, s1 + frameSec);
                }
                else { s1 = ch.SourceStartSec; s2 = ch.SourceEndSec; }

                double srcSpan = Math.Max(0.001, s2 - s1);
                double scaledFullW = slotW * (srcDur / srcSpan);

                var slot = new Avalonia.Controls.Canvas { Width = slotW, Height = h, ClipToBounds = true };
                Avalonia.Controls.Canvas.SetLeft(slot, x);
                Avalonia.Controls.Canvas.SetTop(slot, 0);

                var img = new Avalonia.Controls.Image
                {
                    Source = bmp,
                    Stretch = Avalonia.Media.Stretch.Fill,
                    Width = scaledFullW,
                    Height = h
                };
                Avalonia.Controls.Canvas.SetLeft(img, -(s1 / srcDur) * scaledFullW);
                Avalonia.Controls.Canvas.SetTop(img, 0);

                slot.Children.Add(img);
                host.Children.Add(slot);

                if (ch.IsFreeze) DecorateFrozenSpan(host, x, slotW, h, withLabel: true);
            }
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Frame lane re-layout failed, falling back to a linear strip: {ex.Message}");
            try
            {
                host.Children.Clear();
                host.Children.Add(new Avalonia.Controls.Image
                {
                    Source = bmp,
                    Stretch = Avalonia.Media.Stretch.Fill,
                    Width = host.Bounds.Width,
                    Height = host.Bounds.Height
                });
            }
            catch (System.Exception inner) { RuntimeLog.Swallowed(inner); }
        }
    }

    private void DeleteThumbStrip()
    {
        try { _thumbBitmap?.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _thumbBitmap = null;
        _frameLaneHost = null;

        if (string.IsNullOrEmpty(_thumbStripFile)) return;
        try { if (File.Exists(_thumbStripFile)) File.Delete(_thumbStripFile); }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _thumbStripFile = null;
    }

    /// <summary>
    /// LANES_01 — positions the single caret that crosses BOTH lanes.
    ///
    /// It is deliberately parented to a Panel that spans the whole two-lane row rather than being
    /// drawn into either canvas: one line, one X, so the frame under it and the segment above it
    /// can never disagree about what moment they are showing.
    /// </summary>
    private void UpdateCaret()
    {
        var lanes = this.FindControl<FortniteVideoSoftware.App.Controls.TimelineLanesControl>("GranularLanes");
        if (lanes == null) return;

        double outDur = OutDurationSec();
        if (Math.Abs(lanes.DurationSeconds - outDur) > 0.001) lanes.DurationSeconds = outDur;

        double outSec = _holdCaretOutSec ?? OutTimeline().SourceToOutput(_playheadMs / 1000.0);
        lanes.PositionSeconds = Math.Clamp(outSec, 0, outDur);
    }

    /// <summary>
    /// FREEZE_CARET — output seconds at which the current freeze's hold BEGINS.
    ///
    /// <para>
    /// <c>SourceToOutput</c> of the freeze instant already counts the whole hold (documented
    /// boundary behaviour in <see cref="FortniteVideoSoftware.Core.Media.OutputTimeline"/>, and the
    /// reason F3 exists), so it returns the moment the hold ENDS. Subtracting the hold gives the
    /// moment it starts. This is the same correction the freeze marker uses when it is drawn.
    /// </para>
    /// </summary>
    private double FreezeHoldStartOutSec()
    {
        double freezeRelSec = Math.Max(0, (_freezeTimeMs - _trimStartMs) / 1000.0);
        return Math.Max(0, OutTimeline().SourceToOutput(freezeRelSec) - _freezeDurationS);
    }

    /// <summary>
    /// Returns the current playback position relative to the trim region.
    /// 0.0 = MARK START position.
    /// </summary>
    private double GetCurrentTime()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double absTime = _videoHost.IpcClient.CurrentTime;
        double relTime = absTime - (_trimStartMs / 1000.0);
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : double.MaxValue;
        if (relTime < 0) relTime = 0;
        if (relTime > trimEndSec - (_trimStartMs / 1000.0)) relTime = trimEndSec - (_trimStartMs / 1000.0);
        return relTime;
    }

    /// <summary>
    /// Looks up the playback speed for a given relative position (in ms from trim start).
    /// Returns the segment's speed if the position falls within a speed segment,
    /// otherwise returns the base speed. Freeze segments (speed ≈ 0) return 0.
    /// </summary>
    private double GetEditorSpeedForPosition(double relPosMs)
    {
        foreach (var seg in _segments)
        {
            if (relPosMs >= seg.StartMs && relPosMs < seg.EndMs)
            {
                return seg.Speed;
            }
        }
        return _baseSpeed;
    }

    /// <summary>
    /// Returns the duration of the trim region (not the full video).
    /// </summary>
    private int? FindSegmentAtPosition(int positionMs)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            if (positionMs >= _segments[i].StartMs && positionMs <= _segments[i].EndMs)
                return i;
        }
        return null;
    }


    private FortniteVideoSoftware.Core.Media.OutputTimeline? _outTimeline;
    private string _outTimelineSig = "";


    // ══════════════════════════════════════════════════════════════════════════════════════
    // MEME_06 — PLACING, MOVING AND REMOVING A MEME.
    //
    // THE FRAME-OF-REFERENCE RULE, because everything here turns on it:
    //   the ruler you see            OUTPUT seconds (the finished video's length)
    //   what a MemePlacement stores  CLIP-RELATIVE SOURCE seconds (a moment of gameplay)
    // A meme occupies ZERO source seconds and its full DurationSec of output seconds. So a meme is
    // a POINT in the stored model and a BLOCK on screen, and every conversion between the two goes
    // through OutputTimeline. There is no linear shortcut: inside a 2x segment one output second is
    // two source seconds, so anything computed as "pixels times a constant" is wrong the moment a
    // speed segment sits between the clip start and the meme.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MEME_06 — the memes the Main App scanned, handed over so this editor does not re-scan the
    /// folder or re-probe every file. Set before ShowDialog; empty is legal and the picker says so.
    /// </summary>
    public IReadOnlyList<MemeItem> AvailableMemes { get; set; } = System.Array.Empty<MemeItem>();

    private async void OnAddMemeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_videoPath)) return;

        var picked = await Controls.MemePickerWindow.PickAsync(this, AvailableMemes);
        if (picked == null) return;

        if (!System.IO.File.Exists(picked.FullPath))
        {
            NotifyError("That meme file is no longer on disk.");
            return;
        }

        // Where the playhead is, in SOURCE seconds relative to the trim-in point. _playheadMs is
        // already trim-relative, so this is a division and nothing more.
        double rawSourceRelSec = Math.Max(0, _playheadMs / 1000.0);

        var tl = OutTimeline();

        // D8 — a meme may not interrupt a freeze or a speed segment, and may not sit in deleted
        // footage. SnapInsertionPoint moves it FORWARD to the first legal instant. The marker is
        // then drawn at the snapped value, never at the raw playhead, or the meme lands somewhere
        // the user never saw.
        double snapped = tl.SnapInsertionPoint(rawSourceRelSec);

        // D7 — two memes may not share a point. Checked against the snapped value, because that is
        // where this one would actually go.
        if (tl.HasInsertionAtSource(snapped))
        {
            NotifyError("A meme already sits at that exact point. Move the playhead a little.");
            return;
        }

        if (!MemeSeparationIsSafe(snapped, null, out string? clash))
        {
            NotifyError(clash!);
            return;
        }

        double duration = await ResolveMemeDurationAsync(picked);
        if (duration <= 0.01)
        {
            NotifyError("That meme's length could not be read, so it was not added.");
            return;
        }

        PushUndo("add meme");   // UNDO_02

        // ⚠️ NewId takes an INDEX and must be unique for the life of this editor, not merely
        // unique in the current list: adding two memes, removing the first, then adding another
        // would hand the new one the id the survivor already holds — and ids become FFmpeg filter
        // labels, so a collision corrupts the export graph rather than merely confusing the UI.
        string id = FortniteVideoSoftware.Core.Media.MemePlacement.NewId(_nextMemeIdIndex++);
        while (_memes.Any(m => m.Id == id))
            id = FortniteVideoSoftware.Core.Media.MemePlacement.NewId(_nextMemeIdIndex++);
        _memes.Add(new FortniteVideoSoftware.Core.Media.MemePlacement(
            picked.FullPath, snapped, duration, id));
        _selectedMemeId = id;

        bool moved = Math.Abs(snapped - rawSourceRelSec) > 0.01;
        RuntimeLog.Info("MEME",
            $"Added '{System.IO.Path.GetFileName(picked.FullPath)}' at {snapped:0.###}s source-relative " +
            $"({duration:0.###}s long){(moved ? $"; slid {snapped - rawSourceRelSec:0.###}s forward off a speed block or deleted part" : "")}.");

        InvalidateMemeTimelines();
        RedrawTimeline();
        UpdateMemeButtonsState();
        ShowMemeLandingFrame(id);                                                    // MEME_08
        await RefreshMemePreviewAsync("Fitting the meme into your video...");        // MEME_07

        Notify(moved
            ? $"Meme added — it slid {snapped - rawSourceRelSec:0.0}s along, off a block it cannot interrupt."
            : "Meme added");
    }

    private void OnRemoveMemeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RemoveSelectedMeme();

    private void RemoveSelectedMeme()
    {
        if (_selectedMemeId == null) return;
        int idx = _memes.FindIndex(m => m.Id == _selectedMemeId);
        if (idx < 0) { _selectedMemeId = null; UpdateMemeButtonsState(); return; }

        PushUndo("remove meme");   // UNDO_02

        string name = System.IO.Path.GetFileName(_memes[idx].FilePath);
        _memes.RemoveAt(idx);
        _selectedMemeId = null;

        RuntimeLog.Info("MEME", $"Removed '{name}'.");
        _memeCaretSticky = false;
        InvalidateMemeTimelines();
        RedrawTimeline();
        UpdateMemeButtonsState();
        _ = RefreshMemePreviewAsync("Re-timing your video without that meme...");   // MEME_07
        Notify("Meme removed");
    }

    /// <summary>
    /// MEME_06 — how long this meme runs. A still image has no intrinsic length and is given
    /// <see cref="FortniteVideoSoftware.Core.Media.MemePlacement.StillImageDurationSec"/>; a video is
    /// probed. The timeline cannot be laid out without this number, because every position after
    /// the meme depends on it — which is why it is resolved here and not at export time.
    /// </summary>
    private async Task<double> ResolveMemeDurationAsync(MemeItem item)
    {
        if (item.IsImage) return FortniteVideoSoftware.Core.Media.MemePlacement.StillImageDurationSec;

        try
        {
            string ffprobe = FortniteVideoSoftware.Core.Infrastructure.BinaryPathResolver.Resolve(
                "ffprobe.exe", "backend", "binaries");
            var prober = new FortniteVideoSoftware.Core.Media.MediaProber(ffprobe, item.FullPath);
            double probed = await prober.GetDurationAsync();
            return probed > 0.01 ? probed : 0.0;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Could not read the length of '{System.IO.Path.GetFileName(item.FullPath)}': {ex.Message}");
            return 0.0;
        }
    }

    /// <summary>
    /// MEME_06 — refuses a placement that would land within <see cref="MemeMinSeparationOutSec"/>
    /// of another meme.
    ///
    /// ⚠️ THIS IS NOT COSMETIC. ProcessWorker merges cuts closer than 0.05s onto the earlier one,
    /// because an empty trim piece makes the whole filter graph fail to configure. Left to itself
    /// that merge is silent: the user places two memes a few frames apart and the export quietly
    /// plays them back to back at one seam. Blocking it here keeps the model honest and the
    /// explanation in front of the person who can act on it.
    /// </summary>
    private bool MemeSeparationIsSafe(double snappedSourceRelSec, string? ignoreId, out string? reason)
    {
        foreach (var m in _memes)
        {
            if (ignoreId != null && m.Id == ignoreId) continue;
            if (Math.Abs(m.AtSourceSecRelative - snappedSourceRelSec) < MemeMinSeparationOutSec)
            {
                reason = $"That is too close to '{System.IO.Path.GetFileName(m.FilePath)}'. Leave at least a moment between two memes.";
                return false;
            }
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// MEME_06 — moves an existing meme to a new SOURCE point, snapping and validating exactly as
    /// placement does. Returns true when the model actually changed.
    /// </summary>
    private bool MoveMemeTo(string id, double rawSourceRelSec)
    {
        int idx = _memes.FindIndex(m => m.Id == id);
        if (idx < 0) return false;

        // Snapped against the timeline WITHOUT this meme in it — see BaseTimeline's note. Using
        // OutTimeline here would ask a ruler that contains the block where the block should go.
        //
        // MEME_09 — ⚠️ NEAREST EDGE, NOT `SnapInsertionPoint` ALONE. A meme may not interrupt a
        // speed block (D8), and SnapInsertionPoint enforces that by returning the block's END for
        // ANY point inside it. That is right for placing a meme and WRONG for dragging one: drag
        // leftwards into a 30-second slow-mo block and the band is thrown 30 seconds to the RIGHT,
        // the opposite way to the hand holding it, then pins there for the block's whole width.
        // That is the "extremely stuck" behaviour. Snapping to whichever edge is NEARER means the
        // band stops at the boundary you are pushing against, which is what a person expects a
        // thing to do when it cannot go further.
        double snapped = BaseTimeline().SnapInsertionPoint(SnapMemeToNearestLegalEdge(rawSourceRelSec));

        if (Math.Abs(snapped - _memes[idx].AtSourceSecRelative) < 0.0005) return false;
        if (!MemeSeparationIsSafe(snapped, id, out _)) return false;

        _memes[idx] = _memes[idx] with { AtSourceSecRelative = snapped };
        return true;
    }

    /// <summary>
    /// MEME_09 — pulls a dragged meme OUT of any speed block by the SHORTEST route.
    ///
    /// <para>
    /// D8 forbids a meme interrupting a speed segment or a freeze, so a point inside one has to
    /// move. <see cref="OutputTimeline.SnapInsertionPoint"/> always moves it FORWARD, which is
    /// correct when placing a new meme (you asked for "here or later") and actively hostile when
    /// dragging an existing one (you are pushing it left and it leaps right).
    /// </para>
    /// <para>
    /// This returns the nearer of the blocking block's two edges, so the band comes to rest against
    /// the obstacle from whichever side you approached it. The result is still passed through
    /// <c>SnapInsertionPoint</c> afterwards — this only chooses a better candidate, it never
    /// bypasses the legality rule.
    /// </para>
    /// </summary>
    private double SnapMemeToNearestLegalEdge(double rawSourceRelSec)
    {
        double at = Math.Max(0, rawSourceRelSec);

        foreach (var seg in _segments)
        {
            double s0 = seg.StartMs / 1000.0;
            double s1 = seg.EndMs / 1000.0;
            if (at <= s0 + 0.0005 || at >= s1 - 0.0005) continue;

            // Inside this block. Leave by the closer door.
            double toStart = at - s0;
            double toEnd = s1 - at;
            _memeDragBlockedBy = seg;
            return toStart <= toEnd ? s0 : s1;
        }

        _memeDragBlockedBy = null;
        return at;
    }

    /// <summary>MEME_09 — the block currently refusing the dragged meme, for the status line.</summary>
    private FortniteVideoSoftware.Core.Media.SpeedSegment? _memeDragBlockedBy;

    /// <summary>
    /// MEME_06 — drops the LIVE timeline cache. Adding, moving or removing a meme changes where the
    /// block sits on the finished ruler, and that cache is keyed on a signature that includes the
    /// memes, so clearing it is what makes the ruler redraw correctly.
    ///
    /// <para>
    /// (DRAG_FIX) It deliberately does NOT touch <c>_baseTimeline</c>. BaseTimeline is built with
    /// <c>insertions: null</c> and its signature covers only duration, segments and cuts — a meme
    /// can never invalidate it. Clearing it anyway forced a full <c>OutputTimeline.Create</c> on
    /// every single pointer move of a drag (MoveMemeTo calls BaseTimeline for the snap), which is
    /// where the drag stutter came from.
    /// </para>
    /// </summary>
    private void InvalidateMemeTimelines()
    {
        _outTimeline = null; _outTimelineSig = "";
    }

    /// <summary>MEME_06 — REMOVE MEME appears only while a meme is selected, mirroring REMOVE ZOOM.</summary>
    private void UpdateMemeButtonsState()
    {
        var removeBtn = this.FindControl<Button>("RemoveMemeBtn");
        if (removeBtn != null) removeBtn.IsVisible = _selectedMemeId != null;
    }

    private FortniteVideoSoftware.Core.Media.OutputTimeline OutTimeline()
    {
        double durSec = Math.Max(0.001, GetDuration());
        var segs = new System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.SpeedSegment>(_segments);
        if (_freezeTimeMs >= 0 && _freezeDurationS > 0)
        {
            double relStart = _freezeTimeMs - _trimStartMs;
            segs.Add(new FortniteVideoSoftware.Core.Media.SpeedSegment(
                relStart, relStart + _freezeDurationS * 1000.0, 0.0));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(durSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        foreach (var s in segs)
            sb.Append(s.StartMs).Append(',').Append(s.EndMs).Append(',').Append(s.Speed).Append(';');
        // CUT_02 — the cuts MUST be part of the cache signature. Without them the ruler would keep
        // serving the pre-cut timeline until some unrelated edit changed the signature, and the
        // video would silently be shorter than the timeline claimed.
        foreach (var c in _cuts) sb.Append('X').Append(c.StartMs).Append(',').Append(c.EndMs).Append(';');
        // MEME_06 — memes belong in the signature for exactly the reason CUT_02 gives above: without
        // them the ruler would keep serving the pre-meme timeline until some unrelated edit changed
        // the signature, and the finished video would be LONGER than the timeline claimed.
        foreach (var m in _memes) sb.Append('M').Append(m.Id).Append(',')
            .Append(m.AtSourceSecRelative.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
            .Append(m.DurationSec.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        string sig = sb.ToString();

        if (_outTimeline == null || sig != _outTimelineSig)
        {
            _outTimeline = FortniteVideoSoftware.Core.Media.OutputTimeline.Create(
                durSec * 1000.0, segs, 1.0, 0, InsertionsForTimeline(), CutsForTimeline());
            _outTimelineSig = sig;
        }
        return _outTimeline;
    }

    /// <summary>
    /// MEME_06 — the meme list in the shape OutputTimeline wants.
    ///
    /// ⚠️ BaseTimeline() deliberately does NOT call this. Dragging a meme inside a timeline that
    /// contains that meme is the same trap FREEZE_DRAG documents: inside the block every output
    /// pixel answers "which gameplay moment is here" with the SAME instant, so the block pins
    /// itself and will not move. The drag arithmetic runs against BaseTimeline, where memes have
    /// zero width and the answer is stable for the whole gesture.
    /// </summary>
    private System.Collections.Generic.List<FortniteVideoSoftware.Core.Media.OutputTimeline.Insertion> InsertionsForTimeline()
        => FortniteVideoSoftware.Core.Media.MemePlacement.ToInsertions(_memes);

    /// <summary>Length of the FINISHED video in seconds - what the ruler is drawn against.</summary>
    private double OutDurationSec() => Math.Max(0.001, OutTimeline().TotalOutputSeconds);

    private FortniteVideoSoftware.Core.Media.OutputTimeline? _baseTimeline;
    private string _baseTimelineSig = "";

    /// <summary>
    /// FREEZE_DRAG — the timeline WITHOUT the freeze spliced in.
    ///
    /// <para>
    /// Dragging the freeze cannot be done in the timeline the freeze is part of, because that
    /// timeline moves as you drag it. Ask "which gameplay moment is under this pixel" of a ruler
    /// that already contains the hold and, inside the hold, every pixel answers with the SAME
    /// instant — the frozen one — so the freeze pins itself in place and will not move. Worse,
    /// as the duration changes under a resize, every position past the hold shifts, so the pointer
    /// and the edge it is dragging chase each other.
    /// </para>
    /// <para>
    /// This timeline holds only the speed segments, so it is fixed for the whole gesture and the
    /// hold occupies zero width in it. That makes the drag arithmetic simple and, more importantly,
    /// stable: the answer to "where did the user point" does not depend on the edit in progress.
    /// </para>
    /// </summary>
    private FortniteVideoSoftware.Core.Media.OutputTimeline BaseTimeline()
    {
        double durSec = Math.Max(0.001, GetDuration());

        var sb = new System.Text.StringBuilder();
        sb.Append(durSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        foreach (var s in _segments)
            sb.Append(s.StartMs).Append(',').Append(s.EndMs).Append(',').Append(s.Speed).Append(';');
        // CUT_02 — see OutTimeline: cuts belong in the signature and in the model.
        foreach (var c in _cuts) sb.Append('X').Append(c.StartMs).Append(',').Append(c.EndMs).Append(';');
        string sig = sb.ToString();

        if (_baseTimeline == null || sig != _baseTimelineSig)
        {
            _baseTimeline = FortniteVideoSoftware.Core.Media.OutputTimeline.Create(
                durSec * 1000.0, _segments, 1.0, 0, null, CutsForTimeline());
            _baseTimelineSig = sig;
        }
        return _baseTimeline;
    }

    /// <summary>
    /// FREEZE_DRAG — an X on the output-time canvas -> seconds on the FREEZE-FREE timeline.
    ///
    /// <para>
    /// Pixels before the hold pass through untouched; pixels inside it collapse onto its start; and
    /// pixels after it shift back by the hold, because that much of the ruler is time the freeze
    /// itself inserted. The result is "where would this pixel be if the freeze did not exist",
    /// which is the only frame of reference in which moving the freeze is a well-posed question.
    /// </para>
    /// </summary>
    private double OutXToBaseOutSec(double x, double w)
    {
        if (w <= 0) return 0;
        double outSec = Math.Clamp((x / w) * OutDurationSec(), 0, OutDurationSec());
        if (_freezeTimeMs < 0 || _freezeDurationS <= 0) return outSec;

        double holdStart = FreezeHoldStartOutSec();
        if (outSec <= holdStart) return outSec;
        return Math.Max(holdStart, outSec - Math.Min(_freezeDurationS, outSec - holdStart));
    }

    /// <summary>FREEZE_DRAG — an X on the output-time canvas -> seconds on the FULL ruler.</summary>
    private double OutXToOutSec(double x, double w)
        => w <= 0 ? 0 : Math.Clamp((x / w) * OutDurationSec(), 0, OutDurationSec());

    /// <summary>
    /// FREEZE_DRAG — commits a new hold START, expressed on the freeze-free timeline, back into the
    /// SOURCE instant the rest of the app stores.
    /// </summary>
    private void SetFreezeStartFromBaseOutSec(double baseOutSec)
    {
        double relSec = BaseTimeline().OutputToSourceRelative(
            Math.Clamp(baseOutSec, 0, BaseTimeline().TotalOutputSeconds));
        PushUndo("move freeze", "freeze-drag");   // UNDO_02
        _freezeTimeMs = _trimStartMs + relSec * 1000.0;
    }

    /// <summary>TRIM-RELATIVE source ms -> an X pixel on the output-time canvas.</summary>
    private double SrcMsToX(double srcRelMs, double w)
        => (OutTimeline().SourceToOutput(srcRelMs / 1000.0) / OutDurationSec()) * w;

    /// <summary>An X pixel on the output-time canvas -> TRIM-RELATIVE source ms.</summary>
    private double XToSrcMs(double x, double w)
    {
        if (w <= 0) return 0;
        double outSec = Math.Clamp(x, 0, w) / w * OutDurationSec();
        return OutTimeline().OutputToSourceRelative(outSec) * 1000.0;
    }

    private double GetDuration()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double fullDur = _videoHost.IpcClient.Duration;
        double trimEndSec = (_trimEndMs > 0) ? _trimEndMs / 1000.0 : fullDur;
        double trimDur = Math.Max(0.1, trimEndSec - (_trimStartMs / 1000.0));

        return trimDur;
    }

    private static string FormatMs(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    /// <summary>
    /// SHORT CLOCK — the format used by everything the user READS on screen in this window:
    /// the segment list, both ends of the timeline axis, and the ruler tick labels.
    ///
    /// Rule: <c>MM:SS</c>, escalating to <c>HH:MM:SS</c> only when the video is genuinely an hour
    /// or longer. Never milliseconds. A gameplay clip is seconds long, so "00:00:04.963" spent
    /// most of its width showing two zeros and a decimal nobody can act on, and it forced the
    /// segment rows onto two lines.
    ///
    /// ⚠️ THIS IS NOT A REPLACEMENT FOR <see cref="FormatMs"/>. That one keeps millisecond
    /// precision and is still what status messages and every RuntimeLog line use, because a
    /// millisecond-accurate boundary is exactly what you need when diagnosing a segment/export
    /// mismatch. Do not "unify" them — display and diagnostics want different things.
    /// </summary>
    private static string FormatClock(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// Returns the timeline overlay color for a speed segment, based on its speed
    /// relative to the base (natural) speed:
    ///   • Freeze (≈0x)    → blue
    ///   • Below base speed → red
    ///   • ≥ base speed     → green
    /// The color is independent of selection state so that live edits recolor
    /// immediately even while a segment is highlighted.
    /// </summary>
    private Avalonia.Media.Color GetSegmentOverlayColor(SpeedSegment seg)
    {
        double speed = seg.Speed;
        double baseSpd = _baseSpeed;
        
        if (speed < 0.01)
        {
            return Avalonia.Media.Color.FromArgb(230, 96, 165, 250);
        }
        else if (speed < baseSpd - 0.0001)
        {
            double factor = Math.Clamp((baseSpd - speed) / Math.Max(0.001, baseSpd - 0.1), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            // TONE_01: the RED half of the speed ramp. The alpha still encodes "how far below
            // base speed", so only the HUE moves to the token — the intensity maths is untouched.
            var slow = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Avalonia.Media.Color.FromRgb(168, 50, 50));
            return Avalonia.Media.Color.FromArgb(alpha, slow.R, slow.G, slow.B);
        }
        else
        {
            double factor = Math.Clamp((speed - baseSpd) / Math.Max(0.001, 4.1 - baseSpd), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            // TONE_01: the GREEN half of the same ramp.
            var fast = Infrastructure.ThemeResources.Colour(this, "AppSuccessColor", Avalonia.Media.Color.FromRgb(63, 156, 107));
            return Avalonia.Media.Color.FromArgb(alpha, fast.R, fast.G, fast.B);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // CUT_02 — DELETE PARTS. Removes the marked stretch from the video entirely.
    //
    // Moved here from the Main Screen because this window already owns MARK START / MARK END and
    // the timeline that has to condense afterwards. The heavy lifting is all in OutputTimeline and
    // GranularSpeedBuilder, which already splice the timeline for slow-motion, freezes and memes;
    // a cut is simply the chunk kind that consumes source time and occupies NO output time.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CUT_02 — the marked range to act on, TRIM-RELATIVE ms, or null when nothing is marked.
    ///
    /// Two ways to have a selection, and both are honoured: a committed block the user clicked, or
    /// a live MARK START + MARK END pair not yet turned into one. Reading both is what stops
    /// DELETE PARTS from lecturing a user who has visibly marked a range.
    /// </summary>
    private (double startMs, double endMs)? CurrentMarkedRange()
    {
        if (_pendingStartMs >= 0 && _pendingEndMs >= 0 && _pendingEndMs > _pendingStartMs)
            return (_pendingStartMs, _pendingEndMs);

        if (_selectedSegmentIndex >= 0 && _selectedSegmentIndex < _segments.Count)
        {
            var seg = _segments[_selectedSegmentIndex];
            if (seg.EndMs > seg.StartMs) return (seg.StartMs, seg.EndMs);
        }

        return null;
    }

    /// <summary>
    /// GUIDE_01 — THE DUMMY-PROOF PATH. Shown when an action that needs a marked range is pressed
    /// without one.
    ///
    /// A short red warning first, then a ONE SECOND pause so the user actually reads it, then the
    /// walkthrough: the app dims, a ghost cursor presses MARK START, presses PLAY, sweeps the
    /// timeline as the video runs, and presses MARK END — the exact sequence they were missing.
    ///
    /// Drawn, not recorded. ISSUE_04 explains why the suite has no GIF assets: mandate #2 forbids
    /// shipping loose files beside the .exe, and a recording would go stale the moment a button
    /// moves or the font scale changes. CoachOverlay renders vector shapes over the window's own
    /// live controls, so it follows the real layout at any size, theme or scale, and costs nothing
    /// to ship. Returns true when it took over, so the caller aborts.
    /// </summary>
    private bool GuideWhenNothingMarked(string actionName)
    {
        if (CurrentMarkedRange() != null) return false;

        RuntimeLog.Info("GUIDE", $"{actionName} pressed with no marked range. Showing the MARK START / MARK END walkthrough.");
        NotifyError("You did not selected an area on time the timeline yet!");

        // The pause is the point: firing the walkthrough instantly buries the message it explains.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            try
            {
                Controls.CoachOverlay.PlayOnce(this, new List<Controls.CoachStep>
                {
                    new("First, mark where it starts",
                        "Move the video to where your section should BEGIN, then press MARK START.",
                        "MarkStartBtn", Controls.CoachGesture.Click),
                    new("Now play the video",
                        "Press PLAY and let it run to where your section should END.",
                        "GranularPlayPause", Controls.CoachGesture.Click),
                    new("Watch it play",
                        "The line sweeps along the timeline as the video plays. Stop when you reach the end of the bit you want.",
                        "GranularLanes", Controls.CoachGesture.DragHorizontal),
                    new("Then mark where it ends",
                        "Press MARK END. The stretch between your two marks is now selected — and THAT is what "
                        + actionName + " works on.",
                        "MarkEndBtn", Controls.CoachGesture.Click),
                });
            }
            catch (Exception ex) { RuntimeLog.Fail("GUIDE", ex); }
        };
        delay.Start();

        return true;
    }

    /// <summary>MEME_06 — ADD MEME / REMOVE MEME, wired alongside DELETE PARTS.</summary>
    private void WireMemeButtons()
    {
        var addBtn = this.FindControl<Button>("AddMemeBtn");
        if (addBtn != null) addBtn.Click += OnAddMemeClicked;

        var removeBtn = this.FindControl<Button>("RemoveMemeBtn");
        if (removeBtn != null) removeBtn.Click += OnRemoveMemeClicked;

        UpdateMemeButtonsState();
    }

    private void WireDeletePartsButton()
    {
        var btn = this.FindControl<Button>("DeletePartsBtn");
        if (btn == null) return;
        btn.AddHandler(Button.ClickEvent, (_, _) => OnDeletePartsClicked());
    }

    private async void OnDeletePartsClicked()
    {
        try
        {
            RuntimeLog.Info("CUT", "User clicked DELETE PARTS in the Granular Speed Editor.");

            if (GuideWhenNothingMarked("DELETE PARTS")) return;

            var range = CurrentMarkedRange()!.Value;
            double durMs = GetDuration() * 1000.0;

            double startMs = Math.Max(0, Math.Min(range.startMs, durMs));
            double endMs = Math.Max(0, Math.Min(range.endMs, durMs));

            var candidate = new List<FortniteVideoSoftware.Core.Media.CutRange>(_cuts)
            {
                new(startMs, endMs)
            };
            double survivingMs = SurvivingMsAfterCuts(candidate, durMs);

            RuntimeLog.Info("CUT",
                $"DELETE PARTS requested: {FormatMs(startMs)} -> {FormatMs(endMs)} " +
                $"({(endMs - startMs) / 1000.0:F2}s). Clip is {durMs / 1000.0:F2}s, " +
                $"{survivingMs / 1000.0:F2}s would survive across {candidate.Count} cut(s).");

            if (survivingMs < MinSurvivingMs)
            {
                RuntimeLog.Fail("CUT", $"DELETE PARTS refused — only {survivingMs:F0}ms would be left.");
                NotifyError("That would delete almost the whole video. At least half a second has to be left.");
                return;
            }

            if (FortniteVideoSoftware.App.Infrastructure.SettingsManager.Instance.ConfirmMainAppCut)
            {
                // DIALOG_01 — themed Avalonia dialog, not the Win32 MessageBox. It inherits the
                // app's fonts, colours and font scale, so it belongs to the editor it interrupts.
                // DIALOG_02 — destructive: DELETE IT is red, KEEP IT is green, Enter is KEEP IT.
                bool ok = await Controls.ConfirmDialogWindow.AskAsync(
                    this,
                    $"Delete this whole scene from the video?\n\n" +
                    $"{FormatMs(startMs)} to {FormatMs(endMs)}  —  {(endMs - startMs) / 1000.0:F1} seconds.\n\n" +
                    "The timeline closes up and the video gets shorter. Your original recording is not touched, " +
                    "and CLEAR ALL puts everything back.",
                    "Delete Entire Scene?",
                    yesText: "DELETE IT",
                    noText: "KEEP IT",
                    destructive: true);
                if (!ok)
                {
                    RuntimeLog.Info("CUT", "DELETE PARTS cancelled by the user at the confirmation.");
                    return;
                }
            }

            // UNDO_01 — one snapshot covers the cut AND the segment/freeze reconciliation that
            // follows, so a single Ctrl+Z puts the whole scene back exactly as it was.
            PushUndo("delete parts");
            _cuts.Add(new FortniteVideoSoftware.Core.Media.CutRange(startMs, endMs));
            NormalizeCutsInPlace(durMs);

            // CUT_03 — THE DELETED RANGE MUST STOP EXISTING AS A SEGMENT.
            // MARK END calls AddPendingSegment, so by the time DELETE PARTS runs the range the user
            // marked is already a committed speed block. Leaving it there made the right-hand list
            // show a block over footage that no longer exists, and made the editor's own state
            // disagree with the exported file. This wipes the deleted footage out of the segment
            // list and the freeze, then the timeline is rebuilt from what is actually left.
            ApplyCutToSegmentsAndFreeze(startMs, endMs);

            // The marks are spent — leaving them armed would invite deleting the same stretch twice.
            _pendingStartMs = -1;
            _pendingEndMs = -1;
            _selectedSegmentIndex = -1;

            // The ruler is drawn against OutTimeline(), which now has to know about the hole. Both
            // caches are keyed on a signature that includes the cuts, so clearing them is what
            // makes the timeline visibly condense on the next redraw.
            _outTimeline = null; _outTimelineSig = "";
            _baseTimeline = null; _baseTimelineSig = "";

            double removedSec = TotalCutSeconds();
            RuntimeLog.Success("CUT",
                $"DELETE PARTS applied. {_cuts.Count} cut(s) now removing {removedSec:F2}s total. " +
                $"{_segments.Count} speed segment(s) survive, freeze={(_freezeTimeMs >= 0 ? FormatMs(_freezeTimeMs - _trimStartMs) : "none")}. " +
                $"Finished video is about {OutDurationSec():F2}s. Timeline condensed and recalculated.");

            // CUT_03 — the voice-over needs no realignment here and that is BY DESIGN: takes are
            // stored in SOURCE time and converted at export through the same OutputTimeline the
            // ruler above now uses, so removing footage slides them automatically. What DOES change
            // is the finished length a take was recorded against, so the preview player is told to
            // re-read the timeline rather than keep a stale duration.
            _voiceOverPlayer.Reload();

            // CUT_03 — ⚠️ THE LIST, NOT JUST THE TIMELINE. ApplyCutToSegmentsAndFreeze already
            // removed the blocks from `_segments`, but without this the right-hand pane keeps
            // rendering the OLD rows, so deleted footage still looks like a live segment. A cut is
            // a gonner: it must leave no trace in the list.
            RefreshSegmentList();
            UpdateDeleteButtonVisibility();
            RedrawTimeline();
            SetStatus($"Scene deleted — {(endMs - startMs) / 1000.0:F1}s removed. Video is now about {OutDurationSec():F1}s.");
            NotifyUndoable($"Deleted {(endMs - startMs) / 1000.0:F1}s of video", "DeletePartsBtn");   // ANCHOR_01
        }
        catch (Exception ex) { RuntimeLog.Fail("CUT", ex); }
    }

    /// <summary>
    /// CUT_03 — removes deleted footage from the speed segments and the freeze.
    ///
    /// Everything here is in SOURCE time, which is what makes this simple: footage after a cut does
    /// NOT move, because OutputTimeline does the source-to-output mapping. Only blocks that overlap
    /// the hole need surgery, and there are exactly four cases:
    ///
    ///   fully inside the cut          -> deleted outright, it covers nothing that survives
    ///   starts before, ends inside    -> truncated to the cut's start
    ///   starts inside, ends after     -> moved forward to the cut's end
    ///   spans the whole cut           -> LEFT ALONE. Its source range still covers surviving
    ///                                    footage on both sides, and OutputTimeline already splits
    ///                                    it around the hole and applies the speed to both halves.
    ///                                    Shortening it here would double-count the removal.
    ///
    /// A survivor trimmed below <see cref="MinSegmentAfterCutMs"/> is dropped: a sliver of a speed
    /// block is not something the user chose, and each one costs a whole parallel branch at export.
    /// Every change is logged individually — after a cut the log alone should explain why a block
    /// the user created is no longer in the list.
    /// </summary>
    private void ApplyCutToSegmentsAndFreeze(double cutStartMs, double cutEndMs)
    {
        int removed = 0, trimmed = 0;

        for (int i = _segments.Count - 1; i >= 0; i--)
        {
            var seg = _segments[i];
            double ss = seg.StartMs, se = seg.EndMs;

            bool startsInside = ss >= cutStartMs - 0.5 && ss < cutEndMs - 0.5;
            bool endsInside = se > cutStartMs + 0.5 && se <= cutEndMs + 0.5;

            if (startsInside && endsInside)
            {
                RuntimeLog.Info("CUT",
                    $"  segment #{i + 1} [{FormatMs(ss)}-{FormatMs(se)}] {seg.Speed:0.00}x was entirely inside the "
                    + "deleted scene — removed.");
                _segments.RemoveAt(i);
                removed++;
                continue;
            }

            if (!startsInside && endsInside)
            {
                if (cutStartMs - ss < MinSegmentAfterCutMs)
                {
                    RuntimeLog.Info("CUT",
                        $"  segment #{i + 1} [{FormatMs(ss)}-{FormatMs(se)}] would be left with only "
                        + $"{cutStartMs - ss:F0}ms — removed instead of leaving a sliver.");
                    _segments.RemoveAt(i);
                    removed++;
                }
                else
                {
                    _segments[i] = seg with { EndMs = cutStartMs };
                    RuntimeLog.Info("CUT", $"  segment #{i + 1} truncated to end at {FormatMs(cutStartMs)}.");
                    trimmed++;
                }
                continue;
            }

            if (startsInside && !endsInside)
            {
                if (se - cutEndMs < MinSegmentAfterCutMs)
                {
                    RuntimeLog.Info("CUT",
                        $"  segment #{i + 1} [{FormatMs(ss)}-{FormatMs(se)}] would be left with only "
                        + $"{se - cutEndMs:F0}ms — removed instead of leaving a sliver.");
                    _segments.RemoveAt(i);
                    removed++;
                }
                else
                {
                    _segments[i] = seg with { StartMs = cutEndMs };
                    RuntimeLog.Info("CUT", $"  segment #{i + 1} moved to start at {FormatMs(cutEndMs)}.");
                    trimmed++;
                }
            }
            // spans the cut entirely -> untouched, on purpose. See the remarks above.
        }

        // The freeze holds ONE frame. If that frame was deleted there is nothing left to hold, so
        // the freeze goes with it — the same rule OutputTimeline.Create applies when it builds the
        // chunk list, kept in step here so the UI and the export never disagree.
        if (_freezeTimeMs >= 0)
        {
            double freezeRel = _freezeTimeMs - _trimStartMs;
            if (freezeRel > cutStartMs - 0.5 && freezeRel < cutEndMs - 0.5)
            {
                RuntimeLog.Info("CUT",
                    $"  freeze at {FormatMs(freezeRel)} held a frame inside the deleted scene — cleared.");
                _freezeTimeMs = -1;
                _selectedFreezePresetS = -1.0;
            }
        }

        RuntimeLog.Info("CUT",
            $"Segment reconciliation done: {removed} removed, {trimmed} trimmed, {_segments.Count} remaining.");
    }

    /// <summary>
    /// CUT_03 — a speed block left shorter than this by a cut is dropped rather than kept.
    /// Below a fifth of a second nobody perceives a speed change, and every surviving block costs a
    /// parallel branch in the export graph (GranularSpeedBuilder.HighChunkCountWarnThreshold).
    /// </summary>
    private const double MinSegmentAfterCutMs = 200.0;

    // ══════════════════════════════════════════════════════════════════════════════════════
    // UNDO_01 — UNDO / REDO FOR THIS EDITOR.
    //
    // ⚠️ MEMORY IS THE WHOLE DESIGN PROBLEM HERE, SO READ THIS BEFORE CHANGING ANY OF IT.
    // A naive "snapshot everything on every change" undo in a window that fires on every slider
    // tick will grow without bound and hold references to disposed objects. Four rules keep it
    // bounded and safe, and all four matter:
    //
    //   (U1) A SNAPSHOT IS PLAIN DATA, NEVER A CONTROL OR A STREAM. It holds value types and an
    //        immutable array of SpeedSegment RECORDS. It never touches _videoHost, the IPC client,
    //        NAudio readers, bitmaps or canvases — so an old snapshot can never keep a disposed
    //        native handle alive, and can never resurrect one.
    //   (U2) HARD CAP, ENFORCED ON PUSH. MaxUndoDepth entries. The oldest is dropped the moment the
    //        cap is exceeded, so the list has a fixed ceiling no matter how long the window is open.
    //        At ~40 bytes per segment, 40 states of a heavy 25-segment project is roughly 40 KB —
    //        the ceiling, not a typical case.
    //   (U3) REDO IS TRUNCATED ON EVERY NEW EDIT. Editing after an undo drops the whole redo tail
    //        immediately. Without this, branch after branch accumulates and is unreachable forever.
    //   (U4) SNAPSHOTS ARE DEDUPED. PushUndo compares against the top of the stack and does nothing
    //        if the state is identical, so slider drags and repeated redraws cannot flood it.
    //
    // Restoring a snapshot deliberately does NOT touch the video position, the zoom overlay or the
    // playback state — only project data. Undo must never yank the playhead around.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// UNDO_01 — one restorable state. A readonly record struct of value types plus one immutable
    /// array: see rule (U1). Deliberately NOT a class holding live objects.
    /// </summary>
    private readonly record struct EditorSnapshot(
        ImmutableArray<SpeedSegment> Segments,
        ImmutableArray<FortniteVideoSoftware.Core.Media.CutRange> Cuts,
        ImmutableArray<FortniteVideoSoftware.Core.Media.MemePlacement> Memes,
        double BaseSpeed,
        double FreezeTimeMs,
        double FreezeDurationS,
        string Label,
        int SelectedIndex)
    {
        /// <summary>Value equality for (U4). The Label is excluded — it is only for the log.</summary>
        public bool SameStateAs(EditorSnapshot other)
        {
            if (Math.Abs(BaseSpeed - other.BaseSpeed) > 0.0001) return false;
            if (Math.Abs(FreezeTimeMs - other.FreezeTimeMs) > 0.5) return false;
            if (Math.Abs(FreezeDurationS - other.FreezeDurationS) > 0.0001) return false;
            if (Segments.Length != other.Segments.Length) return false;
            if (Cuts.Length != other.Cuts.Length) return false;
            if (Memes.Length != other.Memes.Length) return false;   // MEME_06
            for (int i = 0; i < Segments.Length; i++)
                if (!Segments[i].Equals(other.Segments[i])) return false;
            for (int i = 0; i < Cuts.Length; i++)
                if (!Cuts[i].Equals(other.Cuts[i])) return false;
            // MemePlacement is a record, so this compares path, anchor, duration AND id — which is
            // what makes a drag of half a pixel count as "no change" and not stack an undo step.
            for (int i = 0; i < Memes.Length; i++)
                if (!Memes[i].Equals(other.Memes[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// UNDO_01 (U2) — the hard ceiling. 40 steps is far more than anyone reaches in one session and
    /// keeps the worst case in tens of kilobytes. Raising this raises the memory ceiling linearly;
    /// removing it removes the ceiling entirely, which is the bug this constant exists to prevent.
    /// </summary>
    private const int MaxUndoDepth = 40;

    private readonly List<EditorSnapshot> _undoStack = new();
    private readonly List<EditorSnapshot> _redoStack = new();

    /// <summary>
    /// UNDO_02 (rules 2 and 3) — GESTURE COALESCING.
    ///
    /// A drag fires continuously and a speed wheel passes through dozens of values on the way from
    /// 1.0x to 0.5x. Snapshotting each one would mean two hundred presses of Ctrl+Z to undo a
    /// single drag — undo would technically work and be useless.
    ///
    /// A caller passes a coalesce key; the FIRST push of a burst is kept (it holds the state from
    /// before the gesture started, which is exactly what undo needs) and every later push with the
    /// same key inside the idle window is dropped. The window is measured from the LAST call, not
    /// the first, so a slow ten-second drag stays one entry. Releasing the pointer calls
    /// <see cref="EndUndoGesture"/>, which closes the burst immediately.
    /// </summary>
    private string _undoGestureKey = "";
    private DateTime _undoGestureAt = DateTime.MinValue;
    private const int UndoGestureIdleMs = 700;

    /// <summary>Guards against a restore re-entering PushUndo through the redraw it triggers.</summary>
    private bool _restoringSnapshot;

    private EditorSnapshot CaptureSnapshot(string label) => new(
        ImmutableArray.CreateRange(_segments),
        ImmutableArray.CreateRange(_cuts),
        ImmutableArray.CreateRange(_memes),   // MEME_06
        _baseSpeed, _freezeTimeMs, _freezeDurationS, label, _selectedSegmentIndex);

    /// <summary>
    /// UNDO_01 — records the state BEFORE a change. Call this at the TOP of any action that alters
    /// segments, cuts, base speed or the freeze; the label is what the tooltip and the log show.
    /// </summary>
    /// <summary>
    /// UNDO_02 — ends the current gesture, so the next change starts a new undo entry even if it
    /// carries the same coalesce key. Call from pointer-release handlers.
    /// </summary>
    private void EndUndoGesture()
    {
        _undoGestureKey = "";
        _undoGestureAt = DateTime.MinValue;
    }

    /// <param name="coalesceKey">
    /// UNDO_02 — non-null for CONTINUOUS controls (drags, wheels, spinners). Repeated pushes with
    /// the same key inside <see cref="UndoGestureIdleMs"/> collapse into the first one, so one
    /// gesture costs one Ctrl+Z. Leave null for discrete clicks.
    /// </param>
    private void PushUndo(string label, string? coalesceKey = null)
    {
        if (_restoringSnapshot) return;

        if (coalesceKey != null)
        {
            bool sameGesture = _undoGestureKey == coalesceKey
                               && (DateTime.UtcNow - _undoGestureAt).TotalMilliseconds < UndoGestureIdleMs;

            // Refresh the clock even when dropping, so the window tracks the LAST movement.
            _undoGestureAt = DateTime.UtcNow;
            if (sameGesture) return;

            _undoGestureKey = coalesceKey;
        }
        else
        {
            EndUndoGesture();
        }

        var snap = CaptureSnapshot(label);

        // (U4) nothing actually changed since the last push — do not grow the stack.
        if (_undoStack.Count > 0 && _undoStack[^1].SameStateAs(snap)) return;

        _undoStack.Add(snap);

        // (U2) enforce the ceiling on push, so the list can never exceed it even briefly.
        while (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);

        // (U3) a new edit invalidates every redo branch.
        if (_redoStack.Count > 0)
        {
            RuntimeLog.Info("UNDO", $"New edit after undo — discarding {_redoStack.Count} redo state(s).");
            _redoStack.Clear();
        }

        RefreshUndoRedoButtons();
    }

    /// <summary>
    /// UNDO_02 (rule 7) — plain-English description of what a restore actually does, worked out by
    /// DIFFING the two states rather than from a hand-written string per call site. "Undid change
    /// speed" tells the user nothing; "Speed back to 1.0x" tells them the result. Self-maintaining:
    /// a new undoable action gets a sensible sentence without touching this method.
    /// </summary>
    private static string DescribeRestore(EditorSnapshot from, EditorSnapshot to)
    {
        int segDelta = to.Segments.Length - from.Segments.Length;
        if (segDelta > 0)
            return segDelta == 1 ? "Brought back 1 segment" : $"Brought back {segDelta} segments";
        if (segDelta < 0)
            return segDelta == -1 ? "Removed the segment again" : $"Removed {-segDelta} segments again";

        double cutDelta = 0;
        foreach (var c in to.Cuts) cutDelta += c.EndMs - c.StartMs;
        foreach (var c in from.Cuts) cutDelta -= c.EndMs - c.StartMs;
        if (cutDelta > 1) return $"Put back the {cutDelta / 1000.0:0.0}s you deleted";
        if (cutDelta < -1) return $"Deleted {-cutDelta / 1000.0:0.0}s again";

        if (Math.Abs(to.BaseSpeed - from.BaseSpeed) > 0.001)
            return $"Overall speed back to {to.BaseSpeed:0.0}x";

        if (to.FreezeTimeMs < 0 && from.FreezeTimeMs >= 0) return "Removed the frozen frame";
        if (to.FreezeTimeMs >= 0 && from.FreezeTimeMs < 0) return "Brought back the frozen frame";
        if (Math.Abs(to.FreezeDurationS - from.FreezeDurationS) > 0.005)
            return $"Freeze back to {to.FreezeDurationS:0.0}s";

        // Same count on both sides: something INSIDE a segment changed. Name it.
        for (int i = 0; i < to.Segments.Length && i < from.Segments.Length; i++)
        {
            var a = from.Segments[i];
            var b = to.Segments[i];
            if (Math.Abs(a.Speed - b.Speed) > 0.001) return $"Speed back to {b.Speed:0.0}x";
            if (Math.Abs(a.StartMs - b.StartMs) > 0.5 || Math.Abs(a.EndMs - b.EndMs) > 0.5)
                return "Segment back where it was";
            if (a.ZoomW.HasValue != b.ZoomW.HasValue)
                return b.ZoomW.HasValue ? "Brought back the zoom" : "Removed the zoom";
            if (a.ZoomW != b.ZoomW || a.ZoomX != b.ZoomX || a.ZoomY != b.ZoomY || a.ZoomH != b.ZoomH)
                return "Zoom box back where it was";
            if (a.ZoomSlow != b.ZoomSlow) return b.ZoomSlow ? "Zoom back to slow" : "Zoom back to instant";
        }

        return $"Undid {to.Label}";
    }

    private void PerformUndo()
    {
        if (_undoStack.Count == 0)
        {
            ShowUndoNotice("Nothing left to undo", "UndoBtn");
            return;
        }

        var previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        // The CURRENT state becomes the redo entry, so redo is exact rather than reconstructed.
        var current = CaptureSnapshot(previous.Label);
        _redoStack.Add(current);
        while (_redoStack.Count > MaxUndoDepth) _redoStack.RemoveAt(0);

        RuntimeLog.Info("UNDO",
            $"UNDO '{previous.Label}': segments {current.Segments.Length} -> {previous.Segments.Length}, " +
            $"cuts {current.Cuts.Length} -> {previous.Cuts.Length}, " +
            $"base {current.BaseSpeed:0.00}x -> {previous.BaseSpeed:0.00}x. " +
            $"Depth now undo={_undoStack.Count} redo={_redoStack.Count}.");

        RestoreSnapshot(previous);
        // UNDOHINT_01 / UNDO_02 (rules 6+7) — say what came BACK, beside the button that did it.
        ShowUndoNotice($"{DescribeRestore(current, previous)} \u2014 Ctrl+Y to redo", "UndoBtn");
    }

    private void PerformRedo()
    {
        if (_redoStack.Count == 0)
        {
            ShowUndoNotice("Nothing left to redo", "RedoBtn");
            return;
        }

        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        _undoStack.Add(CaptureSnapshot(next.Label));
        while (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);

        RuntimeLog.Info("UNDO",
            $"REDO '{next.Label}': back to {next.Segments.Length} segment(s), {next.Cuts.Length} cut(s). " +
            $"Depth now undo={_undoStack.Count} redo={_redoStack.Count}.");

        RestoreSnapshot(next);
        ShowUndoNotice($"{DescribeRestore(_undoStack[^1], next)} \u2014 Ctrl+Z to undo again", "RedoBtn");
    }

    /// <summary>
    /// UNDO_01 — puts project data back. Touches ONLY project data: never the playhead, never the
    /// zoom overlay, never playback. The timeline caches are invalidated by hand because their
    /// signatures are built from exactly the fields this replaces.
    /// </summary>
    private void RestoreSnapshot(EditorSnapshot snap)
    {
        _restoringSnapshot = true;
        try
        {
            _segments.Clear();
            _segments.AddRange(snap.Segments);

            _cuts.Clear();
            _cuts.AddRange(snap.Cuts);

            // MEME_06 — restore the memes, and drop a selection pointing at one that no longer
            // exists. Leaving a stale id selected would leave REMOVE MEME on screen with nothing
            // behind it.
            _memes.Clear();
            _memes.AddRange(snap.Memes);
            if (_selectedMemeId != null && !_memes.Any(m => m.Id == _selectedMemeId))
                _selectedMemeId = null;
            InvalidateMemeTimelines();

            // MEME_07 — an undo that moves, adds or removes a meme changes the preview just as
            // much as making the edit did, so it goes through the same rebuild stall.
            _memeCaretSticky = false;
            _ = RefreshMemePreviewAsync("Re-timing your video after the undo...");

            _baseSpeed = snap.BaseSpeed;
            _freezeTimeMs = snap.FreezeTimeMs;
            _freezeDurationS = snap.FreezeDurationS;

            // UNDO_02 (rule 4) — KEEP THE SELECTION. This used to blank it, so an undo also
            // emptied the side panel and felt like more had been taken back than was asked for.
            // The snapshot carries the selection from before the change; clamped in case the list
            // it pointed into is now shorter.
            _selectedSegmentIndex = (snap.SelectedIndex >= 0 && snap.SelectedIndex < _segments.Count)
                ? snap.SelectedIndex
                : -1;
            _pendingStartMs = -1;
            _pendingEndMs = -1;

            _outTimeline = null; _outTimelineSig = "";
            _baseTimeline = null; _baseTimelineSig = "";

            if (_zoomModeActive) ExitZoomMode();

            // UNDO_01 — same reason as CUT_03: restoring `_segments` is not visible until the
            // pane is rebuilt from it.
            RefreshSegmentList();
            UpdateDeleteButtonVisibility();
            RedrawTimeline();
            RefreshUndoRedoButtons();
        }
        catch (Exception ex) { RuntimeLog.Fail("UNDO", ex); }
        finally { _restoringSnapshot = false; }
    }

    /// <summary>
    /// UNDOHINT_01 — the standing prompt under the UNDO / REDO pair.
    ///
    /// It always names the SPECIFIC action the shortcut would take back or put back, so the key and
    /// its consequence are read together, right beside the buttons that do the same thing. Redo is
    /// offered first when a redo is pending, because that is the state the user just created by
    /// pressing Ctrl+Z and it is the thing they are most likely to want next.
    /// </summary>
    private void RefreshUndoHintText()
    {
        var hint = this.FindControl<TextBlock>("UndoHintText");
        if (hint == null) return;

        // UNDO_02 (rule 5) — SHOW BOTH WHEN BOTH ARE POSSIBLE. This used to switch entirely to the
        // redo wording the moment a redo existed, so after one Ctrl+Z it stopped mentioning undo
        // even with ten more undos still available — it was telling the user the wrong key.
        bool canUndo = _undoStack.Count > 0;
        bool canRedo = _redoStack.Count > 0;

        if (canUndo && canRedo)
            hint.Text = $"Ctrl+Z: undo \u201c{_undoStack[^1].Label}\u201d   \u00b7   Ctrl+Y: redo \u201c{_redoStack[^1].Label}\u201d";
        else if (canUndo)
            hint.Text = $"Press Ctrl + Z to undo \u201c{_undoStack[^1].Label}\u201d";
        else if (canRedo)
            hint.Text = $"Press Ctrl + Y to redo \u201c{_redoStack[^1].Label}\u201d";
        else
            hint.Text = "Every change can be taken back with Ctrl+Z";
    }

    /// <summary>
    /// UNDOHINT_01 — announces a change AND teaches the shortcut in the same breath. Every action
    /// that pushes an undo state should report through here rather than calling ShowFeedback
    /// directly, so the offer to undo is never missing from a step that can be undone.
    /// </summary>
    private void NotifyUndoable(string what, string? anchorName = null)
    {
        ShowUndoNotice($"{what} \u2014 press Ctrl + Z to undo", anchorName);
        RefreshUndoHintText();
    }

    /// <summary>
    /// ANCHOR_01 / UNDO_02 (rule 6) — floats an undo message NEXT TO the control that caused it.
    ///
    /// The eye is on the button that was just pressed, so that is where the words belong. Falls
    /// back to the UNDO button itself (the thing the message is telling you to use) and, failing
    /// that, to the centred notice — a message must never be lost because a control could not be
    /// found or is off-screen.
    /// </summary>
    private void ShowUndoNotice(string text, string? anchorName = null)
    {
        Control? anchor = null;
        if (anchorName != null) anchor = this.FindControl<Control>(anchorName);
        anchor ??= this.FindControl<Button>("UndoBtn");

        Controls.FloatingNotice.ShowAt(this, anchor, text, Controls.NoticeKind.Success);
    }

    private void RefreshUndoRedoButtons()
    {
        RefreshUndoHintText();

        var u = this.FindControl<Button>("UndoBtn");
        var r = this.FindControl<Button>("RedoBtn");
        if (u != null)
        {
            u.IsEnabled = _undoStack.Count > 0;
            // UNDO_02 (rule 8) — name the action so hovering answers "what will this take back?"
            ToolTip.SetTip(u, _undoStack.Count > 0
                ? $"Undo \u201c{_undoStack[^1].Label}\u201d  (Ctrl+Z)"
                : "Nothing to undo yet (Ctrl+Z)");
        }
        if (r != null)
        {
            r.IsEnabled = _redoStack.Count > 0;
            ToolTip.SetTip(r, _redoStack.Count > 0
                ? $"Redo \u201c{_redoStack[^1].Label}\u201d  (Ctrl+Y)"
                : "Nothing to redo (Ctrl+Y)");
        }
    }

    private void WireUndoRedo()
    {
        var u = this.FindControl<Button>("UndoBtn");
        if (u != null) u.AddHandler(Button.ClickEvent, (_, _) => PerformUndo());

        var r = this.FindControl<Button>("RedoBtn");
        if (r != null) r.AddHandler(Button.ClickEvent, (_, _) => PerformRedo());

        // Ctrl+Z / Ctrl+Y. Tunnel so the shortcut works wherever focus happens to be, and marked
        // Handled so a focused text field cannot also act on it.
        this.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Control) return;
            if (e.Key == Key.Z) { PerformUndo(); e.Handled = true; }
            else if (e.Key == Key.Y) { PerformRedo(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        RefreshUndoRedoButtons();
    }

    /// <summary>
    /// UNDO_01 — drops both stacks. Called when the window closes so no snapshot outlives the
    /// editor, and after APPLY, where the project has been handed to the Main App and undoing into
    /// a state that was never exported would be misleading.
    /// </summary>
    private void ClearUndoHistory(string why)
    {
        if (_undoStack.Count == 0 && _redoStack.Count == 0) return;
        RuntimeLog.Info("UNDO", $"Clearing history ({why}): {_undoStack.Count} undo, {_redoStack.Count} redo.");
        _undoStack.Clear();
        _redoStack.Clear();
        RefreshUndoRedoButtons();
    }

    /// <summary>CUT_02 — minimum surviving footage, in ms. See MainWindow's identical guard.</summary>
    private const double MinSurvivingMs = 500.0;

    /// <summary>Runs the export's own normalisation so this window can never show a cut the export would not make.</summary>
    private void NormalizeCutsInPlace(double durMs)
    {
        var rel = _cuts
            .Select(c => new FortniteVideoSoftware.Core.Media.OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0))
            .ToList();
        var norm = FortniteVideoSoftware.Core.Media.OutputTimeline.NormalizeCuts(rel, durMs / 1000.0);

        _cuts.Clear();
        foreach (var c in norm)
            _cuts.Add(new FortniteVideoSoftware.Core.Media.CutRange(c.StartSec * 1000.0, c.EndSec * 1000.0));
    }

    private static double SurvivingMsAfterCuts(
        IReadOnlyList<FortniteVideoSoftware.Core.Media.CutRange> cuts, double durMs)
    {
        var rel = cuts
            .Select(c => new FortniteVideoSoftware.Core.Media.OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0))
            .ToList();
        var norm = FortniteVideoSoftware.Core.Media.OutputTimeline.NormalizeCuts(rel, durMs / 1000.0);

        double removed = 0;
        foreach (var c in norm) removed += c.LengthSec * 1000.0;
        return Math.Max(0, durMs - removed);
    }

    private double TotalCutSeconds()
    {
        double t = 0;
        foreach (var c in _cuts) t += (c.EndMs - c.StartMs) / 1000.0;
        return t;
    }

    /// <summary>CUT_02 — the cut list in the units OutputTimeline wants: clip-relative SECONDS.</summary>
    private List<FortniteVideoSoftware.Core.Media.OutputTimeline.Cut> CutsForTimeline()
        => _cuts
            .Select(c => new FortniteVideoSoftware.Core.Media.OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0))
            .ToList();

    /// <summary>ISSUE_09 — the one suite-wide notice. See MainWindow.ShowTacticalFeedback.</summary>
    private void ShowFeedback(string text)
        => Controls.FloatingNotice.Show(this, text);

    /// <summary>ISSUE_09 — same notice in the "that worked" colour.</summary>
    private void ShowFeedbackSuccess(string text)
        => Controls.FloatingNotice.Success(this, text);

    private void SetStatus(string msg)
    {
        var lbl = this.FindControl<TextBlock>("BottomStatusLabel");
        if (lbl != null) lbl.Text = msg;
    }

    /// <summary>ISSUE_09 — status line + the suite-wide notice. Discrete events only.</summary>
    private void Notify(string msg)
    {
        SetStatus(msg);
        Controls.FloatingNotice.Success(this, msg);
    }

    /// <summary>ISSUE_09 — status line + the suite-wide notice, in red. Discrete rejections only.</summary>
    private void NotifyError(string msg)
    {
        SetStatus(msg);
        Controls.FloatingNotice.Error(this, msg);
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        try { _marchingAntsTimer?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _playbackTimer?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _freezePulseTimer?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _zoomTutorialTimer?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _thumbCts?.Cancel(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        DeleteThumbStrip();
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        FortniteVideoSoftware.App.WindowBoundsHelper.SaveBoundsSync(this, "GranularBounds");

        RuntimeLog.Info("Granular", "Granular Speed Editor closing. Stopping timers and saving bounds.");
        ClearLiveZoomCrop();
        _playbackTimer?.Stop();

        try { _videoHost?.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _videoHost = null;

        this.Hide();

        _isSafeToClose = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        // UNDO_01 — snapshots are plain data (rule U1), so they cannot pin a native handle, but the
        // lists still go here so nothing survives the window that owned them.
        ClearUndoHistory("editor closed");

        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        FortniteVideoSoftware.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        RuntimeLog.Info("Granular", "Granular Speed Editor closed. Disposing resources.");

        _playbackTimer?.Stop();
        _marchingAntsTimer?.Stop();
        _freezePulseTimer?.Stop();
        _zoomTutorialTimer?.Stop();
        // MEME_07 — the director only touches mpv through the host, which is disposed two lines
        // below; dropping the reference first is what guarantees no swap is in flight when it goes.
        _memePreview = null;
        _voiceOverPlayer.Dispose();
        _videoHost?.Dispose();
        _videoHost = null;
        base.OnClosed(e);
    }

    private void OnGlobalMasterVolumeChanged(int masterVolumePercentage)
    {
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("volume", masterVolumePercentage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            };
        }
    
}
}
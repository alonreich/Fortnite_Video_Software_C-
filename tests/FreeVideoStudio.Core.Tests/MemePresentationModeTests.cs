using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// MEMEMODE_01 — dual-mode memes. A FULL SCREEN meme interrupts the gameplay and adds its length;
/// a CORNER OVERLAY plays over the gameplay and adds ZERO output seconds (01 TL-MEME, 03 FFM-MEMECORNER,
/// 06 PROJ-SCHEMA, 07 UNDO-STATE).
/// </summary>
public class MemePresentationModeTests
{
    private const double ClipMs = 10_000;

    private static MemePlacement Inline(string id, double at, double dur = 4.0) => new($@"C:\memes\{id}.mp4", at, dur, id);

    private static MemePlacement Corner(string id, double at, double dur = 4.0,
        MemeOverlayCorner corner = MemeOverlayCorner.BottomRight, MemeOverlaySize size = MemeOverlaySize.Medium, bool sound = true)
        => new($@"C:\memes\{id}.mp4", at, dur, id, MemePresentationMode.CornerOverlay, corner, size, sound);

    private static double Total(params MemePlacement[] memes)
        => OutputTimeline.Create(ClipMs, null, 1.0, 0, MemePlacement.ToInsertions(memes)).TotalOutputSeconds;

    private static ProjectDocument Doc(params MemePlacement[] memes) => new()
    {
        Source = new SourceClip { FilePath = @"C:\clips\g.mp4", DurationMs = ClipMs },
        TrimmedDurationMs = ClipMs,
        Memes = memes.ToList(),
    };

    // ── the mandatory duration cases ────────────────────────────────────────────────────────

    [Fact] public void TenSecondsPlusFourSecondInline_IsFourteen() => Assert.Equal(14.0, Total(Inline("a", 5)), 6);

    [Fact] public void TenSecondsPlusFourSecondCorner_IsTen() => Assert.Equal(10.0, Total(Corner("a", 5)), 6);

    [Fact] public void TwoCornerMemes_AreTen() => Assert.Equal(10.0, Total(Corner("a", 2), Corner("b", 5)), 6);

    [Fact] public void InlinePlusCorner_IsFourteen() => Assert.Equal(14.0, Total(Inline("a", 2), Corner("b", 5)), 6);

    [Fact]
    public void InlineToCorner_RemovesFourSeconds()
    {
        var inline = Inline("a", 5);
        var corner = inline with { Mode = MemePresentationMode.CornerOverlay };
        Assert.Equal(-4.0, Total(corner) - Total(inline), 6);
        Assert.Equal(-4.0, Doc(corner).BuildTimeline().TotalOutputSeconds - Doc(inline).BuildTimeline().TotalOutputSeconds, 6);
    }

    [Fact]
    public void CornerToInline_AddsFourSeconds()
    {
        var corner = Corner("a", 5);
        var inline = corner with { Mode = MemePresentationMode.InlineFullScreen };
        Assert.Equal(4.0, Total(inline) - Total(corner), 6);
        Assert.Equal(4.0, Doc(inline).BuildTimeline().TotalOutputSeconds - Doc(corner).BuildTimeline().TotalOutputSeconds, 6);
    }

    [Fact]
    public void OutputDuration_IsTheMemeLengthInlineAndZeroInACorner()
    {
        Assert.Equal(4.0, Inline("a", 1).OutputDurationSec);
        Assert.Equal(0.0, Corner("a", 1).OutputDurationSec);
        Assert.Single(MemePlacement.InlineOnly(new[] { Inline("a", 1), Corner("b", 2) }));
        Assert.Single(MemePlacement.CornerOnly(new[] { Inline("a", 1), Corner("b", 2) }));
    }

    // ── nothing after a corner meme moves (music, voice-over, cuts, other memes) ────────────

    [Fact]
    public void EveryMomentAfterACornerMeme_MapsExactlyAsWithoutIt()
    {
        var cuts = new List<OutputTimeline.Cut> { new(7.0, 8.0) };
        var plain = OutputTimeline.Create(ClipMs, null, 1.0, 0, null, cuts);
        var withCorner = OutputTimeline.Create(ClipMs, null, 1.0, 0, MemePlacement.ToInsertions(new[] { Corner("a", 3) }), cuts);
        var withInline = OutputTimeline.Create(ClipMs, null, 1.0, 0, MemePlacement.ToInsertions(new[] { Inline("a", 3) }), cuts);

        foreach (double t in new[] { 0.0, 2.9, 3.0, 3.5, 5.0, 6.99, 8.0, 9.5, 10.0 })
        {
            Assert.Equal(plain.SourceToOutput(t), withCorner.SourceToOutput(t), 9);       // music / voice-over anchors
            if (t > 3.0) Assert.Equal(plain.SourceToOutput(t) + 4.0, withInline.SourceToOutput(t), 9);
        }
        Assert.Equal(plain.CutMarkerOutputPositions(), withCorner.CutMarkerOutputPositions());   // cuts
        Assert.Empty(withCorner.InsertionOutputRanges());
    }

    [Fact]
    public void MergerMusicByOutputTime_IgnoresCornerMemes_ButNotFullScreenOnes()
    {
        // MUSICMAP_01 — the Merger places music by body output time. A corner meme at 2 s must not move it.
        EdlClip Clip(params EdlMeme[] memes) => new()
        {
            Path = "a.mp4",
            DurationUs = 10_000_000,
            Effects = new EdlEffects { Memes = memes },
        };
        var plain = CompositeTimeline.Build(new MergeEdl { Clips = new[] { Clip() } });
        var corner = CompositeTimeline.Build(new MergeEdl { Clips = new[] { Clip(new EdlMeme("m", "x.mp4", EdlMemePlacement.Mid, 2_000_000, 4.0, MemePresentationMode.CornerOverlay)) } });
        var inline = CompositeTimeline.Build(new MergeEdl { Clips = new[] { Clip(new EdlMeme("m", "x.mp4", EdlMemePlacement.Mid, 2_000_000, 4.0)) } });

        Assert.Equal(10.0, corner.ClipsOutputSec, 3);
        Assert.Equal(14.0, inline.ClipsOutputSec, 3);
        foreach (double t in new[] { 1.0, 3.0, 6.0, 9.0 })
            Assert.Equal(plain.MergedSecToBodyOutputSec(t), corner.MergedSecToBodyOutputSec(t), 6);
        Assert.Equal(plain.MergedSecToBodyOutputSec(6.0) + 4.0, inline.MergedSecToBodyOutputSec(6.0), 6);
    }

    [Fact]
    public void ProcessWorker_RoutesCornerMemesAwayFromEveryTimeShiftingList()
    {
        // The music shift, the voice-over delay, the end-pad rule, the meme cuts and the timing tag
        // all read ProcessWorker's `memes` list; a corner meme must never reach it.
        string src = File.ReadAllText(Path.Combine(AudioTempoFilterBuilderTests.FindRepoRoot(), "src", "FreeVideoStudio.Core", "Media", "ProcessWorker.cs"));
        Assert.Contains("if (m.IsCorner) cornerMemes.Add(m);", src);
        Assert.Contains("else memes.Add(m);", src);
        Assert.Contains("memes.Select(m => (m.CutOutputSec, m.DurationSec))", src);   // the preview mix map: full screen only
    }

    // ── persistence: project, recovery (same serializer), editor session, Merger EDL ────────

    [Fact]
    public void ProjectFile_RoundTripsModeCornerSizeAndSound()
    {
        var doc = Doc(Inline("a", 1), Corner("b", 5, 2.5, MemeOverlayCorner.TopLeft, MemeOverlaySize.Large, sound: false));
        JsonObject json = ProjectSerializer.Write(doc);
        Assert.Equal(ProjectDocument.SchemaVersion, json["schema_version"]!.GetValue<int>());
        var back = ProjectSerializer.Read(json, out string? error);
        Assert.Null(error);
        Assert.Equal(doc.Memes, back!.Memes);
        Assert.Equal(doc, back);
    }

    [Fact]
    public void OldProject_WithoutTheKeys_ReadsAsFullScreenWithSound()
    {
        JsonObject json = ProjectSerializer.Write(Doc(Inline("a", 1)));
        json["schema_version"] = 3;
        foreach (var m in json["memes"]!.AsArray())
        {
            var o = m!.AsObject();
            o.Remove("mode"); o.Remove("corner"); o.Remove("size"); o.Remove("sound");
        }
        var back = ProjectSerializer.Read(json, out string? error);
        Assert.Null(error);
        var meme = Assert.Single(back!.Memes);
        Assert.Equal(MemePresentationMode.InlineFullScreen, meme.Mode);
        Assert.Equal(MemeOverlayCorner.BottomRight, meme.Corner);
        Assert.Equal(MemeOverlaySize.Medium, meme.Size);
        Assert.True(meme.PlaySound);
        Assert.Equal(14.0, back.BuildTimeline().TotalOutputSeconds, 6);
    }

    [Fact]
    public void RecoveryEnvelope_CarriesTheCornerMeme()
    {
        var doc = Doc(Corner("b", 5, 3.0, MemeOverlayCorner.TopRight, MemeOverlaySize.Small, sound: false));
        JsonObject json = ProjectSerializer.Write(doc);   // RECOVERYDOC_01 — the recovery file holds exactly this document
        var back = ProjectSerializer.Read(JsonNode.Parse(json.ToJsonString())!.AsObject(), out _);
        Assert.Equal(doc.Memes[0], back!.Memes[0]);
    }

    [Fact]
    public void PresentationJson_IsForgivingAboutJunk()
    {
        var o = new JsonObject { ["mode"] = 7, ["corner"] = "sideways", ["size"] = true, ["sound"] = "no" };
        var m = MemePresentationJson.Apply(o, Inline("a", 1));
        Assert.Equal(MemePresentationMode.InlineFullScreen, m.Mode);
        Assert.Equal(MemeOverlayCorner.BottomRight, m.Corner);
        Assert.Equal(MemeOverlaySize.Medium, m.Size);
        Assert.True(m.PlaySound);
    }

    [Fact]
    public void MergerEdl_RoundTripsTheFields_AndOldMemesAreFullScreen()
    {
        var meme = new EdlMeme("m1", "x.mp4", EdlMemePlacement.Mid, 2_000_000, 4.0, MemePresentationMode.CornerOverlay,
            MemeOverlayCorner.TopLeft, MemeOverlaySize.Small, false);
        var edl = new MergeEdl { Clips = new[] { new EdlClip { Path = "a.mp4", DurationUs = 10_000_000, Effects = new EdlEffects { Memes = new[] { meme } } } } };
        var back = MergeEdl.FromJson(edl.ToJson());
        Assert.Equal(edl, back);

        var json = JsonNode.Parse(edl.ToJson())!.AsObject();
        var mo = json["Clips"]![0]!["Effects"]!["Memes"]![0]!.AsObject();
        mo.Remove("Mode"); mo.Remove("Corner"); mo.Remove("Size"); mo.Remove("PlaySound");
        var old = MergeEdl.FromJson(json.ToJsonString())!.Clips[0].Effects.Memes[0];
        Assert.Equal(MemePresentationMode.InlineFullScreen, old.Mode);
        Assert.Equal(MemeOverlayCorner.BottomRight, old.Corner);
        Assert.Equal(MemeOverlaySize.Medium, old.Size);
        Assert.True(old.PlaySound);
    }

    [Fact]
    public void MergeEditorBridge_CarriesTheFieldsBothWays_AndNeverSnapsACorner()
    {
        var corner = new EdlMeme("m1", "x.mp4", EdlMemePlacement.Mid, 100_000, 4.0, MemePresentationMode.CornerOverlay,
            MemeOverlayCorner.TopRight, MemeOverlaySize.Large, false);
        var edl = new MergeEdl { Clips = new[] { new EdlClip { Path = "a.mp4", DurationUs = 10_000_000, Effects = new EdlEffects { Memes = new[] { corner } } } } };
        var source = MergeEditorSource.Build(CompositeTimeline.Build(edl));
        var state = source.ToEditor(edl);
        var m = Assert.Single(state.Memes);
        Assert.Equal((MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopRight, MemeOverlaySize.Large, false), (m.Mode, m.Corner, m.Size, m.PlaySound));
        var back = source.FromEditor(state, edl);
        var bm = Assert.Single(back.Clips[0].Effects.Memes);
        Assert.Equal(EdlMemePlacement.Mid, bm.Placement);   // 0.1 s in: a full-screen meme would snap AtStart
        Assert.Equal(corner with { AtUs = bm.AtUs }, bm);
        Assert.InRange(bm.AtUs, 99_000, 101_000);
    }

    // ── undo ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPropertyChange_IsAnUndoableStep()
    {
        var a = Doc(Corner("b", 5));
        var stack = new UndoStack<ProjectDocument>(a);
        var variants = new[]
        {
            a with { Memes = new[] { Corner("b", 5) with { Corner = MemeOverlayCorner.TopLeft } } },
            a with { Memes = new[] { Corner("b", 5) with { Corner = MemeOverlayCorner.TopLeft, Size = MemeOverlaySize.Large } } },
            a with { Memes = new[] { Corner("b", 5) with { Corner = MemeOverlayCorner.TopLeft, Size = MemeOverlaySize.Large, PlaySound = false } } },
            a with { Memes = new[] { Corner("b", 5) with { Corner = MemeOverlayCorner.TopLeft, Size = MemeOverlaySize.Large, PlaySound = false, Mode = MemePresentationMode.InlineFullScreen } } },
        };
        foreach (var v in variants)
        {
            Assert.NotEqual(stack.Current, v);
            stack.Apply(v, "change meme");
        }
        for (int i = variants.Length - 2; i >= 0; i--) Assert.Equal(variants[i], stack.Undo());
        Assert.Equal(a, stack.Undo());
    }

    // ── preview plan (Merger) ───────────────────────────────────────────────────────────────

    [Fact]
    public void MergerPreview_CornerMemesAreNeverCutaways_AndRunOnTheGameplayClock()
    {
        var memes = new[]
        {
            new EdlMeme("full", "f.mp4", EdlMemePlacement.Mid, 2_000_000, 4.0),
            new EdlMeme("corner", "c.mp4", EdlMemePlacement.Mid, 5_000_000, 3.0, MemePresentationMode.CornerOverlay),
        };
        var edl = new MergeEdl { Clips = new[] { new EdlClip { Path = "a.mp4", DurationUs = 10_000_000, Effects = new EdlEffects { Memes = memes } } } };
        var plan = MergerPreviewPlan.Build(CompositeTimeline.Build(edl));

        Assert.Equal("full", Assert.Single(plan.MemePlacements).Id);
        var span = Assert.Single(plan.CornerOverlays);
        Assert.Equal(5.0, span.GameStartSec, 3);     // gameplay clock: the full-screen 4 s hold is not counted
        Assert.Equal(8.0, span.GameEndSec, 3);
        Assert.Equal(5.0, plan.GameplaySecAt(5.0), 3);
        Assert.Equal(9.0, plan.OutputSecAt(5.0), 3);
        Assert.Equal(14.0, plan.TotalOutputSec, 3);
    }

    // ── export graph ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MergeClipGraph_CornerIsAnOverlay_FullScreenIsSpliced_LengthOnlyGrowsByFullScreen()
    {
        var fx = EdlEffects.None;
        const string Canvas = "scale=1920:1080,setsar=1";
        const string MemeCanvas = "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,format=yuv420p";
        var cornerOnly = MergeClipGraph.Build(0, "[0:v]", "[0:a]", 0, 10, fx with { Speed = new[] { new EdlSpeedSegment(0, 1_000_000, 1.0) } }, 1.0, Canvas, MemeCanvas,
            new[] { new MergeMemeInput(5, false, true, 4.0, 3.0, 0, MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft, MemeOverlaySize.Small, true) });
        Assert.Equal(10.0, cornerOnly.DurationSec, 2);
        string g = string.Join(";", cornerOnly.Filters);
        Assert.Contains("overlay=x=32:y=32:eof_action=pass:enable='between(t,3.0000,7.0000)'", g);
        Assert.Contains("amix=inputs=2:normalize=0:duration=first", g);
        Assert.DoesNotContain("concat=n=", g.Split("[c0_body_v]")[^1].Replace("v=1:a=0", ""));   // no meme splice after the body
        Assert.DoesNotContain("fx_v", cornerOnly.VideoLabel);

        var both = MergeClipGraph.Build(0, "[0:v]", "[0:a]", 0, 10, fx with { Speed = new[] { new EdlSpeedSegment(0, 1_000_000, 1.0) } }, 1.0, Canvas, MemeCanvas,
            new[]
            {
                new MergeMemeInput(5, false, true, 4.0, 3.0, 0, MemePresentationMode.CornerOverlay),
                new MergeMemeInput(6, true, false, 4.0, 6.0),
            });
        Assert.Equal(14.0, both.DurationSec, 2);
        Assert.Equal(2, both.MemeCount);
    }

    [Fact]
    public void CornerGraph_SoundOff_IsSilent_AndGeometryMatchesTheLayout()
    {
        var r = CornerMemeOverlayGraph.Build("[v]", "[a]",
            new[] { new CornerMemeInput(3, false, true, 1.0, 5.0, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, PlaySound: false) },
            1080, 1920, "60", "t_");
        Assert.Equal("[a]", r.AudioLabel);
        Assert.Contains("overlay=x=W-w-32:y=H-h-32", string.Join(";", r.Filters));
        Assert.Contains("scale=454:454:force_original_aspect_ratio=decrease", string.Join(";", r.Filters));

        // The preview's box is the export's box.
        var (x, y, w, h) = MemeOverlayLayout.Place(1080, 1920, 640, 360, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium);
        Assert.Equal((454, 254), (w, h));
        Assert.Equal((1080 - 32 - 454, 1920 - 32 - 254), (x, y));
        Assert.Null(MemePlacement.VisibleInterval(10.0, 4.0, 10.0));
        Assert.Equal((8.0, 10.0), MemePlacement.VisibleInterval(8.0, 4.0, 10.0));   // clipped: never lengthens the video
    }

    [MediaFact]
    public void RealFfmpeg_CornerOverlay_KeepsTheExactLength_AndShowsOnlyInsideItsWindow()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"fvs-corner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string meme = Path.Combine(dir, "meme.mkv");
            Run("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=red:s=640x360:r=30:d=4",
                "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000:duration=4", "-shortest", "-c:v", "ffv1", "-c:a", "pcm_s16le", meme);

            var r = CornerMemeOverlayGraph.Build("[0:v]", "[1:a]",
                new[] { new CornerMemeInput(2, false, true, 3.0, 7.0, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true) },
                1920, 1080, "60", "t_");
            string outFile = Path.Combine(dir, "out.mkv");
            Run(new[] { "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=blue:s=1920x1080:r=60:d=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=10",
                "-i", meme, "-filter_complex", string.Join(";", r.Filters),
                "-map", r.VideoLabel, "-map", r.AudioLabel, "-c:v", "ffv1", "-c:a", "pcm_s16le", outFile });

            byte[] Pixel(double t) => RunBytes("-v", "error", "-ss", t.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", outFile,
                "-frames:v", "1", "-vf", "crop=2:2:1700:900", "-f", "rawvideo", "-pix_fmt", "rgb24", "-");
            Assert.True(Pixel(1.0)[2] > 200 && Pixel(1.0)[0] < 50, "blue before the window");
            Assert.True(Pixel(5.0)[0] > 200 && Pixel(5.0)[2] < 50, "red meme inside the window");
            Assert.True(Pixel(8.0)[2] > 200 && Pixel(8.0)[0] < 50, "blue after the window");

            byte[] audio = RunBytes("-v", "error", "-i", outFile, "-map", "0:a", "-ac", "1", "-ar", "48000", "-f", "f32le", "-");
            Assert.InRange(audio.Length / 4 / 48000.0, 9.99, 10.01);   // zero added duration
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { /* temp folder; nothing to do */ } }
    }

    private static void Run(params string[] args)
    {
        var (exit, _, err) = Exec(args);
        Assert.True(exit == 0, err);
    }

    private static byte[] RunBytes(params string[] args) => Exec(args).stdout;

    private static (int exit, byte[] stdout, string err) Exec(string[] args)
    {
        var psi = new ProcessStartInfo(MediaFactAttribute.Ffmpeg!) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        return (p.ExitCode, ms.ToArray(), errTask.GetAwaiter().GetResult());
    }
}

// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// AI Tracking raw waypoint received from the Gemini vision protocol.
/// Coordinates are normalized to a 0-1000 scale relative to the frame.
/// </summary>
public record RawSubjectWaypoint
{
    [JsonPropertyName("time")]
    public double Time { get; init; }

    [JsonPropertyName("cx")]
    public int? Cx { get; init; }

    [JsonPropertyName("cy")]
    public int? Cy { get; init; }

    [JsonPropertyName("w")]
    public int? W { get; init; }

    [JsonPropertyName("h")]
    public int? H { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;
}

/// <summary>
/// A time-indexed keyframe along a smoothed AI camera zoom path.
/// Coordinates are in source video pixels.
/// </summary>
public record AiTrackingKeyframe
{
    [JsonPropertyName("t")]
    public double TimeSec { get; init; }

    [JsonPropertyName("x")]
    public double CropX { get; init; }

    [JsonPropertyName("y")]
    public double CropY { get; init; }

    [JsonPropertyName("w")]
    public double CropW { get; init; }

    [JsonPropertyName("h")]
    public double CropH { get; init; }

    [JsonPropertyName("scale")]
    public double Scale { get; init; }

    [JsonPropertyName("vis")]
    public bool Visible { get; init; }
}

/// <summary>
/// JSON serializer context for AI tracking data structures to ensure Native AOT compliance.
/// </summary>
[JsonSerializable(typeof(RawSubjectWaypoint))]
[JsonSerializable(typeof(List<RawSubjectWaypoint>))]
[JsonSerializable(typeof(AiTrackingKeyframe))]
[JsonSerializable(typeof(List<AiTrackingKeyframe>))]
[JsonSerializable(typeof(AiTrajectorySmoother.SmoothedTrajectory))]
public partial class AiTrackingJsonContext : JsonSerializerContext { }

/// <summary>
/// Mathematical smoothing, velocity-adaptive breathing, and vanishing-target fallback
/// engine for universal AI subject tracking.
/// </summary>
public static class AiTrajectorySmoother
{
    public class SmoothedTrajectory
    {
        [JsonPropertyName("keyframes")]
        public List<AiTrackingKeyframe> Keyframes { get; set; } = new();

        [JsonPropertyName("sourceW")]
        public int SourceW { get; set; }

        [JsonPropertyName("sourceH")]
        public int SourceH { get; set; }

        [JsonPropertyName("segmentDuration")]
        public double SegmentDurationSec { get; set; }

        public (double CropX, double CropY, double CropW, double CropH, double Scale) EvaluateAt(double relSec)
        {
            if (Keyframes.Count == 0)
            {
                return (0, 0, SourceW, SourceH, 1.0);
            }

            if (relSec <= Keyframes[0].TimeSec)
            {
                var k0 = Keyframes[0];
                return (k0.CropX, k0.CropY, k0.CropW, k0.CropH, k0.Scale);
            }

            if (relSec >= Keyframes[^1].TimeSec)
            {
                var kn = Keyframes[^1];
                return (kn.CropX, kn.CropY, kn.CropW, kn.CropH, kn.Scale);
            }

            for (int i = 0; i < Keyframes.Count - 1; i++)
            {
                var kA = Keyframes[i];
                var kB = Keyframes[i + 1];

                if (relSec >= kA.TimeSec && relSec <= kB.TimeSec)
                {
                    double dt = kB.TimeSec - kA.TimeSec;
                    double alpha = dt > 0.0001 ? (relSec - kA.TimeSec) / dt : 0.0;
                    double t = alpha * alpha * (3.0 - 2.0 * alpha);

                    double x = kA.CropX + (kB.CropX - kA.CropX) * t;
                    double y = kA.CropY + (kB.CropY - kA.CropY) * t;
                    double w = kA.CropW + (kB.CropW - kA.CropW) * t;
                    double h = kA.CropH + (kB.CropH - kA.CropH) * t;
                    double scale = kA.Scale + (kB.Scale - kA.Scale) * t;

                    return (x, y, w, h, scale);
                }
            }

            var last = Keyframes[^1];
            return (last.CropX, last.CropY, last.CropW, last.CropH, last.Scale);
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, AiTrackingJsonContext.Default.SmoothedTrajectory);
        }

        public static SmoothedTrajectory? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize(json, AiTrackingJsonContext.Default.SmoothedTrajectory);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                return null;
            }
        }

        /// <summary>
        /// Formulates an FFmpeg piecewise-lerp crop filter expression across keyframes:
        /// crop=w='W(t)':h='H(t)':x='X(t)':y='Y(t)':eval=frame
        /// </summary>
        public string ToFfmpegCropFilter()
        {
            if (Keyframes.Count == 0) return string.Empty;

            var ci = CultureInfo.InvariantCulture;

            bool isScaleDynamic = false;
            if (Keyframes.Count > 1)
            {
                double firstScale = Keyframes[0].Scale;
                for (int i = 1; i < Keyframes.Count; i++)
                {
                    if (Math.Abs(Keyframes[i].Scale - firstScale) > 0.01)
                    {
                        isScaleDynamic = true;
                        break;
                    }
                }
            }

            if (!isScaleDynamic)
            {
                int w = Math.Max(2, ((int)Math.Round(Keyframes[0].CropW) / 2) * 2);
                int h = Math.Max(2, ((int)Math.Round(Keyframes[0].CropH) / 2) * 2);
                if (SourceW > 0) w = Math.Min(w, (SourceW / 2) * 2);
                if (SourceH > 0) h = Math.Min(h, (SourceH / 2) * 2);

                if (Keyframes.Count == 1)
                {
                    int maxX = SourceW > 0 ? Math.Max(0, SourceW - w) : int.MaxValue;
                    int maxY = SourceH > 0 ? Math.Max(0, SourceH - h) : int.MaxValue;
                    int x = Math.Clamp(((int)Math.Round(Keyframes[0].CropX) / 2) * 2, 0, maxX);
                    int y = Math.Clamp(((int)Math.Round(Keyframes[0].CropY) / 2) * 2, 0, maxY);
                    return $"crop=w={w.ToString(ci)}:h={h.ToString(ci)}:x={x.ToString(ci)}:y={y.ToString(ci)}";
                }

                var sbX = new StringBuilder();
                var sbY = new StringBuilder();
                BuildPiecewiseExpr(sbX, Keyframes, k => k.CropX);
                BuildPiecewiseExpr(sbY, Keyframes, k => k.CropY);

                return $"crop=w={w.ToString(ci)}:h={h.ToString(ci)}:x='min(in_w-out_w,max(0,2*trunc(({sbX})/2)))':y='min(in_h-out_h,max(0,2*trunc(({sbY})/2)))'";
            }
            else
            {
                var sbScale = new StringBuilder();
                var sbScaledX = new StringBuilder();
                var sbScaledY = new StringBuilder();

                BuildPiecewiseExpr(sbScale, Keyframes, k => k.Scale);
                BuildPiecewiseExpr(sbScaledX, Keyframes, k => k.CropX * k.Scale);
                BuildPiecewiseExpr(sbScaledY, Keyframes, k => k.CropY * k.Scale);

                int baseW = SourceW > 0 ? (SourceW / 2) * 2 : 1920;
                int baseH = SourceH > 0 ? (SourceH / 2) * 2 : 1080;

                return $"scale=w='2*trunc((iw*({sbScale}))/2)':h='2*trunc((ih*({sbScale}))/2)':eval=frame,crop=w={baseW.ToString(ci)}:h={baseH.ToString(ci)}:x='min(in_w-out_w,max(0,2*trunc(({sbScaledX})/2)))':y='min(in_h-out_h,max(0,2*trunc(({sbScaledY})/2)))'";
            }
        }

        private static void BuildPiecewiseExpr(StringBuilder sb, List<AiTrackingKeyframe> kfs, Func<AiTrackingKeyframe, double> selector)
        {
            var ci = CultureInfo.InvariantCulture;
            int count = kfs.Count;

            int openParens = 0;
            for (int i = 0; i < count - 1; i++)
            {
                var kA = kfs[i];
                var kB = kfs[i + 1];
                double vA = Math.Round(selector(kA), 2);
                double vB = Math.Round(selector(kB), 2);
                double tA = Math.Round(kA.TimeSec, 2);
                double tB = Math.Round(kB.TimeSec, 2);
                double dt = Math.Max(0.01, tB - tA);

                sb.Append("if(lte(t,");
                sb.Append(tB.ToString("0.00", ci));
                sb.Append("),");
                sb.Append(vA.ToString("0.00", ci));
                sb.Append("+(");
                sb.Append((vB - vA).ToString("0.00", ci));
                sb.Append(")*(t-");
                sb.Append(tA.ToString("0.00", ci));
                sb.Append(")/");
                sb.Append(dt.ToString("0.00", ci));
                sb.Append(",");
                openParens++;
            }

            sb.Append(Math.Round(selector(kfs[^1]), 2).ToString("0.00", ci));
            sb.Append(new string(')', openParens));
        }
    }

    /// <summary>
    /// Computes a smoothed camera tracking trajectory with dynamic breathing and vanishing fallback.
    /// </summary>
    public static SmoothedTrajectory SmoothTrajectory(
        IReadOnlyList<RawSubjectWaypoint> rawWaypoints,
        double segmentDurationSec,
        int sourceW,
        int sourceH,
        double baseScale = 2.2,
        double minScale = 1.3,
        double deadbandPercent = 2.0,
        bool avoidHud = true,
        bool portraitMode = false)
    {
        var result = new SmoothedTrajectory
        {
            SourceW = sourceW,
            SourceH = sourceH,
            SegmentDurationSec = segmentDurationSec
        };

        if (sourceW <= 0 || sourceH <= 0 || segmentDurationSec <= 0)
        {
            return result;
        }

        baseScale = Math.Clamp(baseScale, 1.2, 5.0);
        minScale = Math.Clamp(minScale, 1.0, baseScale);
        deadbandPercent = Math.Clamp(deadbandPercent, 0.0, 10.0);

        var tempSorted = new List<RawSubjectWaypoint>(rawWaypoints);
        tempSorted.Sort((a, b) => a.Time.CompareTo(b.Time));

        var sorted = new List<RawSubjectWaypoint>();
        foreach (var wp in tempSorted)
        {
            if (sorted.Count > 0 && Math.Abs(sorted[^1].Time - wp.Time) < 0.001)
            {
                if (!sorted[^1].Visible && wp.Visible)
                {
                    sorted[^1] = wp;
                }
                continue;
            }
            sorted.Add(wp);
        }

        if (sorted.Count == 0)
        {
            result.Keyframes.Add(new AiTrackingKeyframe
            {
                TimeSec = 0.0,
                CropX = 0,
                CropY = 0,
                CropW = sourceW,
                CropH = sourceH,
                Scale = 1.0,
                Visible = false
            });
            result.Keyframes.Add(new AiTrackingKeyframe
            {
                TimeSec = segmentDurationSec,
                CropX = 0,
                CropY = 0,
                CropW = sourceW,
                CropH = sourceH,
                Scale = 1.0,
                Visible = false
            });
            return result;
        }

        int n = sorted.Count;
        double[] velocities = new double[n];
        for (int i = 0; i < n - 1; i++)
        {
            var pA = sorted[i];
            var pB = sorted[i + 1];
            if (pA.Visible && pB.Visible && pA.Cx.HasValue && pA.Cy.HasValue && pB.Cx.HasValue && pB.Cy.HasValue)
            {
                double dt = Math.Max(0.04, pB.Time - pA.Time);
                double dx = pB.Cx.Value - pA.Cx.Value;
                double dy = pB.Cy.Value - pA.Cy.Value;
                velocities[i] = Math.Sqrt(dx * dx + dy * dy) / dt;
            }
        }

        double[] lookaheadBreathing = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (velocities[i] > 300.0)
            {
                double spikeIntensity = Math.Clamp((velocities[i] - 300.0) / 450.0, 0.0, 1.0);
                double tSpike = sorted[i].Time;
                for (int j = 0; j <= i; j++)
                {
                    double dt = tSpike - sorted[j].Time;
                    if (dt >= 0 && dt <= 0.5)
                    {
                        double factor = (1.0 - (dt / 0.5)) * spikeIntensity;
                        lookaheadBreathing[j] = Math.Max(lookaheadBreathing[j], factor);
                    }
                }
            }
        }

        var processedPoints = new List<(double Time, double Cx, double Cy, double W, double H, double Scale, bool Visible)>();
        double lastValidCx = 500.0;
        double lastValidCy = 500.0;
        double lastValidW = 120.0;
        double lastValidH = 260.0;
        double lastValidTime = 0.0;
        double lastAppliedScale = baseScale;
        double lastVx = 0.0;
        double lastVy = 0.0;
        bool hasInitial = false;

        double deadbandThreshold = (deadbandPercent / 100.0) * 1000.0;

        for (int i = 0; i < n; i++)
        {
            var wp = sorted[i];
            if (!wp.Visible || !wp.Cx.HasValue || !wp.Cy.HasValue)
            {
                processedPoints.Add((wp.Time, lastValidCx, lastValidCy, lastValidW, lastValidH, lastAppliedScale, false));
                continue;
            }

            double rawCx = Math.Clamp((double)wp.Cx.Value, 50.0, 950.0);
            double rawCy = Math.Clamp((double)wp.Cy.Value, 50.0, 950.0);
            double wVal = Math.Clamp((double)wp.W.GetValueOrDefault(120), 40.0, 900.0);
            double hVal = Math.Clamp((double)wp.H.GetValueOrDefault(260), 60.0, 950.0);

            if (hVal > 1.15 * wVal)
            {
                rawCy = Math.Max(50.0, rawCy - (0.12 * hVal));
            }

            double bodyFitScale = 1000.0 / Math.Max(120.0, hVal * 1.50);
            double idealScale = Math.Clamp(bodyFitScale, minScale, baseScale);

            double expansion = lookaheadBreathing[i];
            double motionTargetScale = idealScale - (idealScale - minScale) * expansion;

            if (!hasInitial)
            {
                lastValidCx = rawCx;
                lastValidCy = rawCy;
                lastValidW = wVal;
                lastValidH = hVal;
                lastValidTime = wp.Time;
                lastAppliedScale = motionTargetScale;
                hasInitial = true;
                processedPoints.Add((wp.Time, rawCx, rawCy, wVal, hVal, motionTargetScale, true));
                continue;
            }

            double dx = rawCx - lastValidCx;
            double dy = rawCy - lastValidCy;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            double effectiveCx = dist < deadbandThreshold ? lastValidCx : rawCx;
            double effectiveCy = dist < deadbandThreshold ? lastValidCy : rawCy;

            double dt = Math.Max(0.04, wp.Time - lastValidTime);
            lastVx = (effectiveCx - lastValidCx) / dt;
            lastVy = (effectiveCy - lastValidCy) / dt;

            double smoothedScale = lastAppliedScale + (motionTargetScale - lastAppliedScale) * 0.55;

            lastValidCx = effectiveCx;
            lastValidCy = effectiveCy;
            lastValidW = wVal;
            lastValidH = hVal;
            lastValidTime = wp.Time;
            lastAppliedScale = smoothedScale;

            processedPoints.Add((wp.Time, effectiveCx, effectiveCy, wVal, hVal, smoothedScale, true));
        }

        var finalTimeline = new List<(double Time, double Cx, double Cy, double W, double H, double Scale, bool Visible)>();
        double lastConfirmedVisibleTime = -1.0;
        double vanishStartCx = 500.0, vanishStartCy = 500.0, vanishStartScale = 1.0;
        double vanishW = 120.0, vanishH = 260.0;
        double persistVx = 0.0, persistVy = 0.0;

        for (int i = 0; i < processedPoints.Count; i++)
        {
            var pt = processedPoints[i];
            if (pt.Visible)
            {
                lastConfirmedVisibleTime = pt.Time;
                vanishStartCx = pt.Cx;
                vanishStartCy = pt.Cy;
                vanishStartScale = pt.Scale;
                vanishW = pt.W;
                vanishH = pt.H;
                persistVx = lastVx;
                persistVy = lastVy;
                finalTimeline.Add(pt);
            }
            else
            {
                if (lastConfirmedVisibleTime >= 0)
                {
                    double timeSinceLoss = pt.Time - lastConfirmedVisibleTime;
                    if (timeSinceLoss <= 1.0)
                    {
                        double drag = 1.0 - (timeSinceLoss / 1.0) * 0.4;
                        double predCx = Math.Clamp(vanishStartCx + persistVx * timeSinceLoss * drag, 80.0, 920.0);
                        double predCy = Math.Clamp(vanishStartCy + persistVy * timeSinceLoss * drag, 80.0, 920.0);
                        finalTimeline.Add((pt.Time, predCx, predCy, vanishW, vanishH, vanishStartScale, false));
                    }
                    else if (timeSinceLoss <= 1.8)
                    {
                        double p = (timeSinceLoss - 1.0) / 0.8;
                        double smoothP = p * p * (3.0 - 2.0 * p);
                        double easeScale = vanishStartScale + (1.0 - vanishStartScale) * smoothP;
                        double easeCx = vanishStartCx + (500.0 - vanishStartCx) * smoothP;
                        double easeCy = vanishStartCy + (500.0 - vanishStartCy) * smoothP;
                        finalTimeline.Add((pt.Time, easeCx, easeCy, vanishW, vanishH, easeScale, false));
                    }
                    else
                    {
                        finalTimeline.Add((pt.Time, 500.0, 500.0, vanishW, vanishH, 1.0, false));
                    }
                }
                else
                {
                    finalTimeline.Add((pt.Time, 500.0, 500.0, 120.0, 260.0, 1.0, false));
                }
            }
        }

        if (finalTimeline.Count > 0)
        {
            if (finalTimeline[0].Time > 0.001)
            {
                var first = finalTimeline[0];
                finalTimeline.Insert(0, (0.0, first.Cx, first.Cy, first.W, first.H, first.Scale, first.Visible));
            }
            else
            {
                var first = finalTimeline[0];
                finalTimeline[0] = (0.0, first.Cx, first.Cy, first.W, first.H, first.Scale, first.Visible);
            }
        }

        if (finalTimeline.Count > 0)
        {
            if (finalTimeline[^1].Time < segmentDurationSec - 0.001)
            {
                var last = finalTimeline[^1];
                finalTimeline.Add((segmentDurationSec, last.Cx, last.Cy, last.W, last.H, last.Scale, last.Visible));
            }
            else
            {
                var last = finalTimeline[^1];
                finalTimeline[^1] = (segmentDurationSec, last.Cx, last.Cy, last.W, last.H, last.Scale, last.Visible);
            }
        }

        foreach (var pt in finalTimeline)
        {
            double scale = Math.Clamp(pt.Scale, 1.0, baseScale);
            double cropW = sourceW / scale;
            double cropH = sourceH / scale;

            double targetCenterX = (pt.Cx / 1000.0) * sourceW;
            double targetCenterY = (pt.Cy / 1000.0) * sourceH;

            double cropX = targetCenterX - (cropW / 2.0);
            double cropY = targetCenterY - (cropH / 2.0);

            double feetY = ((pt.Cy + pt.H / 2.0) / 1000.0) * sourceH;
            double headY = ((pt.Cy - pt.H / 2.0) / 1000.0) * sourceH;

            double bottomMarginFrac = portraitMode ? 0.78 : 0.88;
            double topMarginFrac = portraitMode ? 0.12 : 0.10;

            if (feetY > cropY + cropH * bottomMarginFrac)
            {
                cropY = feetY - cropH * bottomMarginFrac;
            }

            if (headY < cropY + cropH * topMarginFrac)
            {
                cropY = headY - cropH * topMarginFrac;
            }

            if (avoidHud && !portraitMode)
            {
                if (cropX + cropW > sourceW * 0.78 && cropY < sourceH * 0.32)
                {
                    cropX = Math.Min(cropX, sourceW * 0.78 - cropW);
                    cropY = Math.Max(cropY, sourceH * 0.32);
                }
            }

            if (portraitMode && sourceW > 0 && sourceH > 0)
            {
                double scalePortrait = Math.Max((double)CoordinateConstants.InternalW / sourceW, (double)CoordinateConstants.InternalH / sourceH);
                double survW = CoordinateConstants.InternalW / scalePortrait;
                double minSliceX = (sourceW - survW) / 2.0;
                double maxSliceX = minSliceX + survW;
                if (cropW <= survW)
                {
                    cropX = Math.Clamp(cropX, minSliceX, maxSliceX - cropW);
                }
            }

            cropX = Math.Clamp(cropX, 0.0, Math.Max(0.0, sourceW - cropW));
            cropY = Math.Clamp(cropY, 0.0, Math.Max(0.0, sourceH - cropH));

            result.Keyframes.Add(new AiTrackingKeyframe
            {
                TimeSec = Math.Round(pt.Time, 3),
                CropX = Math.Round(cropX, 1),
                CropY = Math.Round(cropY, 1),
                CropW = Math.Round(cropW, 1),
                CropH = Math.Round(cropH, 1),
                Scale = Math.Round(scale, 2),
                Visible = pt.Visible
            });
        }

        return result;
    }
}

// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/03_FFMPEG_EXPORT_PIPELINE.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Services;

#region Gemini JSON Models for Native AOT

public class GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("inlineData")]
    public GeminiInlineData? InlineData { get; set; }
}

public class GeminiInlineData
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "video/mp4";

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}

public class GeminiContent
{
    [JsonPropertyName("parts")]
    public List<GeminiPart> Parts { get; set; } = new();
}

public class GeminiGenerationConfig
{
    [JsonPropertyName("responseMimeType")]
    public string? ResponseMimeType { get; set; }
}

public class GeminiGenerateRequest
{
    [JsonPropertyName("contents")]
    public List<GeminiContent> Contents { get; set; } = new();

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig? GenerationConfig { get; set; }
}

public class GeminiGenerateResponse
{
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }
}

public class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }
}

public class DiscoveredSubject
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("ymin")]
    public int Ymin { get; set; }

    [JsonPropertyName("xmin")]
    public int Xmin { get; set; }

    [JsonPropertyName("ymax")]
    public int Ymax { get; set; }

    [JsonPropertyName("xmax")]
    public int Xmax { get; set; }

    [JsonPropertyName("frame_index")]
    public int FrameIndex { get; set; } = 2;
}

[JsonSerializable(typeof(GeminiGenerateRequest))]
[JsonSerializable(typeof(GeminiGenerateResponse))]
[JsonSerializable(typeof(List<RawSubjectWaypoint>))]
[JsonSerializable(typeof(DiscoveredSubject))]
[JsonSerializable(typeof(List<DiscoveredSubject>))]
internal partial class GeminiApiJsonContext : JsonSerializerContext { }

#endregion

/// <summary>
/// Universal AI Subject Tracking Service using Google Gemini Vision Protocol.
/// Performs video slice extraction, multi-angle subject discovery, and trajectory smoothing.
/// </summary>
public static class GeminiTrackingService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    static GeminiTrackingService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-AiTracking/1.0");
    }

    /// <summary>
    /// Verifies the validity of a Gemini API key via a minimal ping request.
    /// </summary>
    public static async Task<(bool Success, string Message)> TestApiKeyAsync(
        string apiKey,
        string modelName = "gemini-2.5-flash",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "API Key is empty. Please enter a valid Gemini API Key.");
        }

        if (string.IsNullOrWhiteSpace(modelName)) modelName = "gemini-2.5-flash";

        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";

        var requestBody = new GeminiGenerateRequest
        {
            Contents = new List<GeminiContent>
            {
                new()
                {
                    Parts = new List<GeminiPart>
                    {
                        new() { Text = "Ping. Reply with OK." }
                    }
                }
            }
        };

        try
        {
            string json = JsonSerializer.Serialize(requestBody, GeminiApiJsonContext.Default.GeminiGenerateRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await HttpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Key Verified Successfully!");
            }

            int statusCode = (int)response.StatusCode;
            return (false, MapStatusCodeToMessage(statusCode));
        }
        catch (OperationCanceledException ex)
        {
            CoreLogger.Swallowed(ex);
            return (false, "Request timed out or was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            CoreLogger.Swallowed(ex);
            return (false, "Connection to Google AI servers failed. Check your internet connection.");
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return (false, $"Verification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts 3 candidate still frames from start, middle, and end of the segment for multi-angle discovery.
    /// </summary>
    public static async Task<(string F1, string F2, string F3, double T1, double T2, double T3)> ExtractThreeCandidateFramesAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        CancellationToken cancellationToken = default)
    {
        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);

        double t1 = sourceStartSec + Math.Max(0.05, 0.15 * sourceDurationSec);
        double t2 = sourceStartSec + 0.50 * sourceDurationSec;
        double t3 = sourceStartSec + Math.Min(sourceDurationSec - 0.05, 0.85 * sourceDurationSec);

        string f1Path = Path.Combine(tempDir, $"cand_f1_{Guid.NewGuid():N}.jpg");
        string f2Path = Path.Combine(tempDir, $"cand_f2_{Guid.NewGuid():N}.jpg");
        string f3Path = Path.Combine(tempDir, $"cand_f3_{Guid.NewGuid():N}.jpg");

        await ExtractStillAsync(videoPath, t1, f1Path, cancellationToken).ConfigureAwait(false);
        await ExtractStillAsync(videoPath, t2, f2Path, cancellationToken).ConfigureAwait(false);
        await ExtractStillAsync(videoPath, t3, f3Path, cancellationToken).ConfigureAwait(false);

        return (f1Path, f2Path, f3Path, t1, t2, t3);
    }

    private static async Task ExtractStillAsync(string videoPath, double sec, string outPath, CancellationToken token)
    {
        string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(sec.ToString("0.000", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add(outPath);

        await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Queries Gemini with 3 distinct frames to discover and label distinct visible characters/subjects.
    /// </summary>
    public static async Task<List<DiscoveredSubject>> DiscoverSubjectsAsync(
        string frame1Path,
        string frame2Path,
        string frame3Path,
        string apiKey,
        string modelName = "gemini-2.5-flash",
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
    {
        consoleLog?.Invoke("Sending 3 multi-angle candidate frames to Gemini Vision scanner...");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(modelName)) modelName = "gemini-2.5-flash";

        byte[] b1 = await File.ReadAllBytesAsync(frame1Path, cancellationToken).ConfigureAwait(false);
        byte[] b2 = await File.ReadAllBytesAsync(frame2Path, cancellationToken).ConfigureAwait(false);
        byte[] b3 = await File.ReadAllBytesAsync(frame3Path, cancellationToken).ConfigureAwait(false);

        string promptText =
            "You are an expert computer vision subject detector for video games.\n" +
            "Analyze these 3 distinct video frames from a clip of Fortnite, a third-person shooter.\n" +
            "MANDATORY EXCLUSION RULE — DO NOT DETECT THE LOCAL PLAYER:\n" +
            "In Fortnite, the game camera is positioned behind the player's own character in third-person view. The player's own character is always shown in the foreground (typically center-bottom or bottom-left). NEVER detect, label, or output the player's own character!\n" +
            "MANDATORY EXCLUSION RULE — CHARACTERS AND COMBATANTS ONLY:\n" +
            "NEVER detect or label buildings, houses, towers, statues, trees, landscape terrain, or inanimate scenery! ONLY detect human/avatar character models, enemy players, combatants, rivals, or vehicles driven by players.\n" +
            "ATTENTION TO DISTANT OPPONENTS & SHIELD DOMES:\n" +
            "Opponents may be distant, small, partially occluded, inside translucent bubble shield domes, crouched on rooftops/piers, holding weapons, or landing from gliders. Carefully scan roofs, platforms, and bubble shields for opponent players.\n" +
            "For each distinct opponent/target detected, output:\n" +
            "- id: integer starting from 1\n" +
            "- label: a descriptive visual label identifying the target (e.g. 'Opponent with Shotgun', 'Enemy in Bubble Shield', 'Opponent Deploying Glider', 'Distant Player on Roof')\n" +
            "- frame_index: 1, 2, or 3 indicating which frame the target is clearest in (1=first angle, 2=middle angle, 3=last angle)\n" +
            "- ymin, xmin, ymax, xmax: integer bounding box on 0-1000 scale on that frame.\n" +
            "If no opponents or enemies are visible in these frames, output an empty JSON array [].\n" +
            "Output strictly a raw JSON array of objects without markdown formatting or code fences.";

        var requestPayload = new GeminiGenerateRequest
        {
            Contents = new List<GeminiContent>
            {
                new()
                {
                    Parts = new List<GeminiPart>
                    {
                        new() { Text = promptText },
                        new() { InlineData = new GeminiInlineData { MimeType = "image/jpeg", Data = Convert.ToBase64String(b1) } },
                        new() { InlineData = new GeminiInlineData { MimeType = "image/jpeg", Data = Convert.ToBase64String(b2) } },
                        new() { InlineData = new GeminiInlineData { MimeType = "image/jpeg", Data = Convert.ToBase64String(b3) } }
                    }
                }
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                ResponseMimeType = "application/json"
            }
        };

        string requestJson = JsonSerializer.Serialize(requestPayload, GeminiApiJsonContext.Default.GeminiGenerateRequest);
        string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";

        using var httpResponse = await PostWithRetryAsync(endpoint, requestJson, consoleLog, cancellationToken).ConfigureAwait(false);

        string responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsedResponse = JsonSerializer.Deserialize(responseJson, GeminiApiJsonContext.Default.GeminiGenerateResponse);

        string? rawJsonText = null;
        if (parsedResponse?.Candidates != null && parsedResponse.Candidates.Count > 0)
        {
            var parts = parsedResponse.Candidates[0].Content?.Parts;
            if (parts != null && parts.Count > 0)
            {
                rawJsonText = parts[0].Text;
            }
        }

        CoreLogger.Info("AI-Discovery", $"Raw Gemini subject discovery response: {rawJsonText}");

        var rawSubjects = new List<DiscoveredSubject>();
        if (!string.IsNullOrWhiteSpace(rawJsonText))
        {
            string cleanJson = rawJsonText.Trim();
            if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase)) cleanJson = cleanJson.Substring(7);
            else if (cleanJson.StartsWith("```", StringComparison.OrdinalIgnoreCase)) cleanJson = cleanJson.Substring(3);
            if (cleanJson.EndsWith("```", StringComparison.OrdinalIgnoreCase)) cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
            cleanJson = cleanJson.Trim();

            try
            {
                var subjects = JsonSerializer.Deserialize(cleanJson, GeminiApiJsonContext.Default.ListDiscoveredSubject);
                if (subjects != null && subjects.Count > 0) rawSubjects.AddRange(subjects);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
            }
        }

        var validTargets = new List<DiscoveredSubject>();
        for (int i = 0; i < rawSubjects.Count; i++)
        {
            var sub = rawSubjects[i];
            if (IsMainPlayerCharacter(sub))
            {
                consoleLog?.Invoke($"Excluded main player character '{sub.Label}' from target options (Fortnite 3rd-person rule).");
                continue;
            }
            if (IsInanimateScenery(sub))
            {
                consoleLog?.Invoke($"Excluded inanimate scenery '{sub.Label}' from target options.");
                continue;
            }
            sub.Id = validTargets.Count + 1;
            validTargets.Add(sub);
        }

        consoleLog?.Invoke($"Discovered {validTargets.Count} external target candidate(s) (main player excluded).");
        return validTargets;
    }

    /// <summary>
    /// Extracts multi-angle candidate frames and discovers subjects. If no enemy subjects are resolved
    /// in the initial segment window, probes forward into the video (up to 2 forward steps) to locate
    /// combatants/opponents that appear or engage slightly later in the action.
    /// </summary>
    public static async Task<(List<DiscoveredSubject> Subjects, string F1, string F2, string F3, double T1, double T2, double T3)> DiscoverSubjectsWithLookaheadAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        double totalVideoDuration,
        string apiKey,
        string modelName = "gemini-2.5-flash",
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
    {
        var (f1, f2, f3, t1, t2, t3) = await ExtractThreeCandidateFramesAsync(
            videoPath, sourceStartSec, sourceDurationSec, cancellationToken).ConfigureAwait(false);

        var subjects = await DiscoverSubjectsAsync(
            f1, f2, f3, apiKey, modelName, consoleLog, cancellationToken).ConfigureAwait(false);

        if (subjects.Count > 0)
        {
            return (subjects, f1, f2, f3, t1, t2, t3);
        }

        double[] lookaheadDeltas =
        {
            Math.Max(2.0, sourceDurationSec * 0.75),
            Math.Max(4.0, sourceDurationSec * 1.50)
        };

        foreach (double delta in lookaheadDeltas)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double probeStart = sourceStartSec + delta;
            if (totalVideoDuration > 0 && probeStart + 0.5 >= totalVideoDuration)
            {
                break;
            }

            consoleLog?.Invoke($"No enemy targets resolved in initial segment frames. Probing forward +{delta.ToString("0.0", CultureInfo.InvariantCulture)}s ({probeStart.ToString("0.0", CultureInfo.InvariantCulture)}s) for visible combatants...");

            string pf1 = string.Empty, pf2 = string.Empty, pf3 = string.Empty;
            try
            {
                var (newF1, newF2, newF3, pt1, pt2, pt3) = await ExtractThreeCandidateFramesAsync(
                    videoPath, probeStart, sourceDurationSec, cancellationToken).ConfigureAwait(false);
                pf1 = newF1; pf2 = newF2; pf3 = newF3;

                var probeSubjects = await DiscoverSubjectsAsync(
                    pf1, pf2, pf3, apiKey, modelName, consoleLog, cancellationToken).ConfigureAwait(false);

                if (probeSubjects.Count > 0)
                {
                    consoleLog?.Invoke($"[Lookahead Success] Discovered {probeSubjects.Count} combatant target(s) at +{delta.ToString("0.0", CultureInfo.InvariantCulture)}s forward lookahead.");
                    DeleteQuietly(f1);
                    DeleteQuietly(f2);
                    DeleteQuietly(f3);
                    return (probeSubjects, pf1, pf2, pf3, pt1, pt2, pt3);
                }

                DeleteQuietly(pf1);
                DeleteQuietly(pf2);
                DeleteQuietly(pf3);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                DeleteQuietly(pf1);
                DeleteQuietly(pf2);
                DeleteQuietly(pf3);
            }
        }

        return (new List<DiscoveredSubject>(), f1, f2, f3, t1, t2, t3);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    /// <summary>
    /// Tracks a subject in a video segment and returns the smoothed camera zoom trajectory.
    /// </summary>
    public static async Task<AiTrajectorySmoother.SmoothedTrajectory> TrackSubjectAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        (int ymin, int xmin, int ymax, int xmax) initialBox,
        int sourceWidth,
        int sourceHeight,
        string apiKey,
        string modelName,
        double baseScale = 2.2,
        double minScale = 1.3,
        double deadbandPercent = 2.0,
        bool avoidHud = true,
        bool portraitMode = false,
        DiscoveredSubject? confirmedTarget = null,
        string? referenceFramePath = null,
        IProgress<string>? statusProgress = null,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video file not found", videoPath);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(modelName)) modelName = "gemini-2.5-flash";

        double sliceDuration = Math.Clamp(sourceDurationSec, 2.0, 8.0);

        statusProgress?.Report("Phase 1: Slicing segment preview...");
        consoleLog?.Invoke($"Extracting 640p video slice at {sourceStartSec.ToString("0.00", CultureInfo.InvariantCulture)}s ({sliceDuration.ToString("0.00", CultureInfo.InvariantCulture)}s duration)...");

        byte[]? refCropBytes = null;
        if (!string.IsNullOrEmpty(referenceFramePath) && File.Exists(referenceFramePath))
        {
            int cropYmin = confirmedTarget?.Ymin ?? initialBox.ymin;
            int cropXmin = confirmedTarget?.Xmin ?? initialBox.xmin;
            int cropYmax = confirmedTarget?.Ymax ?? initialBox.ymax;
            int cropXmax = confirmedTarget?.Xmax ?? initialBox.xmax;
            refCropBytes = CropSubjectJpeg(referenceFramePath, cropYmin, cropXmin, cropYmax, cropXmax);
            if (refCropBytes != null)
            {
                consoleLog?.Invoke($"Attached visual reference crop of target '{confirmedTarget?.Label ?? "Selected Target"}' ({refCropBytes.Length / 1024} KB JPEG) to tracking request.");
            }
        }

        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);
        string tempMp4Path = Path.Combine(tempDir, $"ai_track_{Guid.NewGuid():N}.mp4");

        byte[] mp4Bytes;
        try
        {
            string ffmpegPath = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            var ci = CultureInfo.InvariantCulture;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(sourceStartSec.ToString("0.000", ci));
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(sliceDuration.ToString("0.000", ci));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(videoPath);
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add("scale=640:-2");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("ultrafast");
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add("28");
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add(tempMp4Path);

            var (exitCode, _, stderr) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(tempMp4Path))
            {
                throw new InvalidOperationException($"FFmpeg slice failed (exit {exitCode}): {stderr}");
            }

            mp4Bytes = await File.ReadAllBytesAsync(tempMp4Path, cancellationToken).ConfigureAwait(false);
            consoleLog?.Invoke($"Extracted slice size: {mp4Bytes.Length / 1024} KB.");
        }
        finally
        {
            try
            {
                if (File.Exists(tempMp4Path)) File.Delete(tempMp4Path);
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        statusProgress?.Report("Phase 2: Analyzing subject motion with Gemini...");
        string logMsg = "Encoding inline video payload and querying Gemini vision model...";
        consoleLog?.Invoke(logMsg);
        CoreLogger.Info("AI-Tracking", logMsg);

        string base64Video = Convert.ToBase64String(mp4Bytes);

        string targetLabel = confirmedTarget?.Label ?? "target subject";
        string targetBoxDesc = confirmedTarget != null
            ? $"near [{confirmedTarget.Ymin}, {confirmedTarget.Xmin}, {confirmedTarget.Ymax}, {confirmedTarget.Xmax}] on 0-1000 scale"
            : $"near [{initialBox.ymin}, {initialBox.xmin}, {initialBox.ymax}, {initialBox.xmax}] on 0-1000 scale";

        string refImageClause = refCropBytes != null
            ? "REFERENCE TARGET IMAGE (ATTACHED):\n" +
              "The attached JPEG image shows the EXACT visual appearance of the confirmed target subject.\n" +
              "Focus exclusively on tracking this specific subject across the video.\n"
            : "";

        string promptText =
            "You are a high-precision computer vision subject tracker for video games.\n" +
            $"CONFIRMED TARGET TO TRACK: '{targetLabel}' (initially observed {targetBoxDesc}).\n" +
            $"{refImageClause}" +
            "MANDATORY EXCLUSION RULE — NEVER TRACK THE LOCAL PLAYER:\n" +
            "This video is from Fortnite, a third-person shooter. The camera follows directly behind the local player's own character/avatar (typically in the lower-center or bottom-left foreground, e.g. green alien skin, glider, backpack).\n" +
            "NEVER track, switch to, or output coordinates for the player's own avatar in the foreground!\n" +
            "You must track ONLY the external opponent/target matching the reference target.\n" +
            "TRACKING SPECIFICATIONS:\n" +
            "Track this exact target across the video at 0.5-second intervals (0.0s, 0.5s, 1.0s, 1.5s, 2.0s, ...).\n" +
            "- If the target is occluded, distant, inside a shield dome, behind geometry, or not yet in frame, set visible to false, cx to null, cy to null, w to null, and h to null for those timestamps.\n" +
            "- NEVER switch tracking to the local foreground player avatar, other players, or visual clutter.\n" +
            "- For each timestamp, output: time (float), cx (center x 0-1000), cy (center y 0-1000), w (width 0-1000), h (height 0-1000), and visible (boolean).\n" +
            "Output strictly a raw JSON array of objects without markdown formatting or code fences.";

        var contentParts = new List<GeminiPart>
        {
            new() { Text = promptText }
        };

        if (refCropBytes != null)
        {
            contentParts.Add(new GeminiPart
            {
                InlineData = new GeminiInlineData
                {
                    MimeType = "image/jpeg",
                    Data = Convert.ToBase64String(refCropBytes)
                }
            });
        }

        contentParts.Add(new GeminiPart
        {
            InlineData = new GeminiInlineData
            {
                MimeType = "video/mp4",
                Data = base64Video
            }
        });

        var requestPayload = new GeminiGenerateRequest
        {
            Contents = new List<GeminiContent>
            {
                new()
                {
                    Parts = contentParts
                }
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                ResponseMimeType = "application/json"
            }
        };

        string requestJson = JsonSerializer.Serialize(requestPayload, GeminiApiJsonContext.Default.GeminiGenerateRequest);

        string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";

        using var httpResponse = await PostWithRetryAsync(endpoint, requestJson, consoleLog, cancellationToken).ConfigureAwait(false);

        string responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsedResponse = JsonSerializer.Deserialize(responseJson, GeminiApiJsonContext.Default.GeminiGenerateResponse);

        string? rawJsonText = null;
        if (parsedResponse?.Candidates != null && parsedResponse.Candidates.Count > 0)
        {
            var parts = parsedResponse.Candidates[0].Content?.Parts;
            if (parts != null && parts.Count > 0)
            {
                rawJsonText = parts[0].Text;
            }
        }

        if (string.IsNullOrWhiteSpace(rawJsonText))
        {
            throw new InvalidOperationException("Gemini returned an empty response or no tracking candidates were found.");
        }

        string cleanJson = rawJsonText.Trim();
        if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleanJson = cleanJson.Substring(7);
        }
        else if (cleanJson.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleanJson = cleanJson.Substring(3);
        }

        if (cleanJson.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
        }
        cleanJson = cleanJson.Trim();

        consoleLog?.Invoke($"Raw response:\n{cleanJson}");

        List<RawSubjectWaypoint>? waypoints;
        try
        {
            waypoints = JsonSerializer.Deserialize(cleanJson, GeminiApiJsonContext.Default.ListRawSubjectWaypoint);
        }
        catch (Exception ex)
        {
            consoleLog?.Invoke($"JSON parse error: {ex.Message}");
            throw new InvalidOperationException($"Failed to parse subject waypoints: {ex.Message}");
        }

        if (waypoints == null || waypoints.Count == 0)
        {
            throw new InvalidOperationException("No subject tracking waypoints were identified.");
        }

        int sanitizedCount = SanitizeWaypointsAgainstLocalPlayer(waypoints, confirmedTarget, initialBox, consoleLog);
        if (sanitizedCount > 0)
        {
            consoleLog?.Invoke($"Defensive filter: Sanitized {sanitizedCount} waypoint(s) that jumped to the local player avatar.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        statusProgress?.Report("Phase 3: Smoothing trajectory and velocity curves...");
        consoleLog?.Invoke($"Smoothing {waypoints.Count} waypoints over {sourceDurationSec.ToString("0.00", CultureInfo.InvariantCulture)}s...");

        var trajectory = AiTrajectorySmoother.SmoothTrajectory(
            waypoints,
            sourceDurationSec,
            sourceWidth,
            sourceHeight,
            baseScale,
            minScale,
            deadbandPercent,
            avoidHud,
            portraitMode);

        consoleLog?.Invoke($"Generated {trajectory.Keyframes.Count} smoothed camera keyframes.");

        return trajectory;
    }

    internal static string MapStatusCodeToMessage(int statusCode) => statusCode switch
    {
        401 => "Invalid Gemini API Key. Verify your key in Settings > AI Tracking.",
        429 => "Gemini free rate limit reached (15 requests per minute). Wait 30 seconds before retrying.",
        403 => "Permission denied for this model on your Google account.",
        500 or 503 => "Google servers are temporarily overloaded. Please try again.",
        _ => $"Google AI error (HTTP {statusCode}). Check your key and network."
    };

    private static async Task<HttpResponseMessage> PostWithRetryAsync(
        string endpoint,
        string requestJson,
        Action<string>? consoleLog,
        CancellationToken cancellationToken)
    {
        string redactedEndpoint = Regex.Replace(endpoint, @"key=[^&]+", "key=REDACTED");
        int maxRetries = 3;
        int[] delaysMs = [2000, 4000, 6000];

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await HttpClient.PostAsync(endpoint, requestContent, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                int delay = delaysMs[attempt];
                string retryMsg = $"[Gemini Retry {attempt + 1}/{maxRetries}] Network connection issue: {ex.Message}. Retrying in {delay / 1000}s...";
                consoleLog?.Invoke(retryMsg);
                CoreLogger.Warn("Gemini-Retry", $"{retryMsg} ({redactedEndpoint})");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            int statusCode = (int)response.StatusCode;
            string rawBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            CoreLogger.Warn("Gemini-HTTP", $"HTTP {statusCode} from {redactedEndpoint}. Body: {rawBody}");

            bool isRetriable = statusCode is 429 or 500 or 503 or 504;
            if (isRetriable && attempt < maxRetries)
            {
                response.Dispose();
                int delay = delaysMs[attempt];
                string friendly = MapStatusCodeToMessage(statusCode);
                string retryMsg = $"[Gemini Retry {attempt + 1}/{maxRetries}] HTTP {statusCode} ({friendly}). Retrying in {delay / 1000}s...";
                consoleLog?.Invoke(retryMsg);
                CoreLogger.Warn("Gemini-Retry", retryMsg);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            string mappedError = MapStatusCodeToMessage(statusCode);
            string errorDetails = $"Google AI Error (HTTP {statusCode}): {mappedError}\nEndpoint: {redactedEndpoint}\nResponse: {rawBody}";
            RuntimeLog.Fail("GEMINI_API", errorDetails);
            response.Dispose();
            throw new HttpRequestException($"{mappedError} (HTTP {statusCode})");
        }

        throw new HttpRequestException("Gemini request failed after maximum retry attempts.");
    }

    /// <summary>
    /// Evaluates if a detected candidate is the main player character.
    /// In Fortnite (a 3rd-person shooter), the player's own character is in the lower foreground
    /// directly in front of the camera and should NEVER be offered as a tracked target.
    /// </summary>
    internal static bool IsMainPlayerCharacter(DiscoveredSubject subject)
    {
        if (subject == null) return false;

        string label = subject.Label.Trim().ToLowerInvariant();

        if (label.Contains("main player") ||
            label.Contains("local player") ||
            label.Contains("primary character") ||
            label.Contains("self") ||
            label.Contains("protagonist") ||
            label.Contains("user character") ||
            label.Contains("camera character") ||
            label.Contains("own character") ||
            label.Contains("my player"))
        {
            return true;
        }

        int cx = (subject.Xmin + subject.Xmax) / 2;
        int height = subject.Ymax - subject.Ymin;

        bool inForegroundCenter = subject.Ymax >= 750 && cx >= 250 && cx <= 650 && height >= 300;

        if (inForegroundCenter && (label.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                                  label.Equals("character", StringComparison.OrdinalIgnoreCase) ||
                                  label.Contains("foreground") ||
                                  label.Contains("closest")))
        {
            return true;
        }

        if (subject.Ymax >= 850 && cx >= 300 && cx <= 600 && height >= 400)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates if a detected candidate is scenery, landscape, buildings, or inanimate geometry
    /// rather than an active character, combatant, or player vehicle.
    /// </summary>
    internal static bool IsInanimateScenery(DiscoveredSubject subject)
    {
        if (subject == null) return false;

        string label = subject.Label.Trim().ToLowerInvariant();

        if (label.Contains("opponent") ||
            label.Contains("player") ||
            label.Contains("enemy") ||
            label.Contains("combatant") ||
            label.Contains("character") ||
            label.Contains("rival") ||
            label.Contains("figure") ||
            label.Contains("glider") ||
            label.Contains("crouch") ||
            label.Contains("jump") ||
            label.Contains("aim") ||
            label.Contains("shoot") ||
            label.Contains("weapon"))
        {
            return false;
        }

        if (label.Contains("building") ||
            label.Contains("structure") ||
            label.Contains("house") ||
            label.Contains("tower") ||
            label.Contains("statue") ||
            label.Contains("monument") ||
            label.Contains("scenery") ||
            label.Contains("landscape") ||
            label.Contains("terrain") ||
            label.Contains("mountain") ||
            label.Contains("hill") ||
            label.Contains("rock") ||
            label.Contains("boulder") ||
            label.Contains("tree") ||
            label.Contains("bush") ||
            label.Contains("wall") ||
            label.Contains("fence") ||
            label.Contains("ramp") ||
            label.Contains("floor") ||
            label.Contains("roof") ||
            label.Contains("pier"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Crops a candidate subject bounding box from a candidate frame using SkiaSharp with padding margin.
    /// Returns a high-quality JPEG byte array.
    /// </summary>
    public static byte[]? CropSubjectJpeg(string framePath, int ymin, int xmin, int ymax, int xmax, int paddingPercent = 15)
    {
        if (string.IsNullOrEmpty(framePath) || !File.Exists(framePath)) return null;
        try
        {
            using var original = SKBitmap.Decode(framePath);
            if (original == null) return null;

            int frameW = original.Width;
            int frameH = original.Height;

            int pxMin = (int)Math.Round((xmin / 1000.0) * frameW);
            int pxMax = (int)Math.Round((xmax / 1000.0) * frameW);
            int pyMin = (int)Math.Round((ymin / 1000.0) * frameH);
            int pyMax = (int)Math.Round((ymax / 1000.0) * frameH);

            int targetW = Math.Max(1, pxMax - pxMin);
            int targetH = Math.Max(1, pyMax - pyMin);

            int padX = (int)Math.Round(targetW * (paddingPercent / 100.0));
            int padY = (int)Math.Round(targetH * (paddingPercent / 100.0));

            int cropX = Math.Clamp(pxMin - padX, 0, frameW - 1);
            int cropY = Math.Clamp(pyMin - padY, 0, frameH - 1);
            int cropW = Math.Clamp(pxMax + padX - cropX, 1, frameW - cropX);
            int cropH = Math.Clamp(pyMax + padY - cropY, 1, frameH - cropY);

            using var cropped = new SKBitmap(cropW, cropH);
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.DrawBitmap(original, new SKRect(cropX, cropY, cropX + cropW, cropY + cropH), new SKRect(0, 0, cropW, cropH));
            }

            using var image = SKImage.FromBitmap(cropped);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    /// <summary>
    /// Defensive Sanitizer: Detects and suppresses waypoints that erroneously lock onto the 3rd-person
    /// local player avatar in the foreground instead of tracking the external opponent.
    /// </summary>
    internal static int SanitizeWaypointsAgainstLocalPlayer(
        List<RawSubjectWaypoint> waypoints,
        DiscoveredSubject? confirmedTarget,
        (int ymin, int xmin, int ymax, int xmax) initialBox,
        Action<string>? consoleLog = null)
    {
        if (waypoints == null || waypoints.Count == 0) return 0;

        int initYmin = confirmedTarget?.Ymin ?? initialBox.ymin;
        int initXmin = confirmedTarget?.Xmin ?? initialBox.xmin;
        int initYmax = confirmedTarget?.Ymax ?? initialBox.ymax;
        int initXmax = confirmedTarget?.Xmax ?? initialBox.xmax;
        int initH = initYmax - initYmin;

        int count = 0;
        for (int i = 0; i < waypoints.Count; i++)
        {
            var wp = waypoints[i];
            if (!wp.Visible || !wp.Cx.HasValue || !wp.Cy.HasValue) continue;

            int cx = wp.Cx.Value;
            int cy = wp.Cy.Value;
            int h = wp.H.GetValueOrDefault(200);
            int ymax = cy + h / 2;

            bool isLocalPlayerSpatial = ymax >= 720 && cx >= 240 && cx <= 660 && h >= 260;
            if (isLocalPlayerSpatial)
            {
                if (initH < 260 || initYmax < 720)
                {
                    consoleLog?.Invoke($"Waypoint at t={wp.Time.ToString("0.00", CultureInfo.InvariantCulture)}s ({cx},{cy}) matched 3rd-person local player profile. Suppressing waypoint to hold target trajectory.");
                    waypoints[i] = wp with { Visible = false, Cx = null, Cy = null, W = null, H = null };
                    count++;
                }
            }
        }
        return count;
    }
}

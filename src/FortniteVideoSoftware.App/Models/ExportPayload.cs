using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Models;

public record ExportPayload
{
    public string InputPath { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public double TrimStartMs { get; init; }
    public double TrimEndMs { get; init; }
    public double LoadedVideoDurationMs { get; init; }
    public double BaseSpeed { get; init; }
    public bool ThumbnailSet { get; init; }
    public double ThumbnailPosMs { get; init; }
    public double ThumbnailDurationSec { get; init; }
    
    public string? VoiceOverWavPath { get; init; }
    public double VoiceOverStartSec { get; init; }
    public List<VoiceOverTake>? VoiceOverTakes { get; init; }
    public bool VoiceOverDuckAudio { get; init; }
    
    public string HardwareMode { get; init; } = "Auto";
    public double? SourceMeasuredLufs { get; init; }
    public bool? ApplyLoudnessNormalization { get; init; }
    public bool? ApplyPeakFlattening { get; init; }
    public bool IsMobileFormat { get; init; }
    public bool IsBossHp { get; init; }
    public bool EnableFades { get; init; }
    public bool ShowTeammates { get; init; }
    public bool ShowSpectating { get; init; }
    public string? MemeFile { get; init; }

    /// <summary>
    /// MEME_03 — several memes, each at a chosen moment (CLIP-RELATIVE source seconds, already
    /// snapped per D8). Empty means "fall back to the legacy single-meme fields".
    /// </summary>
    public List<FortniteVideoSoftware.Core.Media.MemePlacement>? MemePlacements { get; init; }

    /// <summary>MEME_02 — play the meme before the gameplay instead of after it.</summary>
    public bool MemeAtStart { get; init; }
    public string? PortraitText { get; init; }
    public List<SpeedSegment>? SpeedSegments { get; init; }

    /// <summary>
    /// CUT_01 — sections deleted from the middle of the clip, in ABSOLUTE source milliseconds
    /// (the same frame of reference as <see cref="SpeedSegments"/> and the trim points).
    /// Null or empty means one unbroken clip, which is the historical behaviour.
    /// </summary>
    public List<CutRange>? Cuts { get; init; }
    
    public int QualityLevel { get; init; } = 7;
    public double? TargetMbOverride { get; init; }
    public bool MusicLeadFadeIn { get; init; } = true;
    public bool MusicTailFadeOut { get; init; } = true;
    public List<MusicTrack>? MusicTracks { get; init; }
    public JsonObject? MusicConfig { get; init; }
    public bool KeepMusicDuringMeme { get; init; }
}

public record ExportResult
{
    public bool Success { get; init; }
    public bool Canceled { get; init; }
    public string? OutputPath { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Warning { get; init; }
}

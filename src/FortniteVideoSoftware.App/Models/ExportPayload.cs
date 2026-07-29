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
    
    // Voice Over
    public string? VoiceOverWavPath { get; init; }
    public double VoiceOverStartSec { get; init; }
    public List<VoiceOverTake>? VoiceOverTakes { get; init; }
    public bool VoiceOverMuteMale { get; init; }
    public bool VoiceOverMuteFemale { get; init; }
    public bool VoiceOverMuteChild { get; init; }
    public double VoiceOverMuteMaleHz { get; init; }
    public double VoiceOverMuteFemaleHz { get; init; }
    public double VoiceOverMuteChildHz { get; init; }
    
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
    public string? PortraitText { get; init; }
    public List<SpeedSegment>? SpeedSegments { get; init; }
    
    // Missing wires
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

// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App.Services;

/// <summary>GRANULARPERF_01 — UI-owned timeline snapshots; unchanged reads allocate no strings or lists.</summary>
public sealed class EditorTimelineCache
{
    private OutputTimeline? _timeline;
    private SpeedSegment[] _segments = [];
    private CutRange[] _cuts = [];
    private MemePlacement[] _memes = [];
    private double _duration, _speed, _freezeStart, _freezeDuration;

    public void Clear() => _timeline = null;

    public OutputTimeline Get(double durationMs, IReadOnlyList<SpeedSegment> segments,
        IReadOnlyList<CutRange> cuts, IReadOnlyList<MemePlacement> memes,
        double speed = 1, double freezeStartMs = -1, double freezeDurationSeconds = 0)
    {
        if (_timeline != null && _duration == durationMs && _speed == speed &&
            _freezeStart == freezeStartMs && _freezeDuration == freezeDurationSeconds &&
            Equal(_segments, segments) && Equal(_cuts, cuts) && Equal(_memes, memes)) return _timeline;

        _duration = durationMs; _speed = speed;
        _freezeStart = freezeStartMs; _freezeDuration = freezeDurationSeconds;
        _segments = segments.ToArray(); _cuts = cuts.ToArray(); _memes = memes.ToArray();
        var effective = new List<SpeedSegment>(_segments);
        if (freezeStartMs >= 0 && freezeDurationSeconds > 0)
            effective.Add(new(freezeStartMs, freezeStartMs + freezeDurationSeconds * 1000, 0));
        _timeline = OutputTimeline.Create(durationMs, effective, speed, 0,
            MemePlacement.ToInsertions(_memes), CutRange.ToClipRelative(_cuts, 0));
        return _timeline;
    }

    private static bool Equal<T>(T[] saved, IReadOnlyList<T> current)
    {
        if (saved.Length != current.Count) return false;
        for (int i = 0; i < saved.Length; i++)
            if (!EqualityComparer<T>.Default.Equals(saved[i], current[i])) return false;
        return true;
    }
}

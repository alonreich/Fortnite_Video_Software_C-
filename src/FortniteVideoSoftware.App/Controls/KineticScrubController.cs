using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Momentum & inertia physics controller for the timeline scrubber.
/// Samples pointer velocity during drag; on release, enters a fling phase
/// with friction decay until |v| < epsilon or a boundary is hit.
/// Uses a fixed 60Hz DispatcherTimer (mirrors FluidVolumeSlider pattern).
/// Honors the Playhead-Follow Rule: fling only seeks when PAUSED.
/// </summary>
public sealed class KineticScrubController : IDisposable
{
    private const double PhysicsHz = 60.0;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.0 / PhysicsHz);
    private const double FrictionPerTick = 0.92;
    private const double MinVelocityMs = 2.0;
    private const double VelocityThreshold = 30.0;

    private readonly DispatcherTimer _timer;
    private double _velocityMsPerTick;
    private double _currentMs;
    private double _minMs;
    private double _maxMs;

    private bool _isFlinging;
    private bool _disposed;

    /// <summary>
    /// Called every physics tick with the new seek target (in milliseconds).
    /// The consumer should clamp and apply the seek.
    /// </summary>
    public event Action<double>? SeekRequested;

    /// <summary>Fired when the fling has fully settled.</summary>
    public event Action? FlingSettled;

    /// <summary>Whether a fling animation is currently active.</summary>
    public bool IsFlinging => _isFlinging;

    public KineticScrubController()
    {
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Call on PointerMoved during an active drag to sample velocity.
    /// </summary>
    /// <param name="currentMs">Current timeline position in ms.</param>
    /// <param name="deltaMs">Delta from last sample (ms).</param>
    public void FeedDragSample(double currentMs, double deltaMs)
    {
        _velocityMsPerTick = _velocityMsPerTick * 0.6 + deltaMs * 0.4;
        _currentMs = currentMs;
    }

    /// <summary>
    /// Call on PointerReleased to potentially start a fling.
    /// </summary>
    /// <param name="currentMs">Position at release in ms.</param>
    /// <param name="minMs">Timeline lower bound (usually 0).</param>
    /// <param name="maxMs">Timeline upper bound (video duration in ms).</param>
    /// <param name="isPlaying">If true, the fling is suppressed (Playhead-Follow Rule).</param>
    public void Release(double currentMs, double minMs, double maxMs, bool isPlaying)
    {
        _currentMs = currentMs;
        _minMs = minMs;
        _maxMs = maxMs;

        if (isPlaying)
        {
            _velocityMsPerTick = 0;
            return;
        }

        if (Math.Abs(_velocityMsPerTick) < VelocityThreshold)
        {
            _velocityMsPerTick = 0;
            return;
        }

        _isFlinging = true;
        _timer.Start();
    }

    /// <summary>Immediately cancel any active fling.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _isFlinging = false;
        _velocityMsPerTick = 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isFlinging) return;

        _currentMs += _velocityMsPerTick;

        if (_currentMs <= _minMs)
        {
            _currentMs = _minMs;
            _velocityMsPerTick = 0;
        }
        else if (_currentMs >= _maxMs)
        {
            _currentMs = _maxMs;
            _velocityMsPerTick = 0;
        }

        SeekRequested?.Invoke(_currentMs);

        _velocityMsPerTick *= FrictionPerTick;

        if (Math.Abs(_velocityMsPerTick) < MinVelocityMs)
        {
            _velocityMsPerTick = 0;
            _isFlinging = false;
            _timer.Stop();
            FlingSettled?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
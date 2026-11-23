namespace QuickClip.Shell;

internal enum CaptureHealthState { Healthy, MissingFrames }

/// <summary>
/// Debounces native recorder throughput and missed capture timeline slots.
/// Uses a capture epoch to prevent stale counters from another graph turning
/// the HUD red; frame loss must be sustained or severe to be visible.
/// </summary>
internal sealed class CaptureHealthSignal
{
    private long _epoch = -1;
    private long _startedAt;
    private long _previousAt;
    private long _previousDropped;
    private int _badIntervals;
    private int _goodIntervals;

    internal CaptureHealthState State { get; private set; } = CaptureHealthState.Healthy;

    internal void Reset(long epoch, long now)
    {
        _epoch = epoch;
        _startedAt = now;
        _previousAt = 0;
        _previousDropped = 0;
        _badIntervals = 0;
        _goodIntervals = 0;
        State = CaptureHealthState.Healthy;
    }

    /// <returns>True when the indicator state changed.</returns>
    internal bool Observe(long epoch, long now, bool running, double actualFps,
        int targetFps, long totalMissingSlots)
    {
        if (epoch != _epoch)
        {
            Reset(epoch, now);
            return false;
        }
        if (!running || targetFps < 15 || !double.IsFinite(actualFps) ||
            actualFps < 0 || totalMissingSlots < 0)
        {
            _previousAt = 0;
            _badIntervals = _goodIntervals = 0;
            return false;
        }
        if (_previousAt == 0 || now <= _previousAt || now - _previousAt > 5000 ||
            totalMissingSlots < _previousDropped)
        {
            _previousAt = now;
            _previousDropped = totalMissingSlots;
            _badIntervals = _goodIntervals = 0;
            return false;
        }

        double seconds = (now - _previousAt) / 1000d;
        long missed = totalMissingSlots - _previousDropped;
        _previousAt = now;
        _previousDropped = totalMissingSlots;
        if (now - _startedAt < 2000 || seconds < 0.7)
            return false; // Startup and timer jitter are not visible degradation.

        bool severe = missed >= Math.Max(15, (int)Math.Ceiling(targetFps * seconds * .25));
        bool bad = missed >= Math.Max(6, (int)Math.Ceiling(targetFps * seconds * .1))
            || actualFps < targetFps * .82;

        if (bad) { _badIntervals++; _goodIntervals = 0; }
        else { _goodIntervals++; _badIntervals = 0; }

        var next = State;
        if (severe || _badIntervals >= 2) next = CaptureHealthState.MissingFrames;
        else if (_goodIntervals >= 3) next = CaptureHealthState.Healthy;
        if (next == State) return false;
        State = next;
        return true;
    }
}

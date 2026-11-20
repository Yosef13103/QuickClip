namespace QuickClip.Recording.Companion;

// An observational signal, never capture authorization. No Windows dependencies.
internal enum ActivityState { Unknown, Idle, Joining, LikelyConnected, Leaving }
internal readonly record struct Observation(bool Complete, bool DiscordPresent, bool SameOwner, TimeSpan At);

internal sealed class ActivityClassifier
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan JoinDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(4);
    private TimeSpan? _last, _since;
    public ActivityState State { get; private set; } = ActivityState.Unknown;

    public ActivityState Reset()
    {
        _last = _since = null;
        return State = ActivityState.Unknown;
    }

    public ActivityState Update(Observation sample)
    {
        if (!sample.Complete || (sample.SameOwner && !sample.DiscordPresent))
            return Reset();
        if (_last is { } last && (sample.At <= last || sample.At - last > MaxGap))
            Reset();
        _last = sample.At;
        if (!sample.DiscordPresent)
        {
            _since = null;
            return State = ActivityState.Idle;
        }
        if (sample.SameOwner)
        {
            if (State is ActivityState.LikelyConnected or ActivityState.Leaving)
            {
                _since = null;
                return State = ActivityState.LikelyConnected;
            }
            if (State != ActivityState.Joining)
            {
                _since = sample.At;
                return State = ActivityState.Joining;
            }
            if (sample.At - _since!.Value >= JoinDelay)
            {
                _since = null;
                return State = ActivityState.LikelyConnected;
            }
        }
        else
        {
            if (State is ActivityState.LikelyConnected)
            {
                _since = sample.At;
                return State = ActivityState.Leaving;
            }
            if (State is ActivityState.Leaving && sample.At - _since!.Value < LeaveDelay)
                return State;
            _since = null;
            return State = ActivityState.Idle;
        }
        return State;
    }
}

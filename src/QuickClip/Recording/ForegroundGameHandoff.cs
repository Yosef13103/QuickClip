namespace QuickClip.Recording;

/// <summary>Confirms a different foreground game before replacing a still-eligible capture source.</summary>
internal sealed class ForegroundGameHandoff(TimeProvider? clock = null)
{
    private static readonly TimeSpan FocusDwell = TimeSpan.FromMilliseconds(500);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private WindowOwner? _pendingOwner;
    private string? _pendingDisplay;
    private long _pendingSince;

    internal bool PreferForeground(GameCaptureObservation? retained, GameCaptureObservation? foreground,
        Func<GameCaptureObservation, string?> authorizedDisplay)
    {
        if (foreground == null || retained == null || foreground.Owner == retained.Owner)
        {
            Reset();
            return true;
        }

        string? display = authorizedDisplay(foreground);
        if (display == null)
        {
            Reset();
            return false;
        }

        if (authorizedDisplay(retained) == null)
        {
            Reset();
            return true;
        }

        if (_pendingOwner != foreground.Owner ||
            !string.Equals(_pendingDisplay, display, StringComparison.OrdinalIgnoreCase))
        {
            (_pendingOwner, _pendingDisplay, _pendingSince) = (foreground.Owner, display, _clock.GetTimestamp());
            return false;
        }

        return _clock.GetElapsedTime(_pendingSince) >= FocusDwell;
    }

    internal void Reset() => _pendingOwner = null;
}

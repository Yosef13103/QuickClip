namespace QuickClip.Recording;

public enum RecordingMode { Off, Armed, GameTriggered, ManualDesktop, Unavailable, Recovering }

internal sealed record CaptureDisplay(string Device, string Identity);

internal sealed record CaptureActivation(CaptureDisplay Display, WindowOwner? GameOwner = null, string? GameId = null)
{
    public RecordingMode Mode => GameOwner.HasValue ? RecordingMode.GameTriggered : RecordingMode.ManualDesktop;
}

/// <summary>Owns one graph and its replay buffer. The recorder serializes commands and observations.</summary>
internal sealed class RecordingLifecycle(Func<CaptureActivation, string?> start, Action stop, TimeProvider? clock = null,
    Func<bool>? canRetry = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CaptureActivation? _active;
    private CaptureActivation? _lastGame;
    private CaptureActivation? _failed;
    private string? _failure;
    private long _failedAt;
    private int _retrySeconds;
    public RecordingMode Mode { get; private set; }
    public string? Error { get; private set; }

    public void Reconcile(bool enabled, string? manualIdentity, GameCaptureObservation? game,
        IReadOnlyList<MonitorInfo> displays, Func<WindowOwner, string?> windowDisplay, Func<string, bool> allowsGameDisplay)
    {
        CaptureActivation? desired = null;
        string? unavailable = null;
        if (!enabled || (game?.VerifiedTarget != null && _lastGame?.GameOwner is { } prior &&
            (prior.ProcessId != game.Owner.ProcessId || prior.Started != game.Owner.Started || _lastGame.GameId != game.Match.Entry?.Id)))
            _lastGame = null;

        if (enabled && manualIdentity != null)
        {
            if (Monitors.FindPhysicalDisplay(displays, manualIdentity) is { } display)
                desired = new(new(display.DeviceName, manualIdentity));
            else unavailable = "The authorized desktop display is disconnected.";
        }
        else if (enabled && game?.VerifiedTarget != null)
        {
            // Windows may relocate a game when its display disconnects. Hold the prior
            // physical identity until it returns or this game session ends.
            if (_lastGame is { } last && Monitors.FindPhysicalDisplay(displays, last.Display.Identity) == null)
                unavailable = "The game display disconnected. Reconnect it or restart the game to resume capture.";
            else if (CaptureSourcePolicy.ResolveGameDisplay(game, displays, windowDisplay) is not { CaptureIdentity: { } identity } display)
                unavailable = "QuickClip cannot verify which physical display contains the game.";
            else if (!allowsGameDisplay(identity))
                unavailable = "The game's entire display needs permission. Open Settings → Recording to allow display following.";
            else desired = new(new(display.DeviceName, identity), game.Owner, game.Match.Entry?.Id);
        }

        if (desired != _active) StopGraph();
        if (desired != _failed) ClearFailure();
        Error = unavailable;

        if (!enabled) Mode = RecordingMode.Off;
        else if (desired == null) Mode = unavailable == null ? RecordingMode.Armed : RecordingMode.Unavailable;
        else if (desired == _failed && _active == null &&
            (_retrySeconds < 0 || _clock.GetElapsedTime(_failedAt) < TimeSpan.FromSeconds(_retrySeconds)))
        {
            Error = _failure;
            Mode = _retrySeconds < 0 ? RecordingMode.Unavailable : RecordingMode.Recovering;
        }
        else
        {
            if (_active == null)
            {
                Error = start(desired);
                if (Error == null) _active = desired;
                else ScheduleRetry(desired, Error);
            }
            if (_active?.GameOwner != null) _lastGame = _active;
            if (Error == null) Mode = desired.Mode;
        }
    }

    // A source can move away and back between polls. Native video invalidates the
    // old graph on the first mismatch, so even the same desired source needs a new buffer.
    public void InvalidateGraph() => StopGraph();

    public void FailActive(string message)
    {
        if (_active == null) return;
        var failed = _active;
        StopGraph();
        ScheduleRetry(failed, message);
    }

    public void MarkHealthy() => ClearFailure();

    private void ScheduleRetry(CaptureActivation failed, string message)
    {
        _retrySeconds = canRetry?.Invoke() == false ? -1 : Math.Min(_retrySeconds <= 0 ? 2 : _retrySeconds * 2, 30);
        (_failed, _failure, _failedAt) = (failed, message, _clock.GetTimestamp());
        Error = message;
        Mode = _retrySeconds < 0 ? RecordingMode.Unavailable : RecordingMode.Recovering;
    }

    private void ClearFailure() => (_failed, _failure, _retrySeconds) = (null, null, 0);

    public void Reset(bool forgetGame = true)
    {
        StopGraph();
        ClearFailure();
        if (forgetGame) _lastGame = null;
        Error = null;
        Mode = RecordingMode.Off;
    }

    private void StopGraph()
    {
        if (_active == null) return;
        stop();
        _active = null;
    }
}

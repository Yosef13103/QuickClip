namespace QuickClip.Recording;

public enum RecordingMode { Off, Armed, GameTriggered, ManualDesktop, Unavailable }

internal sealed record CaptureDisplay(string Device, string Identity);

internal sealed record CaptureActivation(CaptureDisplay Display, WindowOwner? GameOwner = null, string? GameId = null)
{
    public RecordingMode Mode => GameOwner.HasValue ? RecordingMode.GameTriggered : RecordingMode.ManualDesktop;
}

/// <summary>Owns one graph and its replay buffer. The recorder serializes commands and observations.</summary>
internal sealed class RecordingLifecycle(Func<CaptureActivation, string?> start, Action stop)
{
    private CaptureActivation? _active;
    private CaptureActivation? _lastGame;
    private CaptureActivation? _failed;
    private string? _failure;
    public RecordingMode Mode { get; private set; }
    public string? Error { get; private set; }

    public void Reconcile(bool enabled, string? manualIdentity, GameCaptureObservation? game,
        IReadOnlyList<MonitorInfo> displays, Func<WindowOwner, string?> windowDisplay, Func<string, bool> allowsGameDisplay)
    {
        CaptureActivation? desired = null;
        string? unavailable = null;
        if (!enabled || _lastGame?.GameOwner != game?.Owner || _lastGame?.GameId != game?.Match.Entry?.Id)
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
        if (desired != _failed) (_failed, _failure) = (null, null);
        Error = unavailable;

        if (!enabled) Mode = RecordingMode.Off;
        else if (desired == null) Mode = unavailable == null ? RecordingMode.Armed : RecordingMode.Unavailable;
        else if (desired == _failed)
        {
            Error = _failure;
            Mode = RecordingMode.Unavailable;
        }
        else
        {
            if (_active == null)
            {
                Error = start(desired);
                if (Error == null) _active = desired;
                else (_failed, _failure) = (desired, Error);
            }
            if (_active?.GameOwner != null) _lastGame = _active;
            Mode = Error == null ? desired.Mode : RecordingMode.Unavailable;
        }
    }

    // A source can move away and back between polls. Native video invalidates the
    // old graph on the first mismatch, so even the same desired source needs a new buffer.
    public void InvalidateGraph() => StopGraph();

    public void FailActive(string message)
    {
        if (_active == null) return;
        (_failed, _failure) = (_active, message);
        StopGraph();
        Error = message;
        Mode = RecordingMode.Unavailable;
    }

    public void Reset()
    {
        StopGraph();
        (_failed, _failure, _lastGame) = (null, null, null);
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

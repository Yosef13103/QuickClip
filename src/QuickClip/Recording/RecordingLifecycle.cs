namespace QuickClip.Recording;

public enum RecordingMode { Off, Armed, GameTriggered, ManualDesktop, Unavailable }

internal sealed record CaptureActivation(string Display, WindowOwner? GameOwner = null, string? GameId = null)
{
    public RecordingMode Mode => GameOwner.HasValue ? RecordingMode.GameTriggered : RecordingMode.ManualDesktop;
}

/// <summary>Owns one graph. Callers serialize observations and commands on the recorder gate.</summary>
internal sealed class RecordingLifecycle(Func<CaptureActivation, string?> start, Action stop)
{
    private CaptureActivation? _active;
    private CaptureActivation? _failed;
    private string? _failure;
    public RecordingMode Mode { get; private set; }
    public string? Error { get; private set; }

    public void Reconcile(bool enabled, bool armed, string? gameDisplay, string? manualDisplay,
        IReadOnlyList<GameCaptureObservation> games, Func<string, bool> displayAvailable, string gameUnavailable)
    {
        var game = armed
            ? games.FirstOrDefault(x => x.Owner == _active?.GameOwner && x.Match.Entry?.Id == _active?.GameId)
                ?? games.FirstOrDefault()
            : null;
        CaptureActivation? desired = !enabled ? null : manualDisplay is { Length: > 0 } display ? new(display)
            : game != null && gameDisplay is { Length: > 0 } ? new(gameDisplay, game.Owner, game.Match.Entry?.Id) : null;

        if (desired != _active && _active != null) StopGraph();
        if (desired != _failed)
        {
            _failed = null;
            _failure = null;
        }
        Error = null;
        if (!enabled) Mode = RecordingMode.Off;
        else if (desired == null)
        {
            Mode = game == null ? RecordingMode.Armed : RecordingMode.Unavailable;
            if (game != null) Error = gameUnavailable;
        }
        else if (!displayAvailable(desired.Display))
        {
            StopGraph();
            Mode = RecordingMode.Unavailable;
            Error = "The authorized display is unavailable. No other display will be recorded.";
        }
        else if (desired == _failed)
        {
            Mode = RecordingMode.Unavailable;
            Error = _failure;
        }
        else
        {
            if (_active == null)
            {
                Error = start(desired);
                if (Error == null) _active = desired;
                else (_failed, _failure) = (desired, Error);
            }
            Mode = Error == null ? desired.Mode : RecordingMode.Unavailable;
        }
    }

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
        (_failed, _failure) = (null, null);
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

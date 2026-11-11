namespace QuickClip.Recording;

/// <summary>Session-relative monotonic timestamps and the encoded frame that anchors the bookmark.</summary>
public sealed record LiveReplayMarker(Guid Id, Guid SessionId, long SourceId, TimeSpan EventTime,
    TimeSpan AnchorTime, string IntendedGame, string IntendedSource, string? Label);

public sealed record SavedReplayMarker(LiveReplayMarker Marker, TimeSpan Offset);

public enum ReplayCaptureBackend { DesktopDuplication, WindowsGraphicsCapture }

/// <summary>Source identity verified by the fixed monitor policy. An unknown legacy source is left unset.</summary>
public sealed record ReplayDesktopSource(string DisplayDevice, ReplayCaptureBackend Backend);

public sealed record ReplayVideoOutput(int Width, int Height, double? AverageFps);

/// <summary>Metadata for a published clip. The available span runs from the oldest usable keyframe to the latest retained video frame.</summary>
public sealed record ReplaySaveReceipt(string Path, string Game, Guid SessionId, int RequestedSeconds,
    TimeSpan? ActualVideoDuration, TimeSpan AvailableVideoSpan, TimeSpan FirstFrameTime, TimeSpan LastFrameTime,
    long FirstSourceId, long LastSourceId, DateTimeOffset? CapturedAt, IReadOnlyList<SavedReplayMarker> Markers)
{
    public ReplayDesktopSource? DesktopSource { get; init; }
    public string? GameContext { get; init; }
    public string SourceDescription => DescribeSource(DesktopSource, SpansCaptureEpochs);

    internal static string DescribeSource(ReplayDesktopSource? desktop, bool spansEpochs) =>
        desktop != null ? desktop.Backend == ReplayCaptureBackend.DesktopDuplication
            ? "Entire display (Desktop Duplication)" : "Entire display (Windows Graphics Capture)"
        : spansEpochs ? "Mixed or unverified sources" : "Unverified source";
    public ReplayVideoOutput? VideoOutput { get; init; }
    public bool SpansCaptureEpochs => FirstSourceId != LastSourceId;
    public bool? IsShorterThanRequested => ActualVideoDuration is TimeSpan duration ? duration < TimeSpan.FromSeconds(RequestedSeconds) : null;
}

/// <summary>Bounded session metadata. The caller serializes access with recorder lifecycle changes.</summary>
internal sealed class ReplayMarkers
{
    internal const int Capacity = 128; // matches the native receipt ABI
    internal const int MaxLabelLength = 128;
    internal sealed record Candidate(long Sequence, LiveReplayMarker Marker);

    private readonly List<Candidate> _markers = [];

    public void Clear() => _markers.Clear();

    public LiveReplayMarker? Add(CaptureEngine.ReplayPosition position, Guid sessionId, string game, string source, string? label)
    {
        label = label?.Trim();
        if (label?.Length > MaxLabelLength)
            throw new ArgumentException($"A replay marker label can have up to {MaxLabelLength} characters.", nameof(label));
        if (position.Sequence == 0) return null;
        Prune(position, sessionId);
        if (_markers.Count == Capacity) _markers.RemoveAt(0);
        var marker = new LiveReplayMarker(Guid.NewGuid(), sessionId, position.Source, Time(position.EventUs),
            Time(position.FrameUs), game, source, string.IsNullOrEmpty(label) ? null : label);
        _markers.Add(new(position.Sequence, marker));
        return marker;
    }

    public Candidate[] Snapshot(CaptureEngine.ReplayPosition position, Guid sessionId)
    {
        Prune(position, sessionId);
        return _markers.ToArray();
    }

    private void Prune(CaptureEngine.ReplayPosition position, Guid sessionId) =>
        _markers.RemoveAll(item => item.Marker.SessionId != sessionId || item.Sequence < position.FirstSequence);

    internal static IReadOnlyList<SavedReplayMarker> Match(Candidate[] candidates, CaptureEngine.ReplayAnchor[] anchors, TimeSpan? duration)
    {
        var markers = new List<SavedReplayMarker>();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (anchors[i].Sequence != candidates[i].Sequence || anchors[i].OffsetUs < 0) continue;
            TimeSpan offset = Time(anchors[i].OffsetUs);
            if (duration.HasValue && offset >= duration.Value) continue;
            markers.Add(new(candidates[i].Marker, offset));
        }
        return markers.AsReadOnly();
    }

    internal static TimeSpan Time(long microseconds) => TimeSpan.FromTicks(microseconds * 10);
}

/// <summary>Rejects a second save while the first is preparing, muxing or publishing its clip.</summary>
internal sealed class ReplaySaveGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<T?> RunAsync<T>(Func<Task<T?>> save, CancellationToken cancellationToken, Action? onBusy = null)
        where T : struct
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            onBusy?.Invoke();
            return null;
        }
        try { return await save(); }
        finally { _gate.Release(); }
    }
}

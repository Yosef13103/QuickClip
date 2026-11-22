namespace QuickClip.Recording;

// Values match EDataFlow in the Windows SDK and the capture DLL ABI.
internal enum EndpointFlow { Playback, Microphone }

internal sealed record AudioEndpoint(EndpointFlow Flow, string Id, string Name, bool IsDefault);

internal sealed record EndpointTrack(EndpointFlow Flow, string DeviceId, string Label, bool Enabled = true,
    int DefaultMixPercent = 100);
internal enum EndpointStage { Pending, Com, EndpointLookup, EndpointFlow, Activate, Initialize, Event, Start, Streaming, Read, Complete }

internal sealed record EndpointStatus(int TrackIndex, EndpointFlow Flow, EndpointStage Stage, int HResult,
    int MixRate, int MixChannels, int CaptureRate, long PacketCount, long? MillisecondsSincePacket, float? LastPacketPeak)
{
    public string MixFormat { get; init; } = "";
    public int CaptureChannels { get; init; }
    public long SilentPackets { get; init; }
    public long TimestampFallbacks { get; init; }
    public long Discontinuities { get; init; }
    public float? LastPacketRms { get; init; }
    public float MaxPacketPeak { get; init; }
    public float MaxPacketRms { get; init; }
}
internal sealed record RunningEndpointLane(EndpointTrack Track, EndpointStatus? Status);

internal static class EndpointAudio
{
    public const int MaxTracks = 16;

    internal static AudioEndpoint? FindEndpoint(EndpointTrack track, IEnumerable<AudioEndpoint> endpoints) =>
        endpoints.FirstOrDefault(endpoint => SameDevice(track.Flow, track.DeviceId, endpoint.Flow, endpoint.Id));

    internal static bool SameDevice(EndpointFlow leftFlow, string leftId, EndpointFlow rightFlow, string rightId) =>
        leftFlow == rightFlow && StringComparer.OrdinalIgnoreCase.Equals(leftId, rightId);

    internal static RunningEndpointLane[] RunningLanes(IReadOnlyList<EndpointTrack> plan, IReadOnlyList<EndpointStatus> statuses) =>
        plan.Select((track, index) => new RunningEndpointLane(track,
            statuses.FirstOrDefault(status => status.TrackIndex == index && status.Flow == track.Flow))).ToArray();

    internal static IReadOnlyList<AudioEndpoint> ForFlow(IEnumerable<AudioEndpoint> endpoints, EndpointFlow flow) =>
        endpoints.Where(endpoint => endpoint.Flow == flow).ToArray();

    // The compact UI groups playback and microphones visually. Keep actual
    // native track order except for swapping the two requested same-flow entries.
    internal static List<EndpointTrack> ReorderWithinFlow(
        IReadOnlyList<EndpointTrack> source, EndpointTrack selected, int direction)
    {
        var tracks = source.ToList();
        if (direction is not (-1 or 1)) return tracks;
        int from = tracks.FindIndex(t => SameDevice(t.Flow, t.DeviceId, selected.Flow, selected.DeviceId));
        if (from < 0) return tracks;
        int[] siblings = Enumerable.Range(0, tracks.Count)
            .Where(i => tracks[i].Flow == selected.Flow).ToArray();
        int position = Array.IndexOf(siblings, from) + direction;
        if (position < 0 || position >= siblings.Length) return tracks;
        int to = siblings[position];
        (tracks[from], tracks[to]) = (tracks[to], tracks[from]);
        return tracks;
    }

    // Keep opposite-flow slots unchanged, moving full tracks and saved mix values.
    internal static List<EndpointTrack> MoveWithinFlow(
        IReadOnlyList<EndpointTrack> source, EndpointTrack selected, EndpointTrack target, bool after)
    {
        var tracks = source.ToList();
        if (selected.Flow != target.Flow ||
            SameDevice(selected.Flow, selected.DeviceId, target.Flow, target.DeviceId)) return tracks;
        var slots = Enumerable.Range(0, tracks.Count).Where(i => tracks[i].Flow == selected.Flow).ToArray();
        var siblings = slots.Select(i => tracks[i]).ToList();
        int from = siblings.FindIndex(t => SameDevice(t.Flow, t.DeviceId, selected.Flow, selected.DeviceId));
        int to = siblings.FindIndex(t => SameDevice(t.Flow, t.DeviceId, target.Flow, target.DeviceId));
        if (from < 0 || to < 0) return tracks;
        var moved = siblings[from];
        siblings.RemoveAt(from);
        to = siblings.FindIndex(t => SameDevice(t.Flow, t.DeviceId, target.Flow, target.DeviceId));
        siblings.Insert(to + (after ? 1 : 0), moved);
        for (int i = 0; i < slots.Length; i++) tracks[slots[i]] = siblings[i];
        return tracks;
    }

    public static List<EndpointTrack> Normalize(IEnumerable<EndpointTrack?>? tracks)
    {
        var result = new List<EndpointTrack>();
        // Windows endpoint IDs identify one endpoint and the native start API rejects duplicate IDs.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks ?? [])
        {
            if (track is null || !Enum.IsDefined(track.Flow) || string.IsNullOrWhiteSpace(track.DeviceId) ||
                track.DeviceId.Contains('\0') || !ids.Add(track.DeviceId))
                continue;
            result.Add(track with { DefaultMixPercent = Math.Clamp(track.DefaultMixPercent, 0, 200) });
            if (result.Count == MaxTracks) break;
        }
        return result;
    }

    /// <summary>A non-null empty plan means video only. Null selects legacy Desktop/mic routing.</summary>
    public static IReadOnlyList<EndpointTrack>? Plan(AppSettings settings)
    {
        if (!settings.UseAudioEndpoints) return null;
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<EndpointTrack>();
        foreach (var track in Normalize(settings.EndpointTracks).Where(t => t.Enabled))
        {
            string name = string.Concat((track.Label ?? "").Where(c => !char.IsControl(c))).Trim();
            if (name.Length == 0) name = track.Flow == EndpointFlow.Playback ? "Playback" : "Mic";
            string title = name;
            for (int suffix = 2; !titles.Add(title); suffix++) title = $"{name} ({suffix})";
            result.Add(track with { Label = title });
        }
        return result;
    }
}

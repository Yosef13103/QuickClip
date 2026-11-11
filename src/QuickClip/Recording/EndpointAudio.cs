namespace QuickClip.Recording;

// Values match EDataFlow in the Windows SDK and the capture DLL ABI.
internal enum EndpointFlow { Playback, Microphone }

internal sealed record AudioEndpoint(EndpointFlow Flow, string Id, string Name, bool IsDefault);

internal sealed record EndpointTrack(EndpointFlow Flow, string DeviceId, string Label, bool Enabled = true);
internal enum EndpointStage { Pending, Com, EndpointLookup, EndpointFlow, Activate, Initialize, Event, Start, Streaming, Read }

internal sealed record EndpointStatus(int TrackIndex, EndpointFlow Flow, EndpointStage Stage, int HResult,
    int MixRate, int MixChannels, int CaptureRate, long PacketCount, long? MillisecondsSincePacket, float? LastPacketPeak);

internal static class EndpointAudio
{
    public const int MaxTracks = 16;

    internal static AudioEndpoint? FindEndpoint(EndpointTrack track, IEnumerable<AudioEndpoint> endpoints) =>
        endpoints.FirstOrDefault(endpoint => endpoint.Flow == track.Flow &&
            string.Equals(endpoint.Id, track.DeviceId, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<AudioEndpoint> ForFlow(IEnumerable<AudioEndpoint> endpoints, EndpointFlow flow) =>
        endpoints.Where(endpoint => endpoint.Flow == flow).ToArray();

    public static List<EndpointTrack> Normalize(IEnumerable<EndpointTrack?>? tracks)
    {
        var result = new List<EndpointTrack>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks ?? [])
        {
            if (track is null || !Enum.IsDefined(track.Flow) || string.IsNullOrWhiteSpace(track.DeviceId) ||
                track.DeviceId.Contains('\0') || !ids.Add(track.DeviceId))
                continue;
            result.Add(track);
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

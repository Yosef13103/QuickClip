using QuickClip.Gallery;
using QuickClip.Media;

namespace QuickClip.Recording;

/// <summary>
/// Captures user mix defaults once per newly saved replay. The encoded source stems remain
/// unchanged; only QuickClip's initial editor/preview/export mix uses these preferences.
/// Imported/older media without an attached snapshot always start at 100%.
/// </summary>
internal static class RecordingAudioMix
{
    internal static ClipAudioMixLevel[]? Capture(
        IReadOnlyList<EndpointTrack> activePlan, IReadOnlyList<EndpointTrack> currentPreferences)
    {
        ArgumentNullException.ThrowIfNull(activePlan);
        ArgumentNullException.ThrowIfNull(currentPreferences);
        if (activePlan.Count == 0) return null;

        // Active plan comes from the specific native graph that produced the file.
        // Match live preferences by endpoint ID/flow, never by editable display name alone.
        var result = new List<ClipAudioMixLevel>(Math.Min(activePlan.Count, EndpointAudio.MaxTracks));
        for (int i = 0; i < activePlan.Count && i < EndpointAudio.MaxTracks; i++)
        {
            var actual = activePlan[i];
            if (!actual.Enabled || string.IsNullOrWhiteSpace(actual.Label)) continue;
            var setting = currentPreferences.FirstOrDefault(candidate =>
                EndpointAudio.SameDevice(actual.Flow, actual.DeviceId, candidate.Flow, candidate.DeviceId));
            int percent = Math.Clamp(setting?.DefaultMixPercent ?? 100, 0, 200);
            result.Add(new ClipAudioMixLevel(i, actual.Label, percent));
        }
        return result.Count == 0 ? null : result.ToArray();
    }

    /// <summary>Only exact ordinal + saved title matches are safe across remuxing/editing.</summary>
    internal static int? ForStream(ClipDetails details, AudioStreamInfo stream)
    {
        if (details.InitialAudioMix is not { Length: > 0 } preset ||
            string.IsNullOrWhiteSpace(stream.Title))
            return null;
        return preset.FirstOrDefault(saved => saved.Ordinal == stream.Ordinal &&
            StringComparer.OrdinalIgnoreCase.Equals(saved.StreamTitle, stream.Title)) is { } match &&
            match.Percent is >= 0 and <= 200 ? match.Percent : null;
    }
}

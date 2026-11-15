using QuickClip.Recording;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Shell;

internal enum MicrophoneIndicatorState { Hidden, Quiet, Active }

/// <summary>All selected inputs must have recent audible captured PCM; one good lane cannot hide a quiet one.</summary>
internal sealed class MicrophoneIndicatorSignal
{
    internal const long HoldMilliseconds = 2500;
    private string[] _selected = [];
    private long _epoch = -1;
    private readonly Dictionary<string, long> _lastSound = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<string> Selected => _selected;
    internal MicrophoneIndicatorState State { get; private set; }

    internal static string[] SelectedMicrophones(AppSettings settings)
    {
        if (!settings.ShowRecordingMicrophoneIndicator) return [];
        return settings.UseAudioEndpoints
            ? [.. EndpointAudio.Normalize(settings.EndpointTracks).Where(track => track.Enabled && track.Flow == EndpointFlow.Microphone).Select(track => track.DeviceId)]
            : settings.RecordMic ? [.. settings.MicDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase)] : [];
    }

    internal void Configure(long epoch, string[] selected)
    {
        if (_epoch == epoch && _selected.SequenceEqual(selected, StringComparer.OrdinalIgnoreCase)) return;
        _epoch = epoch;
        _selected = selected;
        _lastSound.Clear();
        State = selected.Length == 0 ? MicrophoneIndicatorState.Hidden : MicrophoneIndicatorState.Quiet;
    }

    internal MicrophoneIndicatorState Observe(NativeAudioSignal[] signals, int count, long now)
    {
        if (_selected.Length == 0) return State = MicrophoneIndicatorState.Hidden;
        count = Math.Clamp(count, 0, signals.Length);
        bool allActive = true;
        foreach (string id in _selected)
        {
            bool matching = false, healthy = false, audible = false;
            for (int i = 0; i < count; i++)
            {
                var lane = signals[i];
                if (!Matches(lane, id)) continue;
                matching = true;
                if (lane.ActualFlow != (int)EndpointFlow.Microphone || lane.Stage != EndpointStage.Streaming ||
                    lane.HResult < 0 || lane.MillisecondsSincePacket is < 0 or > 500 || lane.PacketCount <= 0)
                    continue;
                healthy = true;
                audible |= MicrophoneSignal.MeterValue(lane.RecentPeak) > 0;
            }
            // A zero-count non-blocking native read can mean a busy engine. Retain only the short hold,
            // never treat missing lanes as successful or reuse sound across recorder epochs.
            if (count > 0 && (!matching || !healthy)) _lastSound.Remove(id);
            else if (audible) _lastSound[id] = now;
            if (!_lastSound.TryGetValue(id, out long last) || now - last > HoldMilliseconds)
                allActive = false;
        }
        return State = allActive ? MicrophoneIndicatorState.Active : MicrophoneIndicatorState.Quiet;
    }

    internal void Reset() => Configure(-1, []);

    private static bool Matches(NativeAudioSignal lane, string id)
    {
        if (lane.Flow != EndpointFlow.Microphone) return false;
        if (MicrophoneSignal.Matches(lane, id)) return true;
        // Legacy configuration can select the Windows default and its exact ID. The plan deduplicates
        // them; when an explicit lane was retained, its resolved ID must equal the actual default.
        return id.Length == 0 && lane.ActualFlow == (int)EndpointFlow.Microphone &&
            !string.IsNullOrEmpty(lane.DefaultCaptureId) &&
            string.Equals(lane.ResolvedId, lane.DefaultCaptureId, StringComparison.OrdinalIgnoreCase);
    }
}

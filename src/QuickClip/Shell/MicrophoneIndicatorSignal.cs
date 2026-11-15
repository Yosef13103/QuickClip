using QuickClip.Recording;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Shell;

internal enum MicrophoneIndicatorState { Hidden, Checking, Quiet, Active }

/// <summary>One startup signal check per capture epoch. Each selected input must independently prove captured PCM.</summary>
internal sealed class MicrophoneIndicatorSignal
{
    internal const long CheckMilliseconds = 10_000;
    private string[] _selected = [];
    private long _epoch = -1;
    private long _startedAt;
    private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<string> Selected => _selected;
    internal MicrophoneIndicatorState State { get; private set; }

    internal static string[] SelectedMicrophones(AppSettings settings)
    {
        return settings.UseAudioEndpoints
            ? [.. EndpointAudio.Normalize(settings.EndpointTracks).Where(track => track.Enabled && track.Flow == EndpointFlow.Microphone).Select(track => track.DeviceId)]
            : settings.RecordMic ? [.. settings.MicDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase)] : [];
    }

    internal void Configure(long epoch, string[] selected, long now)
    {
        if (_epoch == epoch && _selected.SequenceEqual(selected, StringComparer.OrdinalIgnoreCase)) return;
        _epoch = epoch;
        _selected = selected;
        _confirmed.Clear();
        _startedAt = now;
        State = selected.Length == 0 ? MicrophoneIndicatorState.Hidden : MicrophoneIndicatorState.Checking;
    }

    internal MicrophoneIndicatorState Observe(NativeAudioSignal[] signals, int count, long now)
    {
        if (_selected.Length == 0) return State = MicrophoneIndicatorState.Hidden;
        if (State == MicrophoneIndicatorState.Active) return State;
        count = Math.Clamp(count, 0, signals.Length);
        foreach (string id in _selected)
        {
            if (_confirmed.Contains(id)) continue;
            for (int i = 0; i < count; i++)
            {
                var lane = signals[i];
                if (!Matches(lane, id) || lane.ActualFlow != (int)EndpointFlow.Microphone ||
                    lane.Stage != EndpointStage.Streaming || lane.HResult < 0 ||
                    lane.MillisecondsSincePacket is < 0 or > 500 || lane.PacketCount <= 0 ||
                    MicrophoneSignal.MeterValue(lane.RecentPeak) <= 0)
                    continue;
                _confirmed.Add(id);
                break;
            }
        }
        return State = _confirmed.Count == _selected.Length ? MicrophoneIndicatorState.Active :
            now - _startedAt >= CheckMilliseconds ? MicrophoneIndicatorState.Quiet : MicrophoneIndicatorState.Checking;
    }

    internal void Reset() => Configure(-1, [], 0);

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

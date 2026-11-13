using QuickClip.Recording;

namespace QuickClip.Shell;

internal enum CaptureAlertKind { None, Interrupted, Restored }

/// <summary>
/// User-facing capture interruption alerts. Distinct from ordinary clip-save notices:
/// one warning after sustained recovery, a bounded reminder, and one restoration notice.
/// </summary>
internal sealed class CaptureAlertPolicy(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long? _recoveringSince;
    private long? _lastWarning;
    private bool _warned;
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(5);

    internal CaptureAlertKind Observe(RecordingMode mode, bool recordingEnabled, bool hasError)
    {
        if (!recordingEnabled || mode is RecordingMode.Off or RecordingMode.Armed)
        {
            Reset();
            return CaptureAlertKind.None;
        }

        if (mode is RecordingMode.GameTriggered or RecordingMode.ManualDesktop)
        {
            bool restored = _warned;
            Reset();
            return restored ? CaptureAlertKind.Restored : CaptureAlertKind.None;
        }

        if (mode is not (RecordingMode.Recovering or RecordingMode.Unavailable) || !hasError)
            return CaptureAlertKind.None;

        long now = _clock.GetTimestamp();
        if (mode == RecordingMode.Recovering)
        {
            _recoveringSince ??= now;
            if (_clock.GetElapsedTime(_recoveringSince.Value, now) < GracePeriod)
                return CaptureAlertKind.None;
        }
        // Unavailable is non-retrying (e.g. permission/display missing): warn on entry.
        if (_lastWarning is { } previous &&
            _clock.GetElapsedTime(previous, now) < ReminderInterval)
            return CaptureAlertKind.None;
        _lastWarning = now;
        _warned = true;
        return CaptureAlertKind.Interrupted;
    }

    private void Reset()
    {
        _recoveringSince = null;
        _lastWarning = null;
        _warned = false;
    }
}

namespace QuickClip.Recording.Companion;

internal enum DiscordCaptureMode { Disabled, DuringCalls, WhileOpen, Manual }
internal enum DiscordGroupStatus { Disabled, GamePriority, Waiting, Unknown, TooManyWindows, Eligible }
internal sealed record DiscordGroupDecision(DiscordGroupStatus Status, IReadOnlyList<WindowOwner> Targets);

// Pure policy; authorization and observation are separate, and this never starts capture.
// Preserve a stable primary window and existing pop-out order across observations.
internal sealed class DiscordGroupPolicy
{
    internal const int MaxWindows = 4;

    // For a short, incomplete window-probe transition, keep only previously
    // authorized, currently valid HWNDs. Never grant new sources here.
    internal static DiscordGroupDecision? HoldVerifiedSurvivors(
        DiscordGroupDecision? previous, DiscordGroupStatus currentStatus,
        TimeSpan sinceVerified, Func<WindowOwner, bool> stillCurrent)
    {
        if (previous is not { Status: DiscordGroupStatus.Eligible } ||
            currentStatus != DiscordGroupStatus.Unknown ||
            sinceVerified < TimeSpan.Zero || sinceVerified > TimeSpan.FromSeconds(3))
            return null;
        var verified = previous.Targets.Where(stillCurrent).Distinct().ToArray();
        return verified.Length is >= 1 and <= MaxWindows
            ? new(DiscordGroupStatus.Eligible, verified) : null;
    }
    private readonly List<WindowOwner> _order = [];
    private bool _activeCall;

    public DiscordGroupDecision Decide(bool master, DiscordCaptureMode mode, bool appAuthorized,
        bool gameOrManualDesktop, bool manualRequested, ActivityState activity,
        ProbeResult observation, bool fresh, Func<WindowOwner, bool> stillCurrent)
    {
        DiscordGroupDecision Clear(DiscordGroupStatus status)
        {
            _order.Clear();
            _activeCall = false;
            return new(status, []);
        }
        if (!master || !appAuthorized || mode == DiscordCaptureMode.Disabled)
            return Clear(DiscordGroupStatus.Disabled);
        if (gameOrManualDesktop) return Clear(DiscordGroupStatus.GamePriority);
        if (!fresh || !observation.Complete || observation.Windows == null ||
            observation.Windows != observation.Targets.Length)
            return new(DiscordGroupStatus.Unknown, []); // retain previously verified order
        if (mode == DiscordCaptureMode.DuringCalls)
        {
            if (activity == ActivityState.Unknown) return Clear(DiscordGroupStatus.Unknown);
            // Leaving retains only an already eligible call through its debounce.
            if (activity != ActivityState.LikelyConnected && !(activity == ActivityState.Leaving && _activeCall))
                return Clear(DiscordGroupStatus.Waiting);
        }
        else if (mode == DiscordCaptureMode.Manual && !manualRequested)
            return Clear(DiscordGroupStatus.Waiting);
        else if (mode is not (DiscordCaptureMode.WhileOpen or DiscordCaptureMode.Manual))
            return Clear(DiscordGroupStatus.Disabled);

        var targets = observation.Targets.Distinct().ToArray();
        if (targets.Length != observation.Targets.Length ||
            targets.Any(t => t.Handle == IntPtr.Zero || t.Started == 0 || t.ProcessId == 0 ||
                t.ThreadId == 0 || !stillCurrent(t)))
            return Clear(DiscordGroupStatus.Unknown);
        if (targets.Length == 0) return Clear(DiscordGroupStatus.Waiting);
        if (targets.Length > MaxWindows) return Clear(DiscordGroupStatus.TooManyWindows);
        // Do not silently reuse a slot if the same HWND has a changed owner.
        if (_order.Any(old => targets.Any(t => t.Handle == old.Handle && t != old)))
            return Clear(DiscordGroupStatus.Unknown);
        _order.RemoveAll(t => !targets.Contains(t));
        foreach (var target in targets)
            if (!_order.Contains(target)) _order.Add(target);
        _activeCall = mode == DiscordCaptureMode.DuringCalls;
        return new(DiscordGroupStatus.Eligible, _order.ToArray());
    }
}

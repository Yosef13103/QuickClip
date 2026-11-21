using QuickClip.Recording;
using QuickClip.Recording.Companion;

namespace QuickClip.Tests;

public sealed class DiscordGroupPolicyTests
{
    private static WindowOwner Window(int n) => new((IntPtr)n, (uint)n, 10, 42);
    private static ProbeResult Probe(params WindowOwner[] targets) =>
        new(true, 1, 1, 1, 1, targets.Length, "", 30) { Targets = targets };

    [Fact]
    public void Group_tracks_dynamic_windows_without_reordering_or_requiring_foreground()
    {
        var policy = new DiscordGroupPolicy();
        var a = Window(1); var b = Window(2); var c = Window(3);
        DiscordGroupDecision Decide(ProbeResult p, ActivityState state = ActivityState.LikelyConnected) =>
            policy.Decide(true, DiscordCaptureMode.DuringCalls, true, false, false, state, p, true, _ => true);
        Assert.Equal(new[] { a }, Decide(Probe(a)).Targets);
        Assert.Equal(new[] { a,b }, Decide(Probe(b,a)).Targets);
        Assert.Equal(new[] { a,b,c }, Decide(Probe(c,b,a)).Targets);
        Assert.Equal(new[] { a,c }, Decide(Probe(c,a)).Targets);
        Assert.Equal(new[] { a,c }, Decide(Probe(c,a), ActivityState.Leaving).Targets);
        Assert.Empty(Decide(Probe(a), ActivityState.Idle).Targets);
        Assert.Empty(Decide(Probe(a), ActivityState.Leaving).Targets);
    }

    [Fact]
    public void Deny_by_default_and_fail_closed_for_bad_identity_stale_probe_or_overflow()
    {
        var policy = new DiscordGroupPolicy();
        var a = Window(1);
        DiscordGroupDecision Decide(bool master = true, bool authorized = true, bool game = false,
            bool fresh = true, ProbeResult? probe = null, Func<WindowOwner,bool>? current = null) =>
            policy.Decide(master, DiscordCaptureMode.DuringCalls, authorized, game, false,
                ActivityState.LikelyConnected, probe ?? Probe(a), fresh, current ?? (_ => true));
        Assert.Equal(DiscordGroupStatus.Disabled, Decide(master:false).Status);
        Assert.Equal(DiscordGroupStatus.Disabled, Decide(authorized:false).Status);
        Assert.Equal(DiscordGroupStatus.GamePriority, Decide(game:true).Status);
        Assert.Equal(DiscordGroupStatus.Unknown, Decide(fresh:false).Status);
        Assert.Equal(DiscordGroupStatus.Unknown, Decide(probe: Probe(a) with { Complete = false }).Status);
        Assert.Equal(DiscordGroupStatus.Unknown, Decide(probe: Probe(a) with { Windows = null }).Status);
        Assert.Equal(DiscordGroupStatus.Unknown, Decide(current: _ => false).Status);
        Assert.Equal(DiscordGroupStatus.TooManyWindows, Decide(probe: Probe(Enumerable.Range(1, 5).Select(Window).ToArray())).Status);
        Assert.Equal(DiscordGroupStatus.Eligible, Decide().Status);
        Assert.Equal(DiscordGroupStatus.Unknown, Decide(probe: Probe(a with { Started = 43 })).Status);
        Assert.Equal(DiscordGroupStatus.Unknown, Decide(probe: Probe(a,a)).Status);
    }

    [Fact]
    public void Popup_close_unknown_probe_keeps_verified_remaining_windows_without_new_permission()
    {
        var policy = new DiscordGroupPolicy();
        var main = Window(1); var closedPopup = Window(2); var survivingPopup = Window(3);
        DiscordGroupDecision Decision(ProbeResult sample) =>
            policy.Decide(true, DiscordCaptureMode.DuringCalls, true, false, false,
                ActivityState.LikelyConnected, sample, true, _ => true);
        var first = Decision(Probe(main,closedPopup,survivingPopup));
        Assert.Equal(DiscordGroupStatus.Eligible, first.Status);
        var transient = Decision(Probe(main,survivingPopup) with { Windows = 3 });
        Assert.Equal(DiscordGroupStatus.Unknown, transient.Status);
        var hold = DiscordGroupPolicy.HoldVerifiedSurvivors(first, transient.Status,
            TimeSpan.FromSeconds(1), owner => owner != closedPopup);
        Assert.Equal(DiscordGroupStatus.Eligible, hold?.Status);
        Assert.Equal(new[] { main,survivingPopup }, hold?.Targets);
        Assert.Equal(new[] { main,survivingPopup },
            Decision(Probe(survivingPopup,main)).Targets); // stable primary across mismatch

        Assert.Null(DiscordGroupPolicy.HoldVerifiedSurvivors(first, transient.Status,
            TimeSpan.FromSeconds(4), _ => true));
        Assert.Null(DiscordGroupPolicy.HoldVerifiedSurvivors(first, DiscordGroupStatus.Disabled,
            TimeSpan.FromSeconds(1), _ => true));
        Assert.Null(DiscordGroupPolicy.HoldVerifiedSurvivors(first, DiscordGroupStatus.GamePriority,
            TimeSpan.FromSeconds(1), _ => true));
        Assert.Null(DiscordGroupPolicy.HoldVerifiedSurvivors(first, DiscordGroupStatus.Waiting,
            TimeSpan.FromSeconds(1), _ => true));
        Assert.Null(DiscordGroupPolicy.HoldVerifiedSurvivors(first, transient.Status,
            TimeSpan.FromSeconds(1), _ => false)); // every previous owner revoked
    }

    [Fact]
    public void Game_priority_relinquishes_to_verified_discord_call_without_a_foreground_requirement()
    {
        var policy = new DiscordGroupPolicy();
        var main = Window(17);
        var observed = Probe(main);
        Assert.Equal(DiscordGroupStatus.GamePriority,
            policy.Decide(true, DiscordCaptureMode.DuringCalls, true, true, false,
                ActivityState.LikelyConnected, observed, true, _ => true).Status);

        // Returning from a game uses fresh Discord call evidence. The policy
        // never asks whether Discord owns the foreground window.
        var resumed = policy.Decide(true, DiscordCaptureMode.DuringCalls, true, false, false,
            ActivityState.LikelyConnected, observed, true, _ => true);
        Assert.Equal(DiscordGroupStatus.Eligible, resumed.Status);
        Assert.Equal(new[] { main }, resumed.Targets);
    }

    [Fact]
    public void Multiple_incomplete_samples_do_not_extend_the_call_without_verified_evidence()
    {
        var activity = new ActivityClassifier();
        ActivityState At(int second,bool complete=true) =>
            activity.Update(new(complete, true, true, TimeSpan.FromSeconds(second)));
        Assert.Equal(ActivityState.Joining, At(0));
        Assert.Equal(ActivityState.LikelyConnected, At(2));
        Assert.Equal(ActivityState.LikelyConnected, At(3,false));
        Assert.Equal(ActivityState.LikelyConnected, At(4,false));
        Assert.Equal(ActivityState.Unknown, At(6,false));
    }

    [Fact]
    public void Explicit_open_and_manual_modes_do_not_depend_on_call_evidence()
    {
        var policy = new DiscordGroupPolicy();
        var a = Window(1);
        DiscordGroupDecision Decide(DiscordCaptureMode mode, bool requested) =>
            policy.Decide(true, mode, true, false, requested, ActivityState.Unknown, Probe(a), true, _ => true);
        Assert.Equal(DiscordGroupStatus.Eligible, Decide(DiscordCaptureMode.WhileOpen, false).Status);
        Assert.Equal(DiscordGroupStatus.Waiting, Decide(DiscordCaptureMode.Manual, false).Status);
        Assert.Equal(DiscordGroupStatus.Eligible, Decide(DiscordCaptureMode.Manual, true).Status);
        Assert.Equal(DiscordGroupStatus.Unknown, Decide(DiscordCaptureMode.DuringCalls, true).Status);
    }

    [Fact]
    public void Classifier_debounces_and_invalidates_stale_observations()
    {
        var c = new ActivityClassifier();
        ActivityState Step(int t, bool same, bool complete = true, bool present = true) =>
            c.Update(new(complete, present, same, TimeSpan.FromSeconds(t)));
        Assert.Equal(ActivityState.Idle, Step(0,false));
        Assert.Equal(ActivityState.Joining, Step(1,true));
        Assert.Equal(ActivityState.Joining, Step(2,true));
        Assert.Equal(ActivityState.LikelyConnected, Step(3,true));
        Assert.Equal(ActivityState.Leaving, Step(4,false));
        Assert.Equal(ActivityState.LikelyConnected, Step(5,true));
        Assert.Equal(ActivityState.Leaving, Step(6,false));
        Assert.Equal(ActivityState.Leaving, Step(8,false));
        Assert.Equal(ActivityState.Idle, Step(10,false));
        Assert.Equal(ActivityState.Joining, Step(11,true));
        Assert.Equal(ActivityState.Joining, Step(15,true));
        Assert.Equal(ActivityState.Joining, Step(16,true,false)); // one incomplete probe retains prior state
        Assert.Equal(ActivityState.Idle, Step(17,false,true,false));
    }
}

using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochContinuityTests
{
    private static ReplayEpochGameProof Game(
        uint processId = 100, long started = 1000, string id = "game:portal2",
        string path = @"C:\Games\Portal 2\portal2.exe", string display = "DISPLAY-A",
        long timeUs = 2_000_000, int revision = 2, long permissionRevision = 1, bool enabled = true,
        bool verified = true, bool displayAllowed = true) =>
        new(id, path, new WindowOwner(new IntPtr(processId), 30, processId, started),
            display, revision, permissionRevision, timeUs, enabled, verified, displayAllowed);

    [Fact]
    public void Same_verified_game_may_move_between_authorized_monitors()
    {
        var before = Game();
        var after = Game(display: "DISPLAY-B", timeUs: 4_000_000);
        Assert.Equal(ReplayEpochContinuityDecision.Continue,
            ReplayEpochContinuity.Decide(before, after, maximumGapUs: 10_000_000));
    }

    [Fact]
    public void Recreated_window_for_same_process_does_not_reset_timeline()
    {
        var before = Game();
        var after = Game(timeUs: 3_000_000) with
        {
            Owner = new WindowOwner(new IntPtr(987), 31, 100, 1000)
        };
        Assert.Equal(ReplayEpochContinuityDecision.Continue,
            ReplayEpochContinuity.Decide(before, after, 10_000_000));
    }

    [Fact]
    public void Different_game_or_executable_starts_new_timeline()
    {
        Assert.Equal(ReplayEpochContinuityDecision.StartNewTimeline,
            ReplayEpochContinuity.Decide(Game(), Game(id: "game:elden-ring", timeUs: 4_000_000), 10_000_000));
        Assert.Equal(ReplayEpochContinuityDecision.StartNewTimeline,
            ReplayEpochContinuity.Decide(Game(), Game(path: @"C:\Games\FakePortal\portal2.exe", timeUs: 4_000_000),
                10_000_000));
    }

    [Fact]
    public void Crash_restart_requires_explicit_verified_reacquisition_and_short_gap()
    {
        var old = Game();
        var restarted = Game(processId: 250, started: 3000, timeUs: 8_000_000);
        Assert.Equal(ReplayEpochContinuityDecision.StartNewTimeline,
            ReplayEpochContinuity.Decide(old, restarted, 30_000_000));
        Assert.Equal(ReplayEpochContinuityDecision.Continue,
            ReplayEpochContinuity.Decide(old, restarted, 30_000_000,
                allowVerifiedProcessRestart: true));
        Assert.Equal(ReplayEpochContinuityDecision.StartNewTimeline,
            ReplayEpochContinuity.Decide(old, Game(processId: 251, started: 4000, timeUs: 20_000_000),
                30_000_000, allowVerifiedProcessRestart: true));
    }

    [Fact]
    public void Loss_of_consent_or_verification_discards_prior_replay()
    {
        var old = Game();
        foreach (var next in new[]
        {
            Game(timeUs: 4_000_000, enabled: false),
            Game(timeUs: 4_000_000, verified: false),
            Game(timeUs: 4_000_000, displayAllowed: false),
            Game(timeUs: 4_000_000, revision: 3),
            Game(timeUs: 4_000_000, permissionRevision: 2),
            Game(timeUs: 4_000_000) with { Owner = new WindowOwner(IntPtr.Zero, 30, 100, 1000) }
        })
            Assert.Equal(ReplayEpochContinuityDecision.RevokeAndDiscard,
                ReplayEpochContinuity.Decide(old, next, 30_000_000));
    }

    [Fact]
    public void Expired_or_out_of_order_epoch_creates_new_timeline()
    {
        Assert.Equal(ReplayEpochContinuityDecision.StartNewTimeline,
            ReplayEpochContinuity.Decide(Game(), Game(timeUs: 35_000_000), 10_000_000));
        Assert.Equal(ReplayEpochContinuityDecision.StartNewTimeline,
            ReplayEpochContinuity.Decide(Game(), Game(timeUs: 1_000_000), 10_000_000));
    }
}

using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochSessionTrackerTests
{
    private static ReplayEpochGameProof Proof(long us = 1_000_000, long permission = 3,
        int disable = 7, string game = "game:portal2", string display = "DISPLAY-A",
        long processStart = 1000, bool verified = true) =>
        new(game, @"C:\Games\Portal 2\portal2.exe",
            new WindowOwner(new IntPtr(11), 32, 101, processStart),
            display, disable, permission, us, true, verified, true);

    [Fact]
    public void Fresh_verified_graph_begins_independent_logical_session()
    {
        var tracker = new ReplayEpochSessionTracker(10_000_000);
        var first = Assert.IsType<ReplayEpochSessionTracker.Beginning>(tracker.Begin(Proof()));
        Assert.NotEqual(Guid.Empty, first.LogicalSession);
        Assert.Equal(1, first.EpochNumber);
        Assert.False(first.Continued);
        Assert.True(tracker.Active);
        Assert.False(tracker.End(null));
        Assert.Equal(Guid.Empty, tracker.LogicalSession);
    }

    [Fact]
    public void Verified_stop_and_reacquisition_preserves_logical_session_across_resolution_change()
    {
        var tracker = new ReplayEpochSessionTracker(10_000_000);
        var first = tracker.Begin(Proof())!;
        Assert.True(tracker.End(Proof(us: 1_000_000_000)));
        var next = tracker.Begin(Proof(us: 1_003_000_000, display: "DISPLAY-B"))!;
        Assert.True(next.Continued);
        Assert.False(next.DiscardPreviousHistory);
        Assert.Equal(first.LogicalSession, next.LogicalSession);
        Assert.Equal(2, next.EpochNumber);
    }

    [Fact]
    public void Game_or_process_replacement_and_long_reconnect_do_not_join_history()
    {
        var tracker = new ReplayEpochSessionTracker(10_000_000);
        var first = tracker.Begin(Proof())!;
        Assert.True(tracker.End(Proof(us: 10_000_000)));
        var changed = tracker.Begin(Proof(us: 11_000_000, game: "game:other"))!;
        Assert.False(changed.Continued);
        Assert.True(changed.DiscardPreviousHistory);
        Assert.NotEqual(first.LogicalSession, changed.LogicalSession);
        Assert.True(tracker.End(Proof(us: 12_000_000, game: "game:other")));
        var afterCrash = tracker.Begin(Proof(us: 13_000_000, game: "game:other",
            processStart: 2000))!;
        Assert.False(afterCrash.Continued);
        Assert.NotEqual(changed.LogicalSession, afterCrash.LogicalSession);
        Assert.True(tracker.End(Proof(us: 14_000_000, game: "game:other", processStart: 2000)));
        var afterOutage = tracker.Begin(Proof(us: 50_000_000, game: "game:other",
            processStart: 2000))!;
        Assert.False(afterOutage.Continued);
    }

    [Fact]
    public void Revoked_grant_or_missing_authority_cannot_adopt_old_packets()
    {
        foreach (var changed in new[]
        {
            Proof(us: 2_000_000, permission: 4),
            Proof(us: 2_000_000, disable: 8),
            Proof(us: 2_000_000, verified: false),
        })
        {
            var tracker = new ReplayEpochSessionTracker(10_000_000);
            var first = tracker.Begin(Proof())!;
            Assert.False(tracker.End(changed));
            Assert.Equal(Guid.Empty, tracker.LogicalSession);
            if (ReplayEpochContinuity.IsValidProof(changed))
            {
                var resumed = tracker.Begin(changed)!;
                Assert.NotEqual(first.LogicalSession, resumed.LogicalSession);
                Assert.Equal(1, resumed.EpochNumber);
            }
            else Assert.Null(tracker.Begin(changed));
        }
    }

    [Fact]
    public void Changed_permission_between_verified_graphs_rejects_reuse_of_new_graph()
    {
        var tracker = new ReplayEpochSessionTracker(10_000_000);
        Assert.NotNull(tracker.Begin(Proof()));
        Assert.True(tracker.End(Proof(us: 2_000_000)));
        Assert.Null(tracker.Begin(Proof(us: 3_000_000, permission: 9)));
        Assert.Equal(Guid.Empty, tracker.LogicalSession);
        Assert.NotNull(tracker.Begin(Proof(us: 4_000_000, permission: 9)));
    }

    [Fact]
    public void Cannot_begin_twice_without_ending_previous_graph()
    {
        var tracker = new ReplayEpochSessionTracker(10_000_000);
        Assert.NotNull(tracker.Begin(Proof()));
        Assert.Null(tracker.Begin(Proof(us: 2_000_000)));
        Assert.Equal(Guid.Empty, tracker.LogicalSession);
    }
}

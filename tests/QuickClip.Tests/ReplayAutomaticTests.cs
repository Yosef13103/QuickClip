using QuickClip.Library;
using QuickClip.Gallery;
using QuickClip.Media;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayAutomaticTests
{
    private sealed class Harness(ReplayOverlapMode mode, int cap = 120)
    {
        internal readonly Guid Session = Guid.NewGuid();
        internal readonly List<ReplayCluster> Joined = [];
        internal readonly Dictionary<MediaSource, AutomaticReplay> Rows = [];
        internal ReplayOverlapOptions Options = new(mode, cap);
        internal ReplayAutomaticCoordinator Coordinator => _coordinator ??= new(
            () => Options, (source, _) => Task.FromResult(Rows.GetValueOrDefault(source)),
            (plan, _, _, allowed, _) =>
            {
                Assert.True(allowed());
                Joined.Add(plan);
                return Task.FromResult<AutomaticReplay?>(Add($"combined-{Joined.Count}", plan.Clips[0].FirstUs / 1_000_000,
                    plan.Clips.Max(c => c.EndUs) / 1_000_000));
            });
        private ReplayAutomaticCoordinator? _coordinator;

        internal AutomaticReplay Add(string name, long first, long end, bool favorite = false)
        {
            var p = new ReplayProvenance(Guid.NewGuid(), new(name, new(1, 1, 1000, name)),
                Session, 123, 1_700_000_000_000_000, 1, first * 1_000_000, end * 1_000_000, "layout", "hash");
            var item = new AutomaticReplay(p, new(DateTimeOffset.UtcNow, "Game") { IsFavorite = favorite });
            Rows.Add(p.Media, item);
            return item;
        }

        internal ReplayAutomaticCoordinator.Ticket Reserve(AutomaticReplay clip) =>
            Assert.IsType<ReplayAutomaticCoordinator.Ticket>(Coordinator.Reserve(Session, clip.Provenance.Media));

        internal static Task<MediaJobView> Proof() => Task.FromResult(new MediaJobView(Guid.NewGuid(), "proof",
            false, MediaJobState.Completed, 1, TimeSpan.Zero, null, null, null, false));

        internal async Task Save(string name, long first, long end, bool favorite = false)
        {
            Reserve(Add(name, first, end, favorite)).Complete(Proof());
            await Coordinator.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Live_mode_waits_for_proof_and_extends_previous_combined_result()
    {
        var h = new Harness(ReplayOverlapMode.ExtendPrevious);
        await h.Save("a", 0, 60);
        var ticket = h.Reserve(h.Add("b", 30, 90));
        var work = h.Coordinator.CurrentWork;
        Assert.False(work.IsCompleted);
        Assert.Empty(h.Joined);
        ticket.Complete(Harness.Proof());
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Save("c", 60, 120);
        Assert.Equal(new long[] { 90_000_000, 120_000_000 }, h.Joined.Select(p => p.UnionUs));
        Assert.Contains(h.Joined[1].Clips, c => c.Media.Path == "combined-1");
        await h.Save("d", 90, 150); // cap exceeded
        Assert.Equal(2, h.Joined.Count);
        await h.Coordinator.StopAsync();
    }

    [Fact]
    public async Task Failed_proof_and_favorite_break_live_chain()
    {
        var h = new Harness(ReplayOverlapMode.ExtendPrevious);
        await h.Save("a", 0, 60);
        h.Reserve(h.Add("b", 10, 70)).Complete(null);
        await h.Coordinator.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Save("c", 20, 80);
        await h.Save("d", 30, 90, favorite: true);
        await h.Save("e", 40, 100);
        Assert.Empty(h.Joined);
        await h.Coordinator.StopAsync();
    }

    [Fact]
    public async Task Deferred_mode_waits_for_session_end_and_partitions_by_cap_and_gap()
    {
        var h = new Harness(ReplayOverlapMode.CombineAfterRecording, 90);
        foreach (var (name, first, end) in new[] { ("a", 0, 60), ("b", 30, 90), ("c", 60, 120),
            ("d", 90, 150), ("e", 200, 260), ("f", 230, 290) })
            h.Reserve(h.Add(name, first, end)).Complete(Harness.Proof());
        Assert.True(h.Coordinator.CurrentWork.IsCompleted);
        Assert.Empty(h.Joined);
        h.Coordinator.EndSession(h.Session);
        await h.Coordinator.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, h.Joined.Count);
        Assert.All(h.Joined, p => Assert.Equal(90_000_000, p.UnionUs));
        await h.Coordinator.StopAsync();
    }

    [Fact]
    public async Task Invalidating_ended_inflight_session_prevents_replacement_and_default_is_separate()
    {
        var h = new Harness(ReplayOverlapMode.Separate);
        Assert.Null(h.Coordinator.Reserve(h.Session, h.Add("a", 0, 60).Provenance.Media));
        h.Options = new(ReplayOverlapMode.ExtendPrevious, 120);
        await h.Save("b", 20, 80);
        var ticket = h.Reserve(h.Add("c", 40, 100));
        h.Coordinator.EndSession(h.Session);
        h.Coordinator.Invalidate();
        ticket.Complete(Harness.Proof());
        await h.Coordinator.CurrentWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Joined);
        await h.Coordinator.StopAsync();
    }
}
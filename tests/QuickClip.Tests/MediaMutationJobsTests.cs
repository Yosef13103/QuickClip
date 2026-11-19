using QuickClip.Media;

namespace QuickClip.Tests;

[Collection("media")]
public sealed class MediaMutationJobsTests(MediaFixture media)
{
    [Fact]
    public async Task Replacement_reserves_every_original_until_transaction_settles()
    {
        var owner = new MediaOwnership();
        var jobs = new MediaJobs(owner, () => 1);
        var sources = new[] { MediaSource.Capture(media.TwoTracks), MediaSource.Capture(media.ThreeTracks) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = sources.Select(source => source.Path).ToArray();
        var result = jobs.Submit(new("Replace originals", false, sources, media.Out("union.mp4"),
            (_, _) => Task.FromResult("staged"))
        {
            Mutations = mutations,
            Replace = async (_, ct) =>
            {
                foreach (var source in sources)
                {
                    source.Verify();
                    using var conflictingMutation = owner.TryMutate(source.Path);
                    Assert.Null(conflictingMutation);
                    using var conflictingRead = owner.TryAcquire([source], []);
                    Assert.Null(conflictingRead);
                }
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                return "committed";
            },
        });
        // Submission must own a snapshot, even when the caller later reuses its list.
        mutations[0] = media.Out("unrelated.mp4");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var source in sources)
                Assert.Throws<ExportException>(() => jobs.Submit(new("Conflicting reader", false, [source],
                    media.Out(Guid.NewGuid() + ".mp4"), (_, _) => Task.FromResult("unexpected"))));
            release.SetResult();
            Assert.Equal(MediaJobState.Completed, (await result.Completion.WaitAsync(TimeSpan.FromSeconds(10))).State);
            foreach (var source in sources)
            {
                using var mutation = owner.TryMutate(source.Path);
                Assert.NotNull(mutation);
            }
        }
        finally { await jobs.StopAsync(); }
    }

    [Fact]
    public void Mutation_admission_requires_a_transaction_and_exact_source_snapshot()
    {
        var jobs = new MediaJobs(new MediaOwnership(), () => 1);
        var source = MediaSource.Capture(media.TwoTracks);
        var request = new MediaJobRequest("Invalid", false, [source], media.Out("invalid-union.mp4"),
            (_, _) => Task.FromResult("unexpected"));
        Assert.Throws<ExportException>(() => jobs.Submit(request with { Mutations = [source.Path] }));
        Assert.Throws<ExportException>(() => jobs.Submit(request with
        {
            Mutations = [media.ThreeTracks], Replace = (_, _) => Task.FromResult("unexpected"),
        }));
        Assert.Empty(jobs.Snapshot());
    }

    [Fact]
    public async Task Existing_reader_prevents_the_whole_replacement_without_partial_reservations()
    {
        var owner = new MediaOwnership();
        var jobs = new MediaJobs(owner, () => 1);
        var first = MediaSource.Capture(media.TwoTracks);
        var second = MediaSource.Capture(media.ThreeTracks);
        using (var reader = owner.TryAcquire([second], []))
        {
            Assert.NotNull(reader);
            Assert.Throws<ExportException>(() => jobs.Submit(new("Conflicting transaction", false,
                [first, second], media.Out("conflict-union.mp4"), (_, _) => Task.FromResult("unexpected"))
            {
                Mutations = [first.Path, second.Path], Replace = (_, _) => Task.FromResult("unexpected"),
            }));
            using var untouched = owner.TryMutate(first.Path);
            Assert.NotNull(untouched);
        }
        Assert.Empty(jobs.Snapshot());
        await jobs.StopAsync();
    }
}

using QuickClip.Media;

namespace QuickClip.Tests;

[Collection("media")]
public sealed class MediaJobsTests(MediaFixture media)
{
    [Fact]
    public async Task Two_live_encodes_share_a_source_and_cancel_independently()
    {
        var owner = new MediaOwnership();
        var jobs = new MediaJobs(owner, () => 2);
        var source = MediaSource.Capture(media.TwoTracks);
        var info = await MediaInfo.ProbeAsync(source.Path);
        var ready = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        // Real FFmpeg progress establishes that both jobs entered the encoder.
        // Keep their staging callbacks active until the cancellation assertion;
        // a fast runner may otherwise finish encoding before the snapshot.
        var allowValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signals = Enumerable.Range(0, 3).Select(i => jobs.Submit(Request(i))).ToArray();
        try
        {
            await Task.WhenAll(ready[0].Task, ready[1].Task).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2, jobs.Snapshot().Count(job => job.State == MediaJobState.Running));
            Assert.Equal(MediaJobState.Queued, jobs.Snapshot().Single(job => job.Id == signals[2].Id).State);
            Assert.Null(owner.TryMutate(source.Path));
            Assert.Throws<ExportException>(() => jobs.Submit(Request(2)));

            jobs.Cancel(signals[0].Id);
            var first = await signals[0].Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(MediaJobState.Cancelled, first.State);
            Assert.False(File.Exists(Request(0).Destination));
            allowValidation.TrySetResult();
            await ready[2].Task.WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var signal in signals.Skip(1))
            {
                var result = await signal.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(MediaJobState.Completed, result.State);
                Assert.True(File.Exists(result.Output));
            }
            using var mutation = owner.TryMutate(source.Path);
            Assert.NotNull(mutation);
            source.Verify();
        }
        finally { await jobs.StopAsync(); }

        MediaJobRequest Request(int i)
        {
            string destination = media.Out($"parallel-{i}.mp4");
            return new($"Encode {i}", false, [source], destination, (job, ct) =>
                MediaProcessing.PublishAsync(destination, ".mp4", null, async staging =>
                {
                    var plan = new ExportRequest
                    {
                        Media = info, Tracks = [], OutputPath = staging, Container = Containers.Mp4,
                        End = 4, VideoMode = VideoMode.Encode,
                    };
                    var args = ExportBuilder.BuildArgs(plan);
                    args.Insert(args.IndexOf("-i"), "-re");
                    await ExportRunner.RunAsync(args, staging, plan.Duration, new ProgressCallback<double>(progress =>
                    {
                        job.Progress(1, progress);
                        if (progress is > 0 and < 1) ready[i].TrySetResult();
                    }), ct);
                    if (i < 2) await allowValidation.Task.WaitAsync(ct);
                    job.Validating();
                    await TargetSizeExport.ValidateAsync(plan, ct);
                }, ct));
        }
    }

    [Fact]
    public async Task Shutdown_cancels_queue_and_releases_reservations()
    {
        var owner = new MediaOwnership();
        var jobs = new MediaJobs(owner, () => 1);
        var source = MediaSource.Capture(media.TwoTracks);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = jobs.Submit(new("First", false, [source], media.Out("stop-one.mp4"), async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "unreachable";
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = jobs.Submit(new("Second", false, [source], media.Out("stop-two.mp4"),
            (_, _) => throw new InvalidOperationException("A queued job must not start during shutdown.")));
        await jobs.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MediaJobState.Cancelled, (await first.Completion).State);
        Assert.Equal(MediaJobState.Cancelled, (await second.Completion).State);
        using var mutation = owner.TryMutate(source.Path);
        Assert.NotNull(mutation);
    }

    [Fact]
    public async Task Queued_source_replacement_is_rejected_before_work()
    {
        var jobs = new MediaJobs(new MediaOwnership(), () => 1);
        string path = media.Out("queued-source.mp4");
        File.Copy(media.TwoTracks, path);
        var permit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = jobs.Submit(new("First", false, [], media.Out("hold-slot.mp4"), async (_, ct) =>
        {
            await permit.Task.WaitAsync(ct);
            return "done";
        }));
        var second = jobs.Submit(new("Replaced", false, [MediaSource.Capture(path)], media.Out("no-output.mp4"),
            (_, _) => throw new InvalidOperationException("A changed source must never reach the encoder.")));
        try
        {
            File.Copy(media.ThreeTracks, path, overwrite: true);
            permit.SetResult();
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var rejected = await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(MediaJobState.Failed, rejected.State);
            Assert.Contains("changed", rejected.Message);
        }
        finally { await jobs.StopAsync(); }
    }
}

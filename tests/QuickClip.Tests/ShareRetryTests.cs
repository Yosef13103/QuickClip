using QuickClip.Media;

namespace QuickClip.Tests;

[Collection("media")]
public sealed class ShareRetryTests(MediaFixture media)
{
    [Fact]
    public async Task Failed_share_can_be_retried_repeatedly_with_explicit_copy_of_the_published_output()
    {
        var jobs = new MediaJobs(new MediaOwnership(), () => 1);
        var source = MediaSource.Capture(media.TwoTracks);
        string destination = media.Out("retried-share.mp4");
        var clipboard = new FakeClipboard();
        var notifications = new List<(string Title, string Message, bool Warning)>();
        var finishRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var originalRequest = clipboard.Requests.Begin();
        var original = jobs.Submit(new("Share fixture", true, [source], destination, (job, ct) =>
            MediaProcessing.PublishAsync(destination, ".mp4", null, async staging =>
            {
                using (var input = File.OpenRead(source.Path))
                using (var output = File.OpenWrite(staging))
                    await input.CopyToAsync(output, ct);
                if (++attempts <= 2) throw new ExportException("Simulated encode failure.");
                await finishRetry.Task.WaitAsync(ct);
                job.Validating();
            }, ct)));

        try
        {
            await ClipSharing.ReportCompletionAsync(original.Completion, Notify,
                file => clipboard.CopyAsync(file, originalRequest));
            var failed = await original.Completion;
            Assert.Equal(MediaJobState.Failed, failed.State);
            Assert.True(failed.CanRetry);
            Assert.Null(failed.Output);
            Assert.False(File.Exists(destination));

            var retry = jobs.Retry(failed.Id)!;
            Assert.Null(jobs.Retry(failed.Id));
            await ClipSharing.ReportCompletionAsync(retry, Notify);
            var failedRetry = await retry;
            Assert.Equal(MediaJobState.Failed, failedRetry.State);
            Assert.True(failedRetry.CanRetry);
            Assert.False(File.Exists(destination));

            var finalRetry = jobs.Retry(failedRetry.Id)!;
            clipboard.Set("copied in another app during retry");
            finishRetry.SetResult();
            await ClipSharing.ReportCompletionAsync(finalRetry, Notify);
            var completed = await finalRetry;
            Assert.Equal(MediaJobState.Completed, completed.State);
            Assert.Equal(destination, completed.Output);
            Assert.True(File.Exists(destination));
            Assert.False(jobs.Snapshot().Single(job => job.Id == failedRetry.Id).CanRetry);
            Assert.Null(jobs.Retry(completed.Id));
            source.Verify();

            Assert.Collection(notifications, Failure, Failure, ready =>
            {
                Assert.Equal("Share retry complete", ready.Title);
                Assert.Contains("Copy again", ready.Message);
                Assert.Contains("Reveal for manual attachment", ready.Message);
                Assert.False(ready.Warning);
            });
            Assert.Equal("copied in another app during retry", clipboard.Content);
            Assert.Equal(0, clipboard.CopyAttempts);

            var explicitCopy = clipboard.Requests.Begin();
            Assert.True(await clipboard.CopyAsync(completed.Output!, explicitCopy));
            Assert.Equal(destination, clipboard.Content);
            Assert.False(originalRequest());
        }
        finally { await jobs.StopAsync(); }

        void Notify(string title, string message, bool warning) => notifications.Add((title, message, warning));
        static void Failure((string Title, string Message, bool Warning) failed)
        {
            Assert.Equal("Share failed", failed.Title);
            Assert.True(failed.Warning);
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("external copy")]
    [InlineData("newer QuickClip request")]
    [InlineData("quit")]
    public async Task Initial_share_respects_clipboard_sequence_and_request_ownership(string interveningAction)
    {
        var clipboard = new FakeClipboard();
        var stillRequested = clipboard.Requests.Begin();
        bool canCopy = interveningAction == "none";
        string expected = canCopy ? "prepared.mp4" : clipboard.Content;
        switch (interveningAction)
        {
            case "external copy":
                clipboard.Set(expected = "other app's content");
                break;
            case "newer QuickClip request":
                clipboard.Requests.Begin();
                break;
            case "quit":
                clipboard.Requests.Invalidate();
                break;
        }

        var done = new MediaJobView(Guid.NewGuid(), "Share fixture", true, MediaJobState.Completed,
            1, TimeSpan.Zero, null, "prepared.mp4", null, false);
        await ClipSharing.ReportCompletionAsync(Task.FromResult(done), (title, message, warning) =>
        {
            Assert.Equal("Share ready", title);
            Assert.Contains(canCopy ? "copied to clipboard" : "Copy again", message);
            Assert.False(warning);
        }, file => clipboard.CopyAsync(file, stillRequested));
        Assert.Equal(expected, clipboard.Content);
    }

    [Fact]
    public async Task Failed_publication_preserves_destination_and_never_offers_it_as_a_share()
    {
        var jobs = new MediaJobs(new MediaOwnership(), () => 1);
        string destination = media.Out("share-publication-collision.mp4");
        var clipboard = new FakeClipboard();
        var stillRequested = clipboard.Requests.Begin();
        string? staged = null;
        var original = jobs.Submit(new("Share fixture", true, [], destination, (_, ct) =>
            MediaProcessing.PublishAsync(destination, ".mp4", null, staging =>
            {
                staged = staging;
                File.WriteAllText(staging, "generated staged output");
                File.WriteAllText(destination, "intervening destination");
                return Task.CompletedTask;
            }, ct)));
        try
        {
            int failures = 0;
            await ClipSharing.ReportCompletionAsync(original.Completion, (title, _, warning) =>
            {
                Assert.Equal("Share failed", title);
                Assert.True(warning);
                failures++;
            }, file => clipboard.CopyAsync(file, stillRequested));
            Assert.Equal(1, failures);
            var failed = await original.Completion;
            Assert.Equal(MediaJobState.Failed, failed.State);
            Assert.Null(failed.Output);
            Assert.Equal(0, clipboard.CopyAttempts);
            Assert.NotNull(staged);
            Assert.False(File.Exists(staged));
            Assert.Equal("intervening destination", File.ReadAllText(destination));
            Assert.Throws<ExportException>(() => { _ = jobs.Retry(failed.Id); });
            Assert.True(jobs.Snapshot().Single().CanRetry);
        }
        finally { await jobs.StopAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_and_export_failure_skip_share_feedback(bool share)
    {
        var state = share ? MediaJobState.Cancelled : MediaJobState.Failed;
        var result = new MediaJobView(Guid.NewGuid(), "Media fixture", share, state,
            null, TimeSpan.Zero, null, null, null, true);
        await ClipSharing.ReportCompletionAsync(Task.FromResult(result),
            (_, _, _) => throw new InvalidOperationException("Unexpected share notification."),
            _ => throw new InvalidOperationException("Unexpected clipboard write."));
    }

    private sealed class FakeClipboard
    {
        private uint _sequence = 1;
        internal string Content { get; private set; } = "existing clipboard content";
        internal int CopyAttempts { get; private set; }
        internal ClipSharing.ClipboardRequests Requests { get; }

        internal FakeClipboard() => Requests = new(() => _sequence);

        internal void Set(string content)
        {
            Content = content;
            _sequence++;
        }

        internal Task<bool> CopyAsync(string path, Func<bool> stillRequested)
        {
            CopyAttempts++;
            if (!stillRequested()) return Task.FromResult(false);
            Set(path);
            return Task.FromResult(true);
        }
    }
}

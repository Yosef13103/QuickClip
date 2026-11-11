using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayRuntimeTests
{
    [Theory]
    [InlineData(10, 30)]
    [InlineData(60, 120)]
    public void Overlong_requests_use_the_configured_window_and_preserve_three_track_ranges(int buffer, int request)
    {
        int last = buffer + 12;
        TestFrame[] frames = Enumerable.Range(0, last + 1).Select(s => Frame(s, key: s % 2 == 0)).ToArray();
        var (ok, _, span, offsets, counts) = Snapshot(frames, buffer, request, last * Second, 5, 11, 13, last + 1);
        Assert.True(ok);
        Assert.Equal(12 * Second, span.FirstUs);
        Assert.Equal(buffer * Second, span.LastUs - span.FirstUs);
        Assert.Equal((buffer + 2) * Second, span.AvailableUs);
        Assert.Equal(-1, span.DurationUs); // the packet fixture does not mux a file
        Assert.Equal([-1, -1, 0, buffer * Second], offsets);
        Assert.Equal([buffer + 1, buffer + 1, buffer + 1, buffer + 1], counts);
    }

    [Fact]
    public void Warmup_and_keyframe_alignment_report_the_selected_picture_span()
    {
        TestFrame[] frames = Enumerable.Range(0, 7).Select(s => Frame(s, key: s % 2 == 0)).ToArray();
        var (ok, position, warmup, _, _) = Snapshot(frames, 60, 30, 6 * Second + 50_000);
        Assert.True(ok);
        Assert.Equal(6 * Second, warmup.AvailableUs);
        Assert.Equal(6 * Second, position.FrameUs);
        Assert.Equal(6 * Second + 50_000, position.EventUs);
        var (aligned, _, span, offsets, _) = Snapshot(frames, 60, 3, 6 * Second, 3, 5, 7);
        Assert.True(aligned);
        Assert.Equal(4 * Second, span.FirstUs);
        Assert.Equal([-1, 0, 2 * Second], offsets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resume_after_a_gap_preserves_only_anchors_still_in_the_snapshot(bool clear)
    {
        TestFrame[] frames = [Frame(0), Frame(1, key: false), Frame(2), Frame(32, source: 2, clear: clear), Frame(33, key: false, source: 2)];
        var (ok, position, span, offsets, _) = Snapshot(frames, 60, 60, 33 * Second + 100_000, 3, 4, 5);
        Assert.True(ok);
        Assert.Equal(5, position.Sequence);
        Assert.Equal(2, position.Source);
        Assert.Equal(clear ? 2 : 1, span.FirstSource);
        Assert.Equal(2, span.LastSource);
        Assert.Equal(clear ? -1 : 2 * Second, offsets[0]);
        Assert.Equal(clear ? 0 : 32 * Second, offsets[1]);
        Assert.Equal(clear ? Second : 33 * Second, offsets[2]);
        var (shortOk, _, shortSpan, _, _) = Snapshot(frames, 60, 15, 33 * Second + 100_000);
        Assert.True(shortOk);
        Assert.Equal(32 * Second, shortSpan.FirstUs);
        var (staleOk, stale, _, _, _) = Snapshot(frames, 60, 15, 55 * Second);
        Assert.False(staleOk);
        Assert.Equal(0, stale.Sequence);
    }

    [Fact]
    public void Repeated_marker_presses_are_bounded_and_expire_by_frame_identity()
    {
        var buffer = new ReplayMarkers();
        var session = Guid.NewGuid();
        var position = new CaptureEngine.ReplayPosition { Sequence = 10, FirstSequence = 1, Source = 2, EventUs = 125, FrameUs = 100 };
        for (int i = 0; i < ReplayMarkers.Capacity + 2; i++) Assert.NotNull(buffer.Add(position, session, "Game", "Window", $" {i} "));
        var retained = buffer.Snapshot(position, session);
        Assert.Equal(ReplayMarkers.Capacity, retained.Length);
        Assert.Equal("2", retained[0].Marker.Label);
        Assert.Equal(TimeSpan.FromTicks(1250), retained[0].Marker.EventTime);
        Assert.Equal(ReplayMarkers.Capacity, retained.Select(x => x.Marker.Id).Distinct().Count());
        position.FirstSequence = 11;
        Assert.Empty(buffer.Snapshot(position, session));
    }

    [Fact]
    public void Receipts_accept_only_matched_offsets_inside_the_finalized_track()
    {
        var buffer = new ReplayMarkers();
        var session = Guid.NewGuid();
        for (int i = 1; i <= 4; i++)
            buffer.Add(new CaptureEngine.ReplayPosition { Sequence = i, FirstSequence = 1 }, session, "Game", "Window", null);
        var candidates = buffer.Snapshot(default, session);
        CaptureEngine.ReplayAnchor[] anchors =
        [
            new() { Sequence = 1, OffsetUs = -1 },
            new() { Sequence = 2, OffsetUs = 0 },
            new() { Sequence = 3, OffsetUs = 250_000 },
            new() { Sequence = 4, OffsetUs = Second },
        ];
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMilliseconds(250)], ReplayMarkers.Match(candidates, anchors, TimeSpan.FromSeconds(1)).Select(m => m.Offset));
        Assert.Empty(buffer.Snapshot(default, Guid.NewGuid()));
    }

    [Fact]
    public async Task Cancelled_replay_requests_never_start_a_save()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Recorder.Instance.SaveReplayAsync(15, cancellation.Token));
    }

    [Fact]
    public async Task Save_gate_rejects_overlap_and_releases_after_cancellation()
    {
        var gate = new ReplaySaveGate();
        var first = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int?> running = gate.RunAsync(() => first.Task, CancellationToken.None);
        bool busy = false;
        Assert.Null(await gate.RunAsync<int>(() => throw new InvalidOperationException("Overlapping save ran"),
            CancellationToken.None, () => busy = true));
        Assert.True(busy);
        first.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(42, await gate.RunAsync(() => Task.FromResult<int?>(42), CancellationToken.None));
    }

    private const long Second = 1_000_000;
    private static TestFrame Frame(int seconds, bool key = true, long source = 1, bool clear = false) =>
        new() { TimeUs = seconds * Second, Source = source, Key = key ? 1 : 0, ClearBefore = clear ? 1 : 0 };

    private static (bool Ok, CaptureEngine.ReplayPosition Position, CaptureEngine.ReplaySpan Span, long[] Offsets, int[] Counts)
        Snapshot(TestFrame[] frames, int buffer, int request, long requestUs, params long[] sequences)
    {
        Assert.True(CaptureEngine.IsAvailable);
        CaptureEngine.Initialize();
        var anchors = sequences.Select(s => new CaptureEngine.ReplayAnchor { Sequence = s, OffsetUs = -1 }).ToArray();
        var counts = new int[4];
        bool ok = qc_test_replay_ring(frames, frames.Length, buffer, request, requestUs, out var position, out var span,
            anchors, anchors.Length, counts) != 0;
        return (ok, position, span, anchors.Select(a => a.OffsetUs).ToArray(), counts);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestFrame
    {
        public long TimeUs, Source;
        public int Key, ClearBefore;
    }

    [DllImport("QuickClipCapture.dll")]
    private static extern int qc_test_replay_ring([In] TestFrame[] frames, int frameCount, int bufferSeconds,
        int requestedSeconds, long requestUs, out CaptureEngine.ReplayPosition position, out CaptureEngine.ReplaySpan span,
        [In, Out] CaptureEngine.ReplayAnchor[] anchors, int anchorCount, [Out] int[] streamCounts);
}

using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochLiveSaverTests
{
    private static readonly Guid Session = Guid.Parse("00000000-0000-4000-8000-000000000567");
    private const string Scope = "game:portal2|C:\\Games\\Portal 2\\portal2.exe";

    private static CaptureEngine.EpochDescriptor Archived(ulong id, long first, long last,
        int width = 3440, int height = 1440) => new()
    {
        EpochId = id, FirstUs = first, LastUs = last,
        Width = width, Height = height, CompressedBytes = 250_000,
    };

    private static CaptureEngine.ReplaySpan Active(long first = 0, long last = 5_966_667,
        long duration = 6_000_000) => new()
    {
        FirstUs = first, LastUs = last, DurationUs = duration,
    };

    private static CaptureEngine.ReplayVideo Video(int width = 1920, int height = 1080) =>
        new() { Width = width, Height = height, FpsNumerator = 30, FpsDenominator = 1 };

    [Fact]
    public void Requested_span_crosses_two_resolution_epochs_and_records_actual_gap()
    {
        // A 4s ultrawide epoch was sealed at global QPC 94-98s.
        // After a 2s interruption the 1080p graph produced 6s at QPC 100-106s.
        var selected = ReplayEpochLiveSaver.Select(
            [Archived(1, 94_000_000, 98_000_000)],
            Session, Scope, 100_000_000, 6_000_000, 10, Active(), Video(), 30);
        Assert.NotNull(selected);
        Assert.Single(selected.Archived);
        Assert.Equal(2, selected.Plan.Slices.Count);
        Assert.Equal((3440, 1440), (selected.Plan.Width, selected.Plan.Height));
        Assert.Equal((96_000_000L, 98_000_000L),
            (selected.Plan.Slices[0].StartUs, selected.Plan.Slices[0].EndUs));
        Assert.Equal((98_000_000L, 100_000_000L),
            (Assert.Single(selected.Plan.Gaps).StartUs, selected.Plan.Gaps[0].EndUs));
        Assert.True(selected.Plan.RequiresVideoNormalization);
    }

    [Fact]
    public void Archive_outside_requested_window_uses_normal_fast_path()
    {
        Assert.Null(ReplayEpochLiveSaver.Select(
            [Archived(1, 80_000_000, 82_000_000)],
            Session, Scope, 100_000_000, 6_000_000, 10, Active(), Video(), 30));
    }

    [Fact]
    public void Native_archive_overlapping_current_graph_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ReplayEpochLiveSaver.Select(
            [Archived(1, 99_000_000, 101_000_000)],
            Session, Scope, 100_000_000, 6_000_000, 10, Active(), Video(), 30));
    }

    [Fact]
    public void Invalid_or_unbounded_epoch_geometry_never_gets_merged()
    {
        Assert.Throws<InvalidOperationException>(() => ReplayEpochLiveSaver.Select(
            [Archived(1, 94_000_000, 98_000_000, 9000, 5000)],
            Session, Scope, 100_000_000, 6_000_000, 10, Active(), Video(), 30));
        Assert.Throws<InvalidOperationException>(() => ReplayEpochLiveSaver.Select(
            [Archived(0, 94_000_000, 98_000_000)],
            Session, Scope, 100_000_000, 6_000_000, 10, Active(), Video(), 30));
    }
}

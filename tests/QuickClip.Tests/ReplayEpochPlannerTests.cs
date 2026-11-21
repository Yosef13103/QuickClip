using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochPlannerTests
{
    private static readonly Guid Session = Guid.Parse("00000000-0000-4000-8000-000000000101");
    private const string Source = "verified-game:elden-ring";
    private const long Budget = 34_000_000;

    private static ReplayEncodedEpoch E(long start, long end, int width, int height,
        string source = Source, string codec = "hevc-a", Guid? session = null) =>
        new(session ?? Session, source, Guid.NewGuid(), start, end, width, height, codec);

    [Fact]
    public void Single_1080_epoch_exports_1080_without_stitch_or_padding()
    {
        var plan = ReplayEpochPlanner.Select([E(0, 60, 1920, 1080)], Session, Source, 10, 50, Budget);
        Assert.Equal((1920, 1080), (plan.Width, plan.Height));
        Assert.Single(plan.Slices);
        Assert.Equal((10L, 50L), (plan.Slices[0].StartUs, plan.Slices[0].EndUs));
        Assert.False(plan.HasDiscontinuities);
        Assert.False(plan.RequiresVideoNormalization);
    }

    [Fact]
    public void Up_or_down_resolution_selects_largest_encoded_epoch_in_requested_interval()
    {
        ReplayEncodedEpoch[] ring = [E(0, 100, 3440, 1440), E(100, 200, 1920, 1080)];
        var across = ReplayEpochPlanner.Select(ring, Session, Source, 50, 175, Budget);
        Assert.Equal((3440, 1440), (across.Width, across.Height));
        Assert.True(across.MixedVideoGeometry);
        Assert.True(across.RequiresVideoNormalization);
        Assert.False(across.HasDiscontinuities);

        // The prior 1440p epoch is outside the selected interval; no forced upscale.
        var only1080 = ReplayEpochPlanner.Select(ring, Session, Source, 100, 175, Budget);
        Assert.Equal((1920, 1080), (only1080.Width, only1080.Height));
        Assert.False(only1080.RequiresVideoNormalization);
    }

    [Fact]
    public void Monitor_move_with_recording_gap_preserves_only_real_segments()
    {
        ReplayEncodedEpoch[] ring = [E(0, 40, 1920, 1080), E(50, 90, 3440, 1440)];
        var plan = ReplayEpochPlanner.Select(ring, Session, Source, 0, 90, Budget);
        Assert.Equal((3440, 1440), (plan.Width, plan.Height));
        Assert.Equal(2, plan.Slices.Count);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal((40L, 50L), (gap.StartUs, gap.EndUs));
        Assert.True(plan.HasDiscontinuities);
    }

    [Fact]
    public void Missing_leading_and_trailing_video_is_disclosed_not_invented()
    {
        var plan = ReplayEpochPlanner.Select([E(20, 60, 1920, 1080)],
            Session, Source, 0, 90, Budget);
        Assert.Equal(2, plan.Gaps.Count);
        Assert.Equal((0L, 20L), (plan.Gaps[0].StartUs, plan.Gaps[0].EndUs));
        Assert.Equal((60L, 90L), (plan.Gaps[1].StartUs, plan.Gaps[1].EndUs));
    }

    [Fact]
    public void Different_game_or_recording_session_cannot_join_implicitly()
    {
        var other = E(30, 50, 1920, 1080, source: "verified-game:dark-souls");
        Assert.Throws<InvalidOperationException>(() =>
            ReplayEpochPlanner.Select([E(0, 30, 1920, 1080), other], Session, Source, 0, 50, Budget));
        Assert.Throws<InvalidOperationException>(() =>
            ReplayEpochPlanner.Select([E(0, 30, 1920, 1080), E(30, 50, 1920, 1080, session: Guid.NewGuid())],
                Session, Source, 0, 50, Budget));
    }

    [Fact]
    public void Codec_reinitialization_forces_compatibility_review_even_when_size_matches()
    {
        var plan = ReplayEpochPlanner.Select([E(0, 20, 1920, 1080), E(20, 50, 1920, 1080, codec: "hevc-b")],
            Session, Source, 0, 50, Budget);
        Assert.False(plan.MixedVideoGeometry);
        Assert.True(plan.MixedStreamConfiguration);
        Assert.True(plan.RequiresVideoNormalization);
    }

    [Fact]
    public void Overlapping_or_overbudget_epochs_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReplayEpochPlanner.Select([E(0, 30, 1920, 1080), E(25, 50, 1920, 1080)],
                Session, Source, 0, 50, Budget));
        Assert.Throws<InvalidOperationException>(() =>
            ReplayEpochPlanner.Select([E(0, 30, 7680, 4320)], Session, Source, 0, 30, 5_000_000));
    }

    [Fact]
    public void Largest_output_is_an_actual_epoch_not_a_synthetic_max_width_height()
    {
        // 3840x1080 and 2560x1600 have different extremal dimensions.
        // Pixel area chooses the actual 3840x1080 epoch, not invented 3840x1600.
        var plan = ReplayEpochPlanner.Select(
            [E(0, 20, 3840, 1080), E(20, 40, 2560, 1600)], Session, Source, 0, 40, Budget);
        Assert.Equal((3840, 1080), (plan.Width, plan.Height));
    }
}

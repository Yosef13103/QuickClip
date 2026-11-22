using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochCanvasTests
{
    private static readonly Guid Session = Guid.Parse("00000000-0000-4000-8000-000000002101");

    private static ReplayEpochExportPlan Plan(params (int Width, int Height)[] sizes)
    {
        var epochs = sizes.Select((item, i) => new ReplayEncodedEpoch(
            Session, "verified-game:synthetic", Guid.NewGuid(),
            i * 2_000_000L, (i + 1) * 2_000_000L, item.Width, item.Height, "h264+aac"));
        return ReplayEpochPlanner.Select(epochs, Session, "verified-game:synthetic", 0,
            sizes.Length * 2_000_000L, 34_000_000);
    }

    [Fact]
    public void Auto_preserves_four_k_detail_rather_than_silently_downscaling_to_1440()
    {
        var plan = Plan((1920, 1080), (3840, 2160));
        Assert.Equal((3840, 2160), ReplayEpochCanvas.Resolve(plan));
        Assert.Equal((2560, 1440), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.Balanced)));
        Assert.Equal((2560, 1440), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.P1440)));
        Assert.Equal((1920, 1080), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.P1080)));
    }

    [Fact]
    public void Auto_and_height_presets_preserve_ultrawide_canvas_shape()
    {
        var plan = Plan((1920, 1080), (3440, 1440));
        Assert.Equal((3440, 1440), ReplayEpochCanvas.Resolve(plan));
        Assert.Equal((2580, 1080), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.P1080)));
        Assert.Equal((5160, 2160), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.P2160)));
        Assert.Equal((3440, 1440), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.Balanced)));
    }

    [Fact]
    public void Balanced_never_upscales_small_sources_without_explicit_request()
    {
        var plan = Plan((640, 360), (960, 400));
        Assert.Equal((960, 400), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.Balanced)));
        Assert.Equal((3456, 1440), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.P1440)));
    }

    [Fact]
    public void Custom_dimensions_are_exact_but_validated_before_export()
    {
        var plan = Plan((3440, 1440));
        Assert.Equal((1920, 1080), ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.Custom, 1920, 1080)));
        Assert.Throws<InvalidOperationException>(() => ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.Custom, 1919, 1080)));
        Assert.Throws<InvalidOperationException>(() => ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.Custom, 8192, 4320)));
        Assert.Throws<InvalidOperationException>(() => ReplayEpochCanvas.Resolve(plan,
            new(ReplayEpochOutputSize.Custom, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplayEpochCanvas.Resolve(plan,
            new((ReplayEpochOutputSize)999)));
    }

    [Fact]
    public void When_16_by_9_has_most_pixels_auto_uses_16_by_9_canvas_without_cropping_ultrawide_input()
    {
        var plan = Plan((3440, 1440), (3840, 2160));
        Assert.Equal((3840, 2160), ReplayEpochCanvas.Resolve(plan));
        // Actual exporter uses fit-with-padding; don't claim the whole output
        // can simultaneously be ultrawide and 16:9.
    }
}

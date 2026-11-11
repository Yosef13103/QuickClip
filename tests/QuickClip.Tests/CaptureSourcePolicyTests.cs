using QuickClip.Recording;
using Backend = QuickClip.Recording.CaptureEngine.MonitorBackend;

namespace QuickClip.Tests;

public sealed class CaptureSourcePolicyTests
{
    [Theory]
    [InlineData((int)Backend.Automatic)]
    [InlineData((int)Backend.DesktopDuplication)]
    [InlineData((int)Backend.GraphicsCapture)]
    public void Strict_game_window_cannot_open_a_monitor_or_border_requiring_wgc(int backend)
    {
        var decision = CaptureSourcePolicy.Resolve(CaptureSourceMode.GameWindowOnly, (Backend)backend,
            monitorFallbackAuthorized: true);
        Assert.False(decision.Allowed);
        Assert.Contains("unavailable", decision.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_requires_authorized_monitor_fallback_and_never_uses_wgc(bool authorized)
    {
        var decision = CaptureSourcePolicy.Resolve(CaptureSourceMode.Automatic, Backend.GraphicsCapture, authorized);
        Assert.Equal(authorized, decision.Allowed);
        if (authorized) Assert.Equal(Backend.MonitorOnlyDuplication, decision.Backend);
    }

    [Theory]
    [InlineData((int)Backend.Automatic, (int)Backend.MonitorOnlyDuplication)]
    [InlineData((int)Backend.DesktopDuplication, (int)Backend.MonitorOnlyDuplication)]
    [InlineData((int)Backend.GraphicsCapture, (int)Backend.MonitorOnlyGraphics)]
    public void Selected_monitor_resolves_to_a_fixed_native_backend(int input, int expected)
    {
        var decision = CaptureSourcePolicy.Resolve(CaptureSourceMode.SelectedMonitor, (Backend)input);
        Assert.True(decision.Allowed);
        Assert.Equal((Backend)expected, decision.Backend);
    }

    [Fact]
    public void Losing_an_explicit_display_does_not_select_another_display()
    {
        var primary = new MonitorInfo("DISPLAY1", "Main", 0, 0, 3440, 1440);
        var secondary = new MonitorInfo("DISPLAY2", "Secondary", 3440, 0, 1920, 1080);
        Assert.Same(secondary, CaptureSourcePolicy.SelectMonitor([primary, secondary], "display2", requireSelected: true));
        Assert.Null(CaptureSourcePolicy.SelectMonitor([primary], "display2", requireSelected: true));
        Assert.Same(primary, CaptureSourcePolicy.SelectMonitor([primary], "", requireSelected: true));
    }

    [Fact]
    public void Receipt_distinguishes_graph_epochs_independently_of_verified_desktop_source()
    {
        var receipt = new ReplaySaveReceipt("fixture.mp4", "Desktop", Guid.NewGuid(), 15, TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(12), TimeSpan.Zero, TimeSpan.FromSeconds(12), 1, 3, null, [])
        {
            DesktopSource = new("DISPLAY2", ReplayCaptureBackend.DesktopDuplication),
        };
        Assert.True(receipt.SpansCaptureEpochs);
        Assert.Equal("DISPLAY2", receipt.DesktopSource.DisplayDevice);
        Assert.True((receipt with { DesktopSource = null }).SpansCaptureEpochs);
        Assert.False((receipt with { FirstSourceId = 3 }).SpansCaptureEpochs);
    }
}

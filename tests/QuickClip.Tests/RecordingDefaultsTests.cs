using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class RecordingDefaultsTests
{
    [Fact]
    public void Missing_preset_uses_the_profile_default_without_changing_recording_geometry()
    {
        var settings = AppSettings.Parse("{}");
        Assert.Equal(AppProfile.IsPersonal ? CaptureEngine.NvencSpeed.FastestP2 : CaptureEngine.NvencSpeed.BalancedP4, settings.NvencPreset);
        Assert.Equal(60, settings.RecordFps);
        Assert.Equal(0, settings.RecordResolution);
        Assert.Equal(RecordingQuality.High, settings.RecordQuality);
    }

    [Theory]
    [InlineData("\"BalancedP4\"", 0)]
    [InlineData("\"FasterP3\"", 1)]
    [InlineData("\"FastestP2\"", 2)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    public void Explicit_presets_survive_load_save_and_do_not_change_target_or_quality(string value, int expected)
    {
        var settings = AppSettings.Parse("{\"NvencPreset\":" + value + ",\"RecordFps\":120,\"RecordResolution\":1080,\"PreferGameWindow\":false,\"CaptureBackend\":\"DesktopDuplication\",\"RecordQuality\":\"Low\"}");
        settings = AppSettings.Parse(settings.ToJson());
        Assert.Equal((CaptureEngine.NvencSpeed)expected, settings.NvencPreset);
        Assert.Equal(120, settings.RecordFps);
        Assert.Equal(1080, settings.RecordResolution);
        Assert.Equal(RecordingQuality.Low, settings.RecordQuality);
        Assert.False(settings.PreferGameWindow);
        Assert.Equal(CaptureEngine.MonitorBackend.DesktopDuplication, settings.CaptureBackend);
    }
}

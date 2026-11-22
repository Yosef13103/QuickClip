using QuickClip.Recording;
using QuickClip.Views;
using System.Windows.Controls;

namespace QuickClip.Tests;

public sealed class DisplaySelectionTests
{
    private static MonitorInfo Display(string device, string identity, int left = 0) =>
        new(device, "Same model", left, 0, 3440, 1440) { CaptureIdentity = identity };

    [Fact]
    public async Task Wpf_display_permission_snapshot_does_not_access_checkbox_from_worker_thread()
    {
        var captured = new TaskCompletionSource<Action<AppSettings>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var check = new CheckBox { Tag = "physical-one", IsChecked = true };
                var mutation = SettingsView.SnapshotDisplayPermissionUpdate(check);
                check.IsChecked = false; // Subsequent WPF changes must not change the snapshot.
                captured.SetResult(mutation);
            }
            catch (Exception ex) { captured.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var update = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var settings = new AppSettings { AllowRecordingDisplaysByDefault = false };
        Assert.True(await Task.Run(() => Recorder.TryUpdateDisplayPermission(settings, update, _ => true)));
        Assert.True(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-one"));
        Assert.False(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-two"));
    }

    [Fact]
    public void Monitor_illustrations_preserve_native_aspect_ratio_without_screen_content()
    {
        var ultrawide = new MonitorInfo(@"\\.\DISPLAY1", "Ultrawide", 0, 0, 3440, 1440);
        var standard = new MonitorInfo(@"\\.\DISPLAY2", "Standard", 3440, 0, 1920, 1080);
        var portrait = new MonitorInfo(@"\\.\DISPLAY3", "Portrait", 5360, 0, 1080, 1920);

        var widePreview = SettingsView.ScreenIllustrationSize(ultrawide);
        var normalPreview = SettingsView.ScreenIllustrationSize(standard);
        var portraitPreview = SettingsView.ScreenIllustrationSize(portrait);

        Assert.Equal(3440d / 1440, widePreview.Width / widePreview.Height, 7);
        Assert.Equal(1920d / 1080, normalPreview.Width / normalPreview.Height, 7);
        Assert.Equal(1080d / 1920, portraitPreview.Width / portraitPreview.Height, 7);
        Assert.True(widePreview.Width > normalPreview.Width);
        Assert.True(portraitPreview.Width < normalPreview.Width);
        Assert.True(widePreview.Width <= 220 && widePreview.Height <= 88);
    }

    [Fact]
    public void Manual_target_change_never_grants_capture_permission()
    {
        var settings = new AppSettings { AllowRecordingDisplaysByDefault = false };
        var selected = Display(@"\\.\DISPLAY2", "physical-two", 3440);
        Assert.True(CaptureSourcePolicy.TrySelectManualTarget(settings, selected, _ => true));
        Assert.Equal(selected.DeviceName, settings.RecordMonitor);
        Assert.Equal("physical-two", settings.RecordMonitorIdentity);
        Assert.Empty(settings.AllowedRecordingDisplays);
        Assert.False(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-two"));
        CaptureSourcePolicy.SetDisplayPermission(settings, "physical-two", true);
        Assert.True(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-two"));
        Assert.False(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-one"));
    }

    [Fact]
    public void Invalid_identity_and_failed_save_cannot_change_manual_target()
    {
        var settings = new AppSettings { RecordMonitor = "old", RecordMonitorIdentity = "old-physical" };
        Assert.False(CaptureSourcePolicy.TrySelectManualTarget(settings,
            new MonitorInfo(@"\\.\DISPLAY3", "Unidentified", 0, 0, 1920, 1080), _ => true));
        Assert.False(CaptureSourcePolicy.TrySelectManualTarget(settings, Display(@"\\.\DISPLAY2", "new-physical"), _ => false));
        Assert.Equal("old", settings.RecordMonitor);
        Assert.Equal("old-physical", settings.RecordMonitorIdentity);
    }

    [Fact]
    public void Clearing_default_manual_target_is_optional_persisted_and_never_grants_capture()
    {
        var settings = new AppSettings
        {
            AllowRecordingDisplaysByDefault = false,
            RecordMonitor = @"\\.\DISPLAY2", RecordMonitorIdentity = "physical-two",
            AllowedRecordingDisplays = ["physical-one"]
        };
        Assert.False(CaptureSourcePolicy.TryClearManualTarget(settings, _ => false));
        Assert.Equal("physical-two", settings.RecordMonitorIdentity);
        Assert.True(CaptureSourcePolicy.TryClearManualTarget(settings, _ => true));
        Assert.Equal("", settings.RecordMonitor);
        Assert.Null(settings.RecordMonitorIdentity);
        Assert.False(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-two"));
        Assert.Null(SettingsView.ValidateDisplaySetup(settings,
            [Display(@"\\.\DISPLAY1", "physical-one"), Display(@"\\.\DISPLAY2", "physical-two", 3440)]));
    }

    [Fact]
    public void Reordered_or_disconnected_display_never_silently_changes_physical_target()
    {
        var original = Display(@"\\.\DISPLAY1", "physical-one");
        var other = Display(@"\\.\DISPLAY2", "physical-two", 3440);
        var settings = new AppSettings { RecordMonitor = original.DeviceName, RecordMonitorIdentity = "physical-one" };
        Assert.Equal("physical-one", CaptureSourcePolicy.StoredManualTarget(settings, [original, other])?.CaptureIdentity);
        var swapped = new[] { Display(original.DeviceName, "physical-two"), Display(other.DeviceName, "physical-one", 3440) };
        Assert.Null(CaptureSourcePolicy.StoredManualTarget(settings, swapped));
        Assert.Null(CaptureSourcePolicy.StoredManualTarget(settings, [other]));
        settings.RecordMonitorIdentity = null; // Old serialized profiles cannot prove physical identity.
        Assert.Null(CaptureSourcePolicy.StoredManualTarget(settings, [original]));
        Assert.Equal(original.DeviceName, settings.RecordMonitor);
        Assert.False(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-two"));
    }

    [Fact]
    public void Completion_requires_authorized_display_and_valid_explicit_manual_target()
    {
        var main = Display(@"\\.\DISPLAY1", "physical-one");
        var secondary = Display(@"\\.\DISPLAY2", "physical-two", 3440);
        var settings = new AppSettings { AllowRecordingDisplaysByDefault = false };
        Assert.NotNull(QuickClip.Views.SettingsView.ValidateDisplaySetup(settings, [main, secondary]));
        CaptureSourcePolicy.SetDisplayPermission(settings, "physical-one", true);
        Assert.Null(QuickClip.Views.SettingsView.ValidateDisplaySetup(settings, [main, secondary])); // Default manual target is optional.
        Assert.Null(QuickClip.Views.SettingsView.ValidateDisplaySetup(settings, [main]));
        Assert.True(CaptureSourcePolicy.TrySelectManualTarget(settings, secondary, _ => true));
        Assert.Contains("blocked", QuickClip.Views.SettingsView.ValidateDisplaySetup(settings, [main, secondary])!);
        CaptureSourcePolicy.SetDisplayPermission(settings, "physical-two", true);
        Assert.Null(QuickClip.Views.SettingsView.ValidateDisplaySetup(settings, [secondary, main]));
        Assert.Contains("changed or disconnected", QuickClip.Views.SettingsView.ValidateDisplaySetup(settings,
            [Display(main.DeviceName, "physical-two"), Display(secondary.DeviceName, "physical-one", 3440)])!);
    }

    [Fact]
    public void Same_named_monitors_are_distinct_and_old_profiles_retain_explicit_defaults()
    {
        var settings = AppSettings.Parse("""{"AllowRecordingDisplaysByDefault":true,"ExcludedGameDisplays":["physical-two"]}""");
        Assert.True(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-one"));
        Assert.False(CaptureSourcePolicy.AllowsRecordingDisplay(settings, "physical-two"));
        Assert.False(AppSettings.Parse("""{"AllowedRecordingDisplays":["physical-one"]}""").AllowRecordingDisplaysByDefault);
        var a = Display(@"\\.\DISPLAY1", "physical-one");
        var b = Display(@"\\.\DISPLAY2", "physical-two");
        Assert.NotEqual(a.CaptureIdentity, b.CaptureIdentity);
        Assert.Equal(a.FriendlyName, b.FriendlyName);
    }
}

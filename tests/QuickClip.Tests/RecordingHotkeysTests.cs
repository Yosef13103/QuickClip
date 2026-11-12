using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class RecordingHotkeysTests
{
    [Fact]
    public void Fresh_profile_has_only_save_binding_and_saved_unbind_survives()
    {
        var fresh = new AppSettings();
        Assert.Equal(new Hotkey(Hotkey.Alt, 0x79), RecordingHotkeys.Read(fresh, RecordingCommand.SaveClip));
        Assert.All(Enum.GetValues<RecordingCommand>().Skip(1), command =>
            Assert.True(RecordingHotkeys.Read(fresh, command).IsEmpty));
        var saved = AppSettings.Parse("""{"HotkeyKey":0,"HotkeyModifiers":0}""");
        Assert.True(RecordingHotkeys.Read(saved, RecordingCommand.SaveClip).IsEmpty);
        saved.DesktopHotkey = new(Hotkey.Control, 0x78);
        Assert.Equal(saved.DesktopHotkey, AppSettings.Parse(saved.ToJson()).DesktopHotkey);
        var customized = AppSettings.Parse("""{"HotkeyKey":120,"HotkeyModifiers":4}""");
        Assert.Equal(new Hotkey(Hotkey.Shift, 120), RecordingHotkeys.Read(customized, RecordingCommand.SaveClip));
    }

    [Fact]
    public void Registration_reports_internal_and_windows_conflicts_without_binding_them()
    {
        var settings = new AppSettings
        {
            MasterHotkey = new(Hotkey.Alt, 0x79),
            DesktopHotkey = new(Hotkey.Control, 0x78),
            SwitchMonitorHotkey = new(0x100, 0x77),
        };
        var calls = new List<RecordingCommand>();
        var results = RecordingHotkeys.Register(settings, true, (command, _) =>
        {
            calls.Add(command);
            return command == RecordingCommand.SaveClip;
        });
        Assert.Equal([RecordingCommand.SaveClip, RecordingCommand.ToggleDesktop], calls);
        Assert.Equal(HotkeyRegistration.Registered, results[RecordingCommand.SaveClip]);
        Assert.Equal(HotkeyRegistration.Duplicate, results[RecordingCommand.ToggleMaster]);
        Assert.Equal(HotkeyRegistration.Unavailable, results[RecordingCommand.ToggleDesktop]);
        Assert.Equal(HotkeyRegistration.Invalid, results[RecordingCommand.SwitchMonitor]);
        Assert.Single(RecordingHotkeys.Register(settings, false, (_, _) => true));
    }

    [Fact]
    public void Cycling_requires_a_different_connected_authorized_display()
    {
        MonitorInfo Make(string id) => new(id, "", 0, 0, 1920, 1080) { CaptureIdentity = id };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "offline" };
        MonitorInfo[] connected = [Make("A"), Make("unauthorized"), Make("B")];
        Assert.Equal("B", CaptureSourcePolicy.NextAuthorizedDisplay(connected, allowed, "A"));
        Assert.Equal("A", CaptureSourcePolicy.NextAuthorizedDisplay(connected, allowed, "B"));
        Assert.Null(CaptureSourcePolicy.NextAuthorizedDisplay([Make("A"), Make("unauthorized")], allowed, "A"));
        Assert.Null(CaptureSourcePolicy.NextAuthorizedDisplay([Make("unauthorized")], allowed, "offline"));
    }
}

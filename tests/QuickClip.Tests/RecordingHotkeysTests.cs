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
        Assert.Equal(RecordingHotkeys.Read(fresh, RecordingCommand.SaveClip), RecordingHotkeys.Default(RecordingCommand.SaveClip));
        foreach (var command in Enum.GetValues<RecordingCommand>())
            RecordingHotkeys.Write(customized, command, default);
        var reloaded = AppSettings.Parse(customized.ToJson());
        Assert.All(Enum.GetValues<RecordingCommand>(), command => Assert.True(RecordingHotkeys.Read(reloaded, command).IsEmpty));
        Assert.All(Enum.GetValues<RecordingCommand>().Skip(1), command => Assert.True(RecordingHotkeys.Default(command).IsEmpty));
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

    [Theory]
    [InlineData(Hotkey.Control, 0x11, (int)HotkeyRegistration.Invalid)]
    [InlineData(0, 0xA4, (int)HotkeyRegistration.Invalid)]
    [InlineData(0, 0x7B, (int)HotkeyRegistration.Reserved)]
    [InlineData(0, 0x2C, (int)HotkeyRegistration.Reserved)]
    [InlineData(Hotkey.Windows, 0x41, (int)HotkeyRegistration.Reserved)]
    [InlineData(Hotkey.Alt, 0x09, (int)HotkeyRegistration.Reserved)]
    [InlineData(Hotkey.Alt, 0x79, (int)HotkeyRegistration.Registered)]
    public void Rejects_modifier_keys_and_reserved_chords(int modifiers, int vk, int expected)
    {
        Assert.Equal((HotkeyRegistration)expected, RecordingHotkeys.Validate(new(modifiers, vk)));
    }

    [Fact]
    public void Failed_replacement_keeps_the_old_registration_and_clear_removes_only_its_id()
    {
        var original = RecordingHotkeys.Default(RecordingCommand.SaveClip);
        var taken = new Hotkey(Hotkey.Control, 0x79);
        var replacement = new Hotkey(Hotkey.Shift, 0x79);
        int id = 1;
        var active = new Dictionary<int, Hotkey> { [1] = original, [2] = taken };
        bool Register(int next, Hotkey key) => !active.ContainsValue(key) && active.TryAdd(next, key);
        Assert.Equal(HotkeyRegistration.Unavailable, RecordingHotkeys.Replace(taken, ref id, true, Register, active.Remove));
        Assert.Equal(1, id);
        Assert.Equal(original, active[id]);
        Assert.Equal(HotkeyRegistration.Registered, RecordingHotkeys.Replace(replacement, ref id, true, Register, active.Remove));
        Assert.False(active.ContainsKey(1));
        Assert.Equal(replacement, active[id]);
        Assert.Equal(HotkeyRegistration.Unbound, RecordingHotkeys.Replace(default, ref id, true, Register, active.Remove));
        Assert.Single(active);
        Assert.Equal(taken, active[2]);
    }

    [Fact]
    public void Personal_hotkeys_respect_first_run_and_block_saves_during_master_shutdown()
    {
        var settings = new AppSettings { RecordingEnabled = false };
        Assert.False(RecordingHotkeys.CanDispatch(RecordingCommand.ToggleMaster, settings, true, false));
        Assert.False(RecordingHotkeys.CanDispatch(RecordingCommand.SaveClip, settings, true, true));
        Assert.True(RecordingHotkeys.CanDispatch(RecordingCommand.SaveClip, settings, true, false));
        settings.ReplayOnboardingCompleted = true;
        Assert.True(RecordingHotkeys.CanDispatch(RecordingCommand.ToggleMaster, settings, true, false));
        settings.RecordingEnabled = true;
        Assert.True(RecordingHotkeys.CanDispatch(RecordingCommand.SaveClip, settings, true, true));
    }

    [Fact]
    public void Manual_approval_expires_even_if_the_master_switch_is_immediately_turned_back_on()
    {
        var settings = new AppSettings { RecordingEnabled = true, AuthorizedGameDisplay = "game-display" };
        int consent = settings.RecordingDisableRevision;
        Assert.True(settings.AllowsManualDesktop(consent));

        settings.RecordingEnabled = false;
        Assert.False(settings.AllowsManualDesktop(consent));
        settings.RecordingEnabled = true;

        Assert.False(settings.AllowsManualDesktop(consent));
        Assert.True(settings.AllowsManualDesktop(settings.RecordingDisableRevision));
        Assert.Equal("game-display", settings.AuthorizedGameDisplay);
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

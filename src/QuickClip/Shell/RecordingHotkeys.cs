namespace QuickClip.Shell;

internal enum RecordingCommand { SaveClip = 1, ToggleMaster, ToggleDesktop, SwitchMonitor }
internal enum HotkeyRegistration { Unbound, Registered, Invalid, Duplicate, Unavailable }

internal static class RecordingHotkeys
{
    public static Hotkey Default(RecordingCommand command) => command == RecordingCommand.SaveClip
        ? new(Hotkey.Alt, 0x79) : default;

    public static Hotkey Read(AppSettings settings, RecordingCommand command) => command switch
    {
        RecordingCommand.SaveClip => new(settings.HotkeyModifiers, settings.HotkeyKey),
        RecordingCommand.ToggleMaster => settings.MasterHotkey,
        RecordingCommand.ToggleDesktop => settings.DesktopHotkey,
        RecordingCommand.SwitchMonitor => settings.SwitchMonitorHotkey,
        _ => default,
    };

    public static void Write(AppSettings settings, RecordingCommand command, Hotkey hotkey)
    {
        switch (command)
        {
            case RecordingCommand.SaveClip: (settings.HotkeyModifiers, settings.HotkeyKey) = (hotkey.Modifiers, hotkey.VirtualKey); break;
            case RecordingCommand.ToggleMaster: settings.MasterHotkey = hotkey; break;
            case RecordingCommand.ToggleDesktop: settings.DesktopHotkey = hotkey; break;
            case RecordingCommand.SwitchMonitor: settings.SwitchMonitorHotkey = hotkey; break;
        }
    }

    public static bool IsValid(Hotkey hotkey) => (hotkey.Modifiers & ~0xF) == 0 && hotkey.VirtualKey is > 0 and < 255;

    public static Dictionary<RecordingCommand, HotkeyRegistration> Register(AppSettings settings, bool personal,
        Func<RecordingCommand, Hotkey, bool> register)
    {
        var seen = new HashSet<Hotkey>();
        var results = new Dictionary<RecordingCommand, HotkeyRegistration>();
        foreach (var command in Enum.GetValues<RecordingCommand>())
        {
            if (!personal && command != RecordingCommand.SaveClip) continue;
            var binding = Read(settings, command);
            results[command] = binding.IsEmpty ? HotkeyRegistration.Unbound
                : !IsValid(binding) ? HotkeyRegistration.Invalid
                : !seen.Add(binding) ? HotkeyRegistration.Duplicate
                : register(command, binding) ? HotkeyRegistration.Registered
                : HotkeyRegistration.Unavailable;
        }
        return results;
    }
}

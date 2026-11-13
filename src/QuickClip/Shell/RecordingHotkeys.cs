namespace QuickClip.Shell;

internal enum RecordingCommand { SaveClip = 1, ToggleMaster, ToggleDesktop, SwitchMonitor }
internal enum HotkeyRegistration { Unbound, Registered, Invalid, Reserved, Duplicate, Unavailable }

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

    internal static HotkeyRegistration Validate(Hotkey key)
    {
        if (key.IsEmpty) return HotkeyRegistration.Unbound;
        if ((key.Modifiers & ~0xF) != 0 || key.VirtualKey is <= 6 or >= 255
            or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) or 0xE5 or 0xE7
            || System.Windows.Input.KeyInterop.KeyFromVirtualKey(key.VirtualKey) == System.Windows.Input.Key.None)
            return HotkeyRegistration.Invalid;
        if ((key.Modifiers & Hotkey.Windows) != 0 || key.VirtualKey is 0x7B or 0x2C or 0x1B
            || (key.Modifiers & Hotkey.Alt) != 0 && key.VirtualKey is 0x09 or 0x73
            || (key.Modifiers & (Hotkey.Control | Hotkey.Alt)) == (Hotkey.Control | Hotkey.Alt) && key.VirtualKey == 0x2E)
            return HotkeyRegistration.Reserved;
        return HotkeyRegistration.Registered;
    }

    // Register a replacement with a different ID before releasing the working shortcut.
    internal static HotkeyRegistration Replace(Hotkey key, ref int id, bool registered,
        Func<int, Hotkey, bool> register, Func<int, bool> unregister)
    {
        int next = id ^ 0x100;
        if (!key.IsEmpty && !register(next, key)) return HotkeyRegistration.Unavailable;
        if (registered && !unregister(id))
        {
            if (!key.IsEmpty) unregister(next);
            return HotkeyRegistration.Unavailable;
        }
        id = next;
        return key.IsEmpty ? HotkeyRegistration.Unbound : HotkeyRegistration.Registered;
    }

    public static Dictionary<RecordingCommand, HotkeyRegistration> Register(AppSettings settings, bool personal,
        Func<RecordingCommand, Hotkey, bool> register)
    {
        var seen = new HashSet<Hotkey>();
        var results = new Dictionary<RecordingCommand, HotkeyRegistration>();
        foreach (var command in Enum.GetValues<RecordingCommand>())
        {
            if (!personal && command != RecordingCommand.SaveClip) continue;
            var binding = Read(settings, command);
            var validation = Validate(binding);
            results[command] = validation != HotkeyRegistration.Registered ? validation
                : !seen.Add(binding) ? HotkeyRegistration.Duplicate
                : register(command, binding) ? HotkeyRegistration.Registered
                : HotkeyRegistration.Unavailable;
        }
        return results;
    }
}

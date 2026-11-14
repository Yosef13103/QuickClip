using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace QuickClip.Shell;

/// <summary>"Start with Windows": a per-user Run entry that launches QuickClip into the tray.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickClip";
    internal const string PersonalValueName = "QuickClip Personal";
    private const string PersonalInstallKey = @"Software\QuickClip Personal\Install";
    internal const string WindowsStartupArgument = "--windows-startup";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsAvailable
    {
        get
        {
            if (!AppProfile.IsPersonal) return true;
#if DEBUG
            return false;
#else
            if (!AppProfile.UsesDefaultDataRoot || AppSettings.SaveDisabled) return false;
            using var install = Registry.CurrentUser.OpenSubKey(PersonalInstallKey);
            return MatchesInstallation(Environment.ProcessPath, install?.GetValue("Executable") as string);
#endif
        }
    }

    public static bool IsEnabled
    {
        get
        {
            if (!IsAvailable) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (AppProfile.IsPersonal)
                return string.Equals(key?.GetValue(PersonalValueName) as string, PersonalCommand(Environment.ProcessPath),
                    StringComparison.OrdinalIgnoreCase);
            return key?.GetValue(ValueName) is string cmd && cmd.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        if (!IsAvailable) throw new InvalidOperationException("Windows startup requires an installed copy of QuickClip Personal.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (AppProfile.IsPersonal)
        {
            SetPersonalEntry(key, Environment.ProcessPath!, enabled);
            return;
        }
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static bool MatchesInstallation(string? processPath, string? installedPath) =>
        processPath != null && installedPath != null && Path.IsPathFullyQualified(installedPath) &&
        string.Equals(processPath, installedPath, StringComparison.OrdinalIgnoreCase);

    internal static string? PersonalCommand(string? executable) =>
        executable != null && Path.IsPathFullyQualified(executable) && executable.IndexOfAny(['"', '\r', '\n']) < 0
            && $"\"{executable}\" {WindowsStartupArgument}" is { Length: <= 260 } command ? command : null;

    internal static void SetPersonalEntry(RegistryKey key, string executable, bool enabled)
    {
        if (enabled)
            key.SetValue(PersonalValueName, PersonalCommand(executable) ??
                throw new InvalidOperationException("The installation path is too long or invalid for Windows startup."));
        else key.DeleteValue(PersonalValueName, throwOnMissingValue: false);
    }
}

/// <summary>Keeps one QuickClip per user: later launches hand their arguments to the running one.</summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;

    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, AppProfile.MutexName, out bool created);
        return created;
    }

    public static bool SendToPrimary(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AppProfile.PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args.Length == 0 ? ["--show"] : args));
            pipe.Write(bytes);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Handing over to the running QuickClip failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Calls <paramref name="onArgs"/> (on a worker thread) with each later launch's arguments.</summary>
    public static void Listen(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(AppProfile.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string text = await reader.ReadToEndAsync();
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception ex)
                {
                    Log.Write("Instance pipe error: " + ex.Message);
                    await Task.Delay(1000);
                }
            }
        });
    }
}

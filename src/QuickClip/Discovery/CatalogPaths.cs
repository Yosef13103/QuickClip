namespace QuickClip.Discovery;

internal static class CatalogPaths
{
    internal static string FolderId(string path) => "folder:" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    // Accept ordinary drive paths only. Device namespaces, UNC and drive-relative paths
    // have different normalization and traversal behavior.
    public static string? LocalPath(string? path)
    {
        if (path == null || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] is not ('\\' or '/') || path.AsSpan(2).Contains(':'))
            return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var part in full[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                if (!SafeLeaf(part)) return null;
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool SafeLeaf(string? name) => !string.IsNullOrWhiteSpace(name)
        && name[^1] is not ('.' or ' ') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !ReservedDevice(name.Split('.')[0].TrimEnd());

    private static bool ReservedDevice(string name)
    {
        name = name.ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
                name.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(name[3]));
    }

    public static bool IsWithin(string path, string directory) =>
        path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == directory.Length || Path.EndsInDirectorySeparator(directory) ||
            path[directory.Length] == Path.DirectorySeparatorChar);

    public static bool IsExecutable(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    internal const string MinecraftRuntimeRelative = @"runtime\java-runtime-epsilon\windows-x64\java-runtime-epsilon\bin\javaw.exe";
    internal const string MinecraftLauncherFolder = "Minecraft Launcher";

    internal static bool IsMinecraftBundledRuntime(string path) =>
        path.EndsWith("\\" + MinecraftLauncherFolder + "\\" + MinecraftRuntimeRelative, StringComparison.OrdinalIgnoreCase);

    public static bool IsJavaRuntime(string path) => Path.GetFileNameWithoutExtension(path) is { } name &&
        (name.Equals("java", StringComparison.OrdinalIgnoreCase) || name.Equals("javaw", StringComparison.OrdinalIgnoreCase));

    internal static bool IsRedistributablePath(string path)
    {
        var parts = path.Split('\\', '/');
        for (int i = 0; i + 2 < parts.Length; i++)
            if (parts[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase) &&
                parts[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase) &&
                (parts[i + 2].Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase) ||
                 parts[i + 2].Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    internal static bool IsRedistributable(CatalogEntry entry) =>
        entry.Id.Equals("steam:228980", StringComparison.OrdinalIgnoreCase) ||
        (!entry.InstallDirectories.IsEmpty && entry.InstallDirectories.All(IsRedistributablePath));

    internal static bool IsProtectedExecutable(string path) =>
        LocalPath(path) == null || !IsExecutable(path) || IsRedistributablePath(path) ||
        (WindowsDirectory.Length > 0 && IsWithin(path, WindowsDirectory)) ||
        Path.GetFileNameWithoutExtension(path).ToUpperInvariant() is
            "QUICKCLIP" or "SYSTEM" or "REGISTRY" or "CSRSS" or "LSASS" or "WINLOGON" or "WININIT" or
            "SERVICES" or "SMSS" or "SVCHOST" or "DWM" or "EXPLORER";

    public static CatalogMatchKind DefaultRole(string path)
    {
        if (IsRedistributablePath(path)) return CatalogMatchKind.Ignored;
        if (WindowsDirectory.Length > 0 && IsWithin(path, WindowsDirectory)) return CatalogMatchKind.Application;
        if (IsDiscordUpdater(path)) return CatalogMatchKind.Ignored;
        if (IsDiscordProcess(path)) return CatalogMatchKind.Companion;
        string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        if (name == "ZOOM") return CatalogMatchKind.Companion;
        if (name is "STEAM" or "STEAMWEBHELPER" or "EPICGAMESLAUNCHER" or "LAUNCHER" or "GAMELAUNCHER"
            or "BATTLE.NET" or "RIOTCLIENTSERVICES" or "EADESKTOP" or "EALAUNCHER" or "GALAXYCLIENT"
            or "UBISOFTCONNECT" or "ROCKSTAR GAMES LAUNCHER" or "BOOTSTRAPPACKAGEDGAME" or "START_PROTECTED_GAME"
            or "MCPLACEHOLDERSTUB"
            || name.EndsWith("LAUNCHER", StringComparison.Ordinal))
            return CatalogMatchKind.Launcher;
        if (name is "7ZA" or "SETUP" or "INSTALL" or "INSTALLER" or "UPDATE" or "UPDATER"
            or "7Z" or "7ZR" or "UNRAR" or "DXSETUP" or "PREREQUISITES" or "WERFAULT"
            or "JAVAC" or "DOTNET" or "PYTHON" or "PYTHONW" or "NODE"
            or "RUNDLL32" or "REGSVR32" or "WSCRIPT" or "CSCRIPT" or "MSIEXEC" or "MODTOOL"
            or "CRASHPAD_HANDLER" or "UNITYCRASHHANDLER32" or "UNITYCRASHHANDLER64" or "CRASHREPORTCLIENT"
            || name.EndsWith("HELPER", StringComparison.Ordinal)
            || name.EndsWith("CRASHHANDLER", StringComparison.Ordinal)
            || name.StartsWith("UNINS", StringComparison.Ordinal)
            || name.StartsWith("VC_REDIST", StringComparison.Ordinal)
            || name.EndsWith("CRASHREPORTER", StringComparison.Ordinal)
            || path.Split('\\', '/').SkipLast(1).Contains("CrashReporter", StringComparer.OrdinalIgnoreCase))
            return CatalogMatchKind.Application;
        return CatalogMatchKind.Unknown;
    }

    public static bool IsDiscordUpdater(string path) =>
        Path.GetFileNameWithoutExtension(path).Equals("Update", StringComparison.OrdinalIgnoreCase)
        && IsDiscord(Path.GetFileName(Path.GetDirectoryName(path)) ?? "");

    public static bool IsDiscord(string name) =>
        name.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase);

    public static bool IsDiscordProcess(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (IsDiscord(name) || name.Equals("DiscordDevelopment", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DiscordOverlay", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DiscordHookHelper", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DiscordHookHelper64", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsDiscordUpdater(path)) return true;
        return path.Split('\\', '/').SkipLast(1).Any(part =>
            IsDiscord(part) || part.Equals("DiscordDevelopment", StringComparison.OrdinalIgnoreCase));
    }
}

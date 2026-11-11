namespace QuickClip.Discovery;

internal static class CatalogPaths
{
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
        path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsExecutable(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsDiscordUpdater(string path) =>
        Path.GetFileNameWithoutExtension(path).Equals("Update", StringComparison.OrdinalIgnoreCase)
        && IsDiscord(Path.GetFileName(Path.GetDirectoryName(path)) ?? "");

    public static bool IsDiscord(string name) =>
        name.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase);
}

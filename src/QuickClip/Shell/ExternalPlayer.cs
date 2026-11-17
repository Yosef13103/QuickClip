using System.Diagnostics;

namespace QuickClip.Shell;

/// <summary>QuickClip-only external player preference. Never changes Windows associations.</summary>
internal static class ExternalPlayer
{
    internal static string Label(string? executable) => string.IsNullOrWhiteSpace(executable)
        ? "Windows default app" : Path.GetFileNameWithoutExtension(executable).Equals("vlc", StringComparison.OrdinalIgnoreCase)
            ? "VLC media player" : Path.GetFileNameWithoutExtension(executable);

    internal static string? FindVlc()
    {
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(folder)) continue;
            string candidate = Path.Combine(folder, "VideoLAN", "VLC", "vlc.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static bool IsExecutablePath(string? executable) =>
        !string.IsNullOrWhiteSpace(executable) && Path.IsPathFullyQualified(executable) &&
        string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(executable) && !string.Equals(Path.GetFullPath(executable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo BuildStartInfo(string path, string? executable)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new IOException("The saved clip is missing or unavailable.");
        if (string.IsNullOrWhiteSpace(executable))
            return new ProcessStartInfo(path) { UseShellExecute = true };
        if (!IsExecutablePath(executable))
            throw new IOException("The selected player is unavailable. Change it in General settings or choose another player.");
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(path));
        return start;
    }
}

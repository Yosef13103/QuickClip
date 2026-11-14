using QuickClip.Library;

namespace QuickClip.Views;

internal enum EditorSourceState { Ready, Missing, Unreadable, Replaced }

internal static class EditorSource
{
    internal static bool Matches(string current, IEnumerable<string> paths) =>
        paths.Any(path => string.Equals(current, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// mpv can report a normalized path or file URI while Gallery reports a Windows path.
    /// A delayed FileLoaded event must not reveal a *different* deleted/previous recording.
    /// </summary>
    internal static bool MatchesLoadedPreview(string? playerPath, string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(playerPath) || string.IsNullOrWhiteSpace(requestedPath))
            return false;
        try
        {
            if (Uri.TryCreate(playerPath, UriKind.Absolute, out var uri) && uri.IsFile)
                playerPath = uri.LocalPath;
            return string.Equals(Path.GetFullPath(playerPath), Path.GetFullPath(requestedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException)
        {
            return false;
        }
    }

    internal static EditorSourceState Check(string path, FileStamp? expected, out FileStamp stamp)
    {
        stamp = default;
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stamp = LibraryFiles.ReadStamp(file);
            return expected != null && stamp != expected ? EditorSourceState.Replaced : EditorSourceState.Ready;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return EditorSourceState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EditorSourceState.Unreadable;
        }
    }

    internal static string Message(EditorSourceState state) => state switch
    {
        EditorSourceState.Missing => "This recording is missing. Restore it from the Recycle Bin or move it back, or open another video.",
        EditorSourceState.Replaced => "A different or modified file is at this location. Restore the original recording or open another video.",
        _ => "This recording can’t be read. Check that it’s available and unlocked, then retry or open another video.",
    };
}

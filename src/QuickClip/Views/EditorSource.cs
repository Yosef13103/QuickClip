using QuickClip.Library;

namespace QuickClip.Views;

internal enum EditorSourceState { Ready, Missing, Unreadable, Replaced }

internal static class EditorSource
{
    internal static bool Matches(string current, IEnumerable<string> paths) =>
        paths.Any(path => string.Equals(current, path, StringComparison.OrdinalIgnoreCase));

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

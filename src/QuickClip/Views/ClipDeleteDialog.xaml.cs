using System.Windows;

namespace QuickClip.Views;

internal static class ClipDeleteDialog
{
    internal static bool Confirm(Window? owner, IReadOnlyList<string> paths)
    {
        if (!AppSettings.Current.ConfirmClipDeletion) return true;
        string heading = paths.Count == 1 ? "Move clip to the Recycle Bin?" : $"Move {paths.Count} clips to the Recycle Bin?";
        string files = string.Join("\n", paths.Take(5).Select(Path.GetFileName));
        if (paths.Count > 5) files += $"\n… and {paths.Count - 5} more";
        return ModalDialog.ConfirmDelete(owner, heading, files);
    }
}

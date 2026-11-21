using System.Runtime.InteropServices;
using System.Text;

namespace QuickClip.Recording.Companion;

// Select only the already-authorized main Discord HWND; do not reinterpret a
// pop-out or another process as the main window when it closes.
internal static class DiscordMainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Bounds bounds, int size);

    internal static WindowOwner? Select(IReadOnlyList<WindowOwner> authorized,
        WindowOwner? previous, Func<WindowOwner, bool> isMain, Func<WindowOwner, long> pixelArea)
    {
        if (previous is { } pinned && authorized.Contains(pinned)) return pinned;
        var matches = authorized.Where(isMain).ToArray();
        return matches.Length == 0 ? null : matches.MaxBy(pixelArea);
    }

    // Discord's main window title varies by workspace, usually ending in
    // " - Discord". Do not log titles, which may contain private information.
    internal static bool HasMainTitle(WindowOwner window)
    {
        var text = new StringBuilder(512);
        if (GetWindowText(window.Handle, text, text.Capacity) <= 0) return false;
        var title = text.ToString().Trim();
        return title.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
            title.EndsWith(" - Discord", StringComparison.OrdinalIgnoreCase);
    }

    internal static long Area(WindowOwner window) =>
        TrySize(window, out int width, out int height) ? (long)width * height : 0;

    // DWMWA_EXTENDED_FRAME_BOUNDS excludes the invisible resize border.
    // These are physical pixels even for negative-origin secondary displays.
    internal static bool TrySize(WindowOwner window, out int width, out int height)
    {
        width = height = 0;
        if (DwmGetWindowAttribute(window.Handle, 9, out var bounds, Marshal.SizeOf<Bounds>()) != 0)
            return false;
        width = bounds.Right - bounds.Left;
        height = bounds.Bottom - bounds.Top;
        return width is >= 64 and <= 4096 && height is >= 64 and <= 4096;
    }
}

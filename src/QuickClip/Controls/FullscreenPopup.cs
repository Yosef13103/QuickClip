using System.Windows;
using System.Windows.Controls.Primitives;

namespace QuickClip.Controls;

internal static class FullscreenPopup
{
    internal static CustomPopupPlacement[] Place(Size popup, Size target, Point offset) =>
        [new(new Point(Math.Max(0, (target.Width - popup.Width) / 2), Math.Max(0, target.Height - popup.Height - 14)),
            PopupPrimaryAxis.Horizontal)];

    internal static void UpdateWidth(Popup popup, FrameworkElement content, double availableWidth)
    {
        content.Width = Math.Max(1, Math.Min(Math.Max(420, availableWidth * .5), Math.Min(620, availableWidth - 28)));
        // A popup owns a separate HWND. Force it to recalculate its position
        // when the full-screen client's size or DPI changes.
        popup.HorizontalOffset = .01;
        popup.HorizontalOffset = 0;
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickClip.Recording;

namespace QuickClip.Shell;

internal enum RecordingBadge { Off, Standby, Game, Desktop }

internal static class RecordingStatusIcon
{
    internal static RecordingBadge From(bool enabled, RecorderState state) =>
        !enabled ? RecordingBadge.Off : !state.Running ? RecordingBadge.Standby :
        state.Mode == RecordingMode.ManualDesktop ? RecordingBadge.Desktop :
        state.Mode == RecordingMode.GameTriggered ? RecordingBadge.Game : RecordingBadge.Standby;

    internal static string BrushKey(RecordingBadge badge) => badge switch
    {
        RecordingBadge.Game => "RecordingRed",
        RecordingBadge.Desktop => "AccentBrush",
        RecordingBadge.Standby => "WarningBrush",
        _ => "SecondaryTextBrush"
    };

    private static Color ColorFor(RecordingBadge badge) => badge switch
    {
        RecordingBadge.Game => Color.FromRgb(239, 72, 85),
        RecordingBadge.Desktop => Color.FromRgb(67, 169, 255),
        _ => Color.FromRgb(250, 194, 70)
    };

    private static void DrawBadge(DrawingContext dc, RecordingBadge badge, Point center, double radius)
    {
        dc.DrawEllipse(new SolidColorBrush(ColorFor(badge)), new Pen(new SolidColorBrush(Color.FromRgb(27, 32, 40)), 1.5),
            center, radius, radius);
    }

    internal static ImageSource? Overlay(RecordingBadge badge)
    {
        if (badge == RecordingBadge.Off) return null;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 16, 16));
            DrawBadge(dc, badge, new Point(8, 8), 6.5);
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    // Native 32-bit icon resource (BITMAPINFOHEADER, bottom-up BGRA, AND mask).
    // Called once per badge, never on a frame or sampling loop.
    internal static IntPtr MakeTrayIcon(IntPtr original, RecordingBadge badge)
    {
        if (original == IntPtr.Zero || badge == RecordingBadge.Off) return IntPtr.Zero;
        try
        {
            const int size = 32, stride = size * 4;
            var source = Imaging.CreateBitmapSourceFromHIcon(original, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new Rect(0, 0, size, size));
                DrawBadge(dc, badge, new Point(24, 24), 6.5);
            }
            var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var straight = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[stride * size];
            straight.CopyPixels(pixels, stride, 0);
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            writer.Write(40); writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int y = size - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
            for (int y = size - 1; y >= 0; y--)
            {
                uint unused = 0;
                for (int x = 0; x < size; x++)
                    if (pixels[y * stride + x * 4 + 3] == 0) unused |= 1u << (31 - x);
                for (int shift = 24; shift >= 0; shift -= 8) writer.Write((byte)(unused >> shift));
            }
            var bytes = buffer.ToArray();
            return CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, size, size, 0);
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.General, "Status tray icon unavailable", ex);
            return IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint length, [MarshalAs(UnmanagedType.Bool)] bool icon,
        uint version, int width, int height, uint flags);
}

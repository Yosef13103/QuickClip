using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Shell;

namespace QuickClip;

public partial class MainWindow
{
    private WindowChrome? _personalChrome;

    private void InitializeTitleBar()
    {
        if (!AppProfile.IsPersonal || SystemParameters.HighContrast ||
            Environment.GetCommandLineArgs().Contains("--system-chrome", StringComparer.OrdinalIgnoreCase))
            return;

        _personalChrome = new WindowChrome
        {
            CaptionHeight = 34,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        };
        // Install our hit-test hook ahead of WindowChrome. WPF handles the other non-client messages.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(CaptionWindowProc);
        };
        StateChanged += (_, _) => UpdateMaximizeButton();
        RailIcon.Visibility = Visibility.Collapsed;
        UpdateTitleBar(fullscreen: false);
    }

    private void UpdateTitleBar(bool fullscreen)
    {
        if (_personalChrome == null) return;
        WindowChrome.SetWindowChrome(this, fullscreen ? null : _personalChrome);
        TitleBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateMaximizeButton()
    {
        bool maximized = WindowState == WindowState.Maximized;
        string label = maximized ? "Restore" : "Maximize";
        CaptionMaximize.Content = maximized ? "\uE923" : "\uE922";
        CaptionMaximize.ToolTip = label;
        AutomationProperties.SetName(CaptionMaximize, label);
    }

    private void CaptionMinimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CaptionClose_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void CaptionMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CaptionMenu_Click(object sender, RoutedEventArgs e)
    {
        var origin = CaptionMenu.TranslatePoint(new Point(0, CaptionMenu.ActualHeight), this);
        // ShowSystemMenu expects desktop DIPs; PointToScreen gives physical pixels.
        var target = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (target != null)
            SystemCommands.ShowSystemMenu(this, target.TransformFromDevice.Transform(PointToScreen(origin)));
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || WindowChrome.GetWindowChrome(this) == null || !TitleBar.IsVisible) return;
        if (e.Key == System.Windows.Input.Key.System && e.SystemKey == System.Windows.Input.Key.Space)
        {
            e.Handled = true;
            CaptionMenu_Click(this, e);
        }
    }

    private IntPtr CaptionWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_windowFullScreen || WindowChrome.GetWindowChrome(this) == null) return IntPtr.Zero;

        const int nonClientCalculateSize = 0x0083;
        if (message == nonClientCalculateSize && (IsZoomed(hwnd) || WindowState == WindowState.Maximized))
        {
            // A maximized thick-frame HWND extends past the visible monitor work area.
            // Fit the client rectangle itself so content is laid out inside that area.
            var proposed = Marshal.PtrToStructure<NativeRect>(lParam);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(MonitorFromRect(ref proposed, 2), ref info))
            {
                proposed.Left = Math.Max(proposed.Left, info.Work.Left);
                proposed.Top = Math.Max(proposed.Top, info.Work.Top);
                proposed.Right = Math.Min(proposed.Right, info.Work.Right);
                proposed.Bottom = Math.Min(proposed.Bottom, info.Work.Bottom);
                Marshal.StructureToPtr(proposed, lParam, false);
                handled = true;
                return IntPtr.Zero;
            }
        }

        const int nonClientHitTest = 0x0084;
        if (message != nonClientHitTest || !TitleBar.IsVisible) return IntPtr.Zero;
        var screen = DecodeScreenPoint(lParam);
        var local = TitleBar.PointFromScreen(screen);
        if (!ContainsPoint(TitleBar, local)) return IntPtr.Zero;

        // Windows 11 owns the hover flyout and non-client mouse click. The WPF button
        // still provides a keyboard and UI Automation path through SystemCommands.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
            ContainsPoint(CaptionMaximize, TitleBar.TranslatePoint(local, CaptionMaximize)))
        {
            handled = true;
            return new IntPtr(9); // HTMAXBUTTON
        }

        // WindowChrome's maximized hit-test origin is the outer HWND, which is now
        // offset from our clamped client area. Use the actual visual for the caption.
        if (WindowState == WindowState.Maximized)
        {
            handled = true;
            return new IntPtr(TitleBar.InputHitTest(local) is { } element &&
                WindowChrome.GetIsHitTestVisibleInChrome(element) ? 1 : 2); // HTCLIENT / HTCAPTION
        }
        return IntPtr.Zero;
    }

    private static bool ContainsPoint(FrameworkElement element, Point point) =>
        new Rect(new Point(), element.RenderSize).Contains(point);

    internal static Point DecodeScreenPoint(IntPtr value) =>
        new(unchecked((short)(long)value), unchecked((short)((long)value >> 16)));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

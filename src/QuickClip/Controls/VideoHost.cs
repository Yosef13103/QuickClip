using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickClip.Controls;

/// <summary>
/// Native child window that mpv renders into. mpv creates its own window inside this one; we disable
/// that window so Windows routes its mouse input here instead (disabled children forward to the parent).
/// Coordinates in the mouse events are device pixels relative to this window.
/// </summary>
public sealed class VideoHost : HwndHost
{
    private const string ClassName = "QuickClipVideoHost";
    private static WndProcDelegate? _defProc;
    private static bool _classRegistered;

    public event Action<int, int>? VideoMouseDown;
    public event Action<int, int>? VideoMouseMove;
    public event Action<int, int>? VideoMouseUp;
    /// <summary>Ctrl+wheel: native client coordinates and signed wheel delta.</summary>
    public event Action<int, int, int>? VideoCtrlWheel;
    public Func<int, int, bool>? VideoCtrlPanStart { get; set; }
    public Action<int, int>? VideoCtrlPanMove { get; set; }
    public Action? VideoCtrlPanEnd { get; set; }
    public event Action? VideoContextRequested;

    private bool _ctrlPanCaptured;
    internal (double Width, double Height) NativeClientSize
    {
        get
        {
            if (Hwnd != IntPtr.Zero && GetClientRect(Hwnd, out var rect) &&
                rect.Right > rect.Left && rect.Bottom > rect.Top)
                return (rect.Right - rect.Left, rect.Bottom - rect.Top);
            return (Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        }
    }

    /// <summary>Asked on WM_SETCURSOR with the cursor position; return a Win32 IDC_* id, 0 for the arrow or -1 for none.</summary>
    public Func<int, int, int>? CursorQuery { get; set; }

    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IntPtr Hwnd { get; private set; }

    /// <summary>
    /// Completes once the native window exists, which is when the control is first shown, not while it's collapsed.
    /// Start mpv after this: given no window, it opens one of its own.
    /// </summary>
    public Task Created => _created.Task;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClass();
        Hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the video window.");
        _created.TrySetResult();
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        EndCtrlPan();
        DestroyWindow(hwnd.Handle);
        if (Hwnd == hwnd.Handle) Hwnd = IntPtr.Zero;
    }

    internal bool NativeVisible => Hwnd != IntPtr.Zero && IsWindow(Hwnd) && IsWindowVisible(Hwnd);

    /// <summary>
    /// Re-syncs the native child with WPF after the main window returns from minimized/hidden.
    /// A HwndHost can retain a stale composited surface even though libmpv is still decoding
    /// (dragging the top-level window forces the Win32 child to redraw).
    /// Keep the same HWND and mpv decoder: do not seek or reload the user's clip.
    /// </summary>
    public void RefreshAfterRestore()
    {
        if (Hwnd == IntPtr.Zero || !IsVisible || ActualWidth <= 1 || ActualHeight <= 1 || !IsWindow(Hwnd)) return;

        // HwndHost knows the current WPF coordinates, clipping and DPI mapping.
        // This forces a native layout sync even if the WPF element did not resize.
        UpdateWindowPos();

        // Do not force a non-client frame change or synchronous redraw of an
        // embedded render thread. Re-expose only the currently visible host.
        if (!IsWindowVisible(Hwnd))
            SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        RedrawWindow(Hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN);
        DisableChildWindows();
    }

    /// <summary>Disables mpv's child window(s) so their mouse input reaches us.</summary>
    public void DisableChildWindows()
    {
        if (Hwnd == IntPtr.Zero) return;
        EnumChildWindows(Hwnd, (child, _) =>
        {
            if (IsWindowEnabled(child)) EnableWindow(child, false);
            return true;
        }, IntPtr.Zero);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        switch (msg)
        {
            case WM_MOUSEWHEEL when (wParam.ToInt64() & MK_CONTROL) != 0 && VideoCtrlWheel != null:
                // WM_MOUSEWHEEL lParam uses SCREEN, not client, coordinates.
                var wheel = new POINT { X = x, Y = y };
                if (ScreenToClient(hwnd, ref wheel))
                    VideoCtrlWheel.Invoke(wheel.X, wheel.Y,
                        unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF)));
                handled = true;
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                if ((wParam.ToInt64() & MK_CONTROL) != 0 && VideoCtrlPanStart?.Invoke(x, y) == true)
                {
                    _ctrlPanCaptured = true;
                    SetCapture(hwnd);
                    handled = true;
                    return IntPtr.Zero;
                }
                SetCapture(hwnd);
                VideoMouseDown?.Invoke(x, y);
                handled = true;
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (_ctrlPanCaptured)
                {
                    if ((wParam.ToInt64() & MK_LBUTTON) == 0)
                    {
                        EndCtrlPan();
                        ReleaseCapture();
                    }
                    else VideoCtrlPanMove?.Invoke(x, y);
                }
                else VideoMouseMove?.Invoke(x, y);
                ApplyCursor(x, y);
                handled = true;
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (_ctrlPanCaptured)
                {
                    EndCtrlPan();
                    ReleaseCapture();
                    handled = true;
                    return IntPtr.Zero;
                }
                ReleaseCapture();
                VideoMouseUp?.Invoke(x, y);
                handled = true;
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
            case WM_CANCELMODE:
                EndCtrlPan();
                break;
            case WM_RBUTTONUP when VideoContextRequested != null:
                VideoContextRequested.Invoke();
                handled = true;
                return IntPtr.Zero;
            case WM_SETCURSOR:
                if (GetCursorPos(out var pt) && ScreenToClient(hwnd, ref pt))
                {
                    ApplyCursor(pt.X, pt.Y);
                    handled = true;
                    return new IntPtr(1);
                }
                break;
            case WM_ERASEBKGND:
                handled = true;
                return new IntPtr(1);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void EndCtrlPan()
    {
        if (!_ctrlPanCaptured) return;
        _ctrlPanCaptured = false;
        VideoCtrlPanEnd?.Invoke();
    }

    private void ApplyCursor(int x, int y)
    {
        int id = _ctrlPanCaptured ? IDC_SIZEALL : CursorQuery?.Invoke(x, y) ?? 0;
        SetCursor(id < 0 ? IntPtr.Zero : LoadCursor(IntPtr.Zero, id == 0 ? IDC_ARROW : id));
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        _defProc = DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_defProc),
            hInstance = GetModuleHandle(null),
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* already exists */)
            throw new InvalidOperationException("RegisterClassEx failed.");
        _classRegistered = true;
    }

    /// <summary>Applies <see cref="CursorQuery"/> again (e.g. to hide the pointer without waiting for it to move).</summary>
    public void RefreshCursor()
    {
        if (Hwnd == IntPtr.Zero || !GetCursorPos(out var pt)) return;
        IntPtr under = WindowFromPoint(pt);
        if ((under == Hwnd || IsChild(Hwnd, under)) && ScreenToClient(Hwnd, ref pt)) ApplyCursor(pt.X, pt.Y);
    }

    public const int IDC_ARROW = 32512, IDC_CROSS = 32515, IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643,
        IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_SIZEALL = 32646, IDC_HAND = 32649;

    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
        SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const uint RDW_INVALIDATE = 0x0001, RDW_ALLCHILDREN = 0x0080;
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int BLACK_BRUSH = 4;
    private const int WM_SETCURSOR = 0x0020, WM_ERASEBKGND = 0x0014, WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A,
        WM_CAPTURECHANGED = 0x0215, WM_CANCELMODE = 0x001F, MK_CONTROL = 0x0008, MK_LBUTTON = 0x0001, WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

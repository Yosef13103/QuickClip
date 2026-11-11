namespace QuickClip.Recording;

internal enum CaptureSourceMode { GameWindowOnly, SelectedMonitor, Automatic }

internal readonly record struct CaptureSourceDecision(CaptureEngine.MonitorBackend Backend, string? Error)
{
    internal bool Allowed => Error == null;
}

internal static class CaptureSourcePolicy
{
    internal static CaptureSourceDecision Resolve(CaptureSourceMode mode, CaptureEngine.MonitorBackend monitorBackend,
        bool monitorFallbackAuthorized = false) => mode switch
    {
        CaptureSourceMode.GameWindowOnly => new(default,
            "Borderless game-window recording is unavailable. To record everything visible on a display, select monitor capture."),
        CaptureSourceMode.Automatic when !monitorFallbackAuthorized => new(default,
            "No borderless game-window backend is available and monitor fallback is disabled."),
        CaptureSourceMode.Automatic => new(CaptureEngine.MonitorBackend.MonitorOnlyDuplication, null),
        CaptureSourceMode.SelectedMonitor when monitorBackend == CaptureEngine.MonitorBackend.GraphicsCapture =>
            new(CaptureEngine.MonitorBackend.MonitorOnlyGraphics, null),
        CaptureSourceMode.SelectedMonitor when monitorBackend is CaptureEngine.MonitorBackend.Automatic or
            CaptureEngine.MonitorBackend.DesktopDuplication => new(CaptureEngine.MonitorBackend.MonitorOnlyDuplication, null),
        _ => new(default, "Unsupported capture source preference."),
    };

    internal static MonitorInfo? SelectMonitor(IReadOnlyList<MonitorInfo> monitors, string device, bool requireSelected)
    {
        if (!string.IsNullOrEmpty(device))
        {
            var selected = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));
            if (selected != null || requireSelected) return selected;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }
}

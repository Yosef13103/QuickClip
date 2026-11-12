namespace QuickClip.Recording;

internal enum CaptureSourceMode { GameWindowOnly, SelectedMonitor, Automatic }

internal readonly record struct CaptureSourceDecision(CaptureEngine.MonitorBackend Backend, string? Error)
{
    internal bool Allowed => Error == null;
}

internal static class CaptureSourcePolicy
{
    internal static string? NextAuthorizedDisplay(IReadOnlyList<MonitorInfo> monitors, IReadOnlySet<string> authorized, string current)
    {
        var connected = monitors.Select(monitor => monitor.CaptureIdentity)
            .OfType<string>().Where(authorized.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int index = Array.FindIndex(connected, display => string.Equals(display, current, StringComparison.OrdinalIgnoreCase));
        if (connected.Length == 0 || (connected.Length == 1 && index >= 0)) return null;
        return connected[(index + 1) % connected.Length];
    }
    internal static bool IsEligibleGame(GameCaptureObservation observation, Func<WindowOwner, bool> current,
        Func<string, bool, Discovery.CatalogMatch> match)
    {
        if (observation.Target is not { Recognized: true } target || target.Owner != observation.Owner
            || observation.SkipAutomaticCapture || observation.Match.Entry is not { } entry || !current(target.Owner))
            return false;
        var latest = match(observation.Executable, observation.IsForeground);
        return latest.Entry?.Id == entry.Id && latest.Kind is Discovery.CatalogMatchKind.Game or Discovery.CatalogMatchKind.Candidate;
    }

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

    internal static string GameWindowUnavailable(bool hasBorderApi) => hasBorderApi
        ? "Borderless game-window recording is unavailable in this build. Windows package capability and user consent have not been verified."
        : "Borderless game-window recording is unavailable on this Windows version. Choose the entire selected display with Desktop Duplication to record without the Windows capture border.";

    internal static CaptureSourceDecision ResolvePersonal(bool gameWindow, CaptureEngine.MonitorBackend backend,
        GameCaptureObservation? active, GameCaptureObservation? foreground, bool hasBorderApi)
    {
        if (!gameWindow) return Resolve(CaptureSourceMode.SelectedMonitor, backend);
        string unavailable = GameWindowUnavailable(hasBorderApi);
        return active?.Target == null && foreground?.SkipAutomaticCapture == true
            ? new(default, "The foreground app is ineligible for automatic game targeting. " + unavailable)
            : new(default, unavailable);
    }

    internal static bool AcceptGameDisplay(AppSettings settings, MonitorInfo? display, bool confirmed)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(display?.CaptureIdentity)) return false;
        settings.AuthorizedGameDisplay = display.CaptureIdentity;
        return true;
    }

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

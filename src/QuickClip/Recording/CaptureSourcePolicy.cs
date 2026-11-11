namespace QuickClip.Recording;

internal enum CaptureSourceMode { GameWindowOnly, SelectedMonitor, Automatic }

internal readonly record struct CaptureSourceDecision(CaptureEngine.MonitorBackend Backend, string? Error)
{
    internal bool Allowed => Error == null;
}

internal static class CaptureSourcePolicy
{
    internal static bool IsEligibleGame(GameCaptureObservation observation, Func<WindowOwner, bool> current,
        Func<string, bool, Discovery.CatalogMatch> match)
    {
        return RefreshEligibleGame(observation, current, match) != null;
    }

    internal static GameCaptureObservation? RefreshEligibleGame(GameCaptureObservation observation,
        Func<WindowOwner, bool> current, Func<string, bool, Discovery.CatalogMatch> match)
    {
        if (observation.VerifiedTarget is not { } target || target.Owner != observation.Owner ||
            observation.Match.Entry == null) return null;

        // Revalidate the retained row even when it is the foreground. The selector's
        // foreground fast path assumes the producer already observed the latest match.
        var refreshed = GameDetector.SelectVerifiedObservation(observation, null, current,
            (path, _) => match(path, observation.IsForeground));
        return refreshed == null ? null : refreshed with { IsForeground = observation.IsForeground };
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

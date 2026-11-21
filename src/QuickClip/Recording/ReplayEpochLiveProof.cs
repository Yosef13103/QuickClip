using QuickClip.Discovery;

namespace QuickClip.Recording;

/// <summary>
/// Generates an epoch-continuity proof from freshly revalidated game ownership,
/// executable/catalog identity and physical display permissions. This is a dormant
/// integration seam; production capture and the native replay ring do not use it yet.
/// </summary>
internal static class ReplayEpochLiveProof
{
    internal static ReplayEpochGameProof? TryCreate(
        AppSettings settings,
        GameCaptureObservation observed,
        IReadOnlyList<MonitorInfo> displays,
        Func<WindowOwner, bool> isCurrentOwner,
        Func<string, bool, CatalogMatch> catalogMatch,
        Func<WindowOwner, string?> windowDisplay,
        long monotonicUs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(isCurrentOwner);
        ArgumentNullException.ThrowIfNull(catalogMatch);
        ArgumentNullException.ThrowIfNull(windowDisplay);

        int permissionRevision = settings.CapturePermissionRevision;
        int disableRevision = settings.RecordingDisableRevision;
        if (!settings.RecordingEnabled || monotonicUs < 0) return null;

        // The retained observation may be stale even if the window title still matches.
        var verified = CaptureSourcePolicy.RefreshEligibleGame(observed, isCurrentOwner, catalogMatch);
        if (verified == null || verified.VerifiedTarget?.Owner != verified.Owner ||
            verified.Match.Entry?.Id is not { Length: > 0 } gameId ||
            string.IsNullOrWhiteSpace(verified.Executable))
            return null;

        var display = CaptureSourcePolicy.ResolveGameDisplay(verified, displays, windowDisplay);
        if (display?.CaptureIdentity is not { Length: > 0 } physicalIdentity ||
            !CaptureSourcePolicy.AllowsRecordingDisplay(settings, physicalIdentity))
            return null;

        // The owner or grant might be revoked while Windows resolves the display.
        // Never manufacture an authorization proof from two different grant generations.
        if (permissionRevision != settings.CapturePermissionRevision ||
            disableRevision != settings.RecordingDisableRevision ||
            !settings.RecordingEnabled || !isCurrentOwner(verified.Owner))
            return null;

        return new ReplayEpochGameProof(gameId, verified.Executable, verified.Owner,
            physicalIdentity, disableRevision, permissionRevision, monotonicUs,
            true, true, true);
    }
}

namespace QuickClip.Recording;

/// <summary>
/// A conservative comparison of two independently authorized *game* capture observations.
/// These values are supplied only after live HWND/PID/process-start, catalog and physical
/// display permission checks. A self-asserted label is not proof of authorization.
/// </summary>
internal sealed record ReplayEpochGameProof(
    string GameId,
    string VerifiedExecutable,
    WindowOwner Owner,
    string PhysicalDisplayIdentity,
    int DisableRevision,
    long PermissionRevision,
    long MonotonicUs,
    bool RecordingEnabled,
    bool GameOwnerVerified,
    bool DisplayPermissionVerified);

internal enum ReplayEpochContinuityDecision
{
    Continue,
    StartNewTimeline,
    RevokeAndDiscard
}

internal static class ReplayEpochContinuity
{
    internal static ReplayEpochContinuityDecision Decide(
        ReplayEpochGameProof old, ReplayEpochGameProof current,
        long maximumGapUs, bool allowVerifiedProcessRestart = false, long maxProcessRestartGapUs = 10_000_000)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(current);
        if (!IsValidProof(old) || !IsValidProof(current) ||
            old.DisableRevision != current.DisableRevision ||
            old.PermissionRevision != current.PermissionRevision)
            return ReplayEpochContinuityDecision.RevokeAndDiscard;

        if (current.MonotonicUs < old.MonotonicUs || maximumGapUs <= 0 ||
            current.MonotonicUs - old.MonotonicUs > maximumGapUs)
            return ReplayEpochContinuityDecision.StartNewTimeline;

        if (!StringComparer.Ordinal.Equals(old.GameId, current.GameId) ||
            !StringComparer.OrdinalIgnoreCase.Equals(old.VerifiedExecutable, current.VerifiedExecutable))
            return ReplayEpochContinuityDecision.StartNewTimeline;

        // Window recreation or moving the same verified process to another authorized
        // display is legitimate. A process-start timestamp avoids accidental PID reuse.
        if (old.Owner.ProcessId == current.Owner.ProcessId &&
            old.Owner.Started == current.Owner.Started)
            return ReplayEpochContinuityDecision.Continue;

        // A new process may be the same catalogued game after a crash, but only when
        // an explicitly verified reacquisition is requested within a short outage.
        if (allowVerifiedProcessRestart && maxProcessRestartGapUs > 0 &&
            current.MonotonicUs - old.MonotonicUs <= maxProcessRestartGapUs)
            return ReplayEpochContinuityDecision.Continue;

        return ReplayEpochContinuityDecision.StartNewTimeline;
    }

    internal static bool IsValidProof(ReplayEpochGameProof proof) =>
        proof.RecordingEnabled && proof.GameOwnerVerified && proof.DisplayPermissionVerified &&
        !string.IsNullOrWhiteSpace(proof.GameId) &&
        !string.IsNullOrWhiteSpace(proof.VerifiedExecutable) &&
        !string.IsNullOrWhiteSpace(proof.PhysicalDisplayIdentity) &&
        proof.Owner.Handle != IntPtr.Zero && proof.Owner.ProcessId != 0 &&
        proof.Owner.ThreadId != 0 && proof.Owner.Started > 0 &&
        proof.DisableRevision >= 0 && proof.PermissionRevision >= 0 && proof.MonotonicUs >= 0;
}

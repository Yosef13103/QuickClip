using QuickClip.Recording;

namespace QuickClip.Discovery;

// Research-only decision boundary. The future Windows provider must establish
// these facts from trusted handles and verified publisher evidence, not settings,
// filenames, process titles, or caller-supplied metadata.
internal enum CompanionActivity { Unknown, Inactive, Active }
internal enum CompanionActivation { Manual, Focused, Running, InCall }

internal sealed record CompanionRegistration(Guid Id, VersionedCompanionLayout Layout,
    bool CaptureAllowed, CompanionActivation Activation);

internal sealed record CompanionWindowEvidence(Guid RegistrationId, string Executable,
    WindowOwner Owner, bool OrdinaryVerifiedImage, bool TrustedPublisher,
    bool EligibleWindow, bool Foreground, CompanionActivity CallActivity,
    bool ExplicitWindowGrant);

internal static class CompanionEligibility
{
    // This is a pure policy prototype, not an OS identity verifier, and never
    // selects a monitor, starts capture or falls back to a different window.
    // isCurrent must check the original process lifetime and window ownership.
    internal static bool CanSelect(CompanionRegistration registration, CompanionWindowEvidence evidence,
        bool masterArmed, bool eligibleGame, bool manualRequest, Func<WindowOwner, bool> isCurrent)
    {
        if (!masterArmed || eligibleGame || !registration.CaptureAllowed ||
            registration.Id == Guid.Empty || registration.Id != evidence.RegistrationId ||
            !evidence.OrdinaryVerifiedImage || !evidence.TrustedPublisher || !evidence.EligibleWindow ||
            !evidence.ExplicitWindowGrant || evidence.Owner.Handle == IntPtr.Zero ||
            evidence.Owner.ProcessId == 0 || evidence.Owner.ThreadId == 0 || evidence.Owner.Started <= 0 ||
            registration.Layout.Match(evidence.Executable) == null)
            return false;

        bool active = registration.Activation switch
        {
            CompanionActivation.Manual => manualRequest,
            CompanionActivation.Focused => evidence.Foreground,
            CompanionActivation.Running => true,
            CompanionActivation.InCall => evidence.CallActivity == CompanionActivity.Active,
            _ => false,
        };
        return active && isCurrent(evidence.Owner);
    }
}

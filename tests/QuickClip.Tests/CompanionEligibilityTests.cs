using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class CompanionEligibilityTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static CompanionRegistration Registration(CompanionActivation mode) =>
        new(Id, VersionedCompanionLayout.Create(@"C:\Apps\Discord", "Discord.exe")!, true, mode);
    private static CompanionWindowEvidence Evidence =>
        new(Id, @"C:\Apps\Discord\app-1.0.9268\Discord.exe", new(new IntPtr(5), 6, 7, 8),
            true, true, true, false, CompanionActivity.Unknown, true);

    [Fact]
    public void Unknown_activity_never_counts_as_a_call_and_presence_is_not_an_implicit_grant()
    {
        var e = Evidence;
        Assert.False(CompanionEligibility.CanSelect(Registration(CompanionActivation.InCall), e, true, false, false, _ => true));
        Assert.False(CompanionEligibility.CanSelect(Registration(CompanionActivation.InCall),
            e with { CallActivity = CompanionActivity.Inactive }, true, false, false, _ => true));
        Assert.True(CompanionEligibility.CanSelect(Registration(CompanionActivation.InCall),
            e with { CallActivity = CompanionActivity.Active }, true, false, false, _ => true));
        Assert.False(CompanionEligibility.CanSelect(Registration(CompanionActivation.Manual), e, true, false, false, _ => true));
        Assert.True(CompanionEligibility.CanSelect(Registration(CompanionActivation.Manual), e, true, false, true, _ => true));
        Assert.False(CompanionEligibility.CanSelect(Registration(CompanionActivation.Focused), e, true, false, false, _ => true));
        Assert.True(CompanionEligibility.CanSelect(Registration(CompanionActivation.Focused),
            e with { Foreground = true }, true, false, false, _ => true));
    }

    [Fact]
    public void Registration_window_identity_and_game_priority_are_independent_required_gates()
    {
        var r = Registration(CompanionActivation.Running);
        var e = Evidence;
        bool Select(CompanionWindowEvidence evidence) =>
            CompanionEligibility.CanSelect(r, evidence, true, false, false, _ => true);
        Assert.True(Select(e));
        Assert.False(Select(e with { ExplicitWindowGrant = false }));
        Assert.False(Select(e with { TrustedPublisher = false }));
        Assert.False(Select(e with { OrdinaryVerifiedImage = false }));
        Assert.False(Select(e with { EligibleWindow = false }));
        Assert.False(Select(e with { RegistrationId = Guid.NewGuid() }));
        Assert.False(Select(e with { Executable = @"C:\Apps\Discord\Update.exe" }));
        Assert.False(Select(e with { Owner = e.Owner with { Started = 0 } }));
        Assert.False(CompanionEligibility.CanSelect(r with { CaptureAllowed = false }, e, true, false, false, _ => true));
        Assert.False(CompanionEligibility.CanSelect(r, e, false, false, false, _ => true));
        Assert.False(CompanionEligibility.CanSelect(r, e, true, true, false, _ => true));
        Assert.False(CompanionEligibility.CanSelect(r, e, true, false, false, owner => owner.Started != e.Owner.Started));
    }
}

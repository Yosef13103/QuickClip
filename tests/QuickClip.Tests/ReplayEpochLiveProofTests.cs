using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochLiveProofTests
{
    private static readonly MonitorInfo Display1 =
        new("DISPLAY1", "First", 0, 0, 3440, 1440) { CaptureIdentity = "A" };
    private static readonly MonitorInfo Display2 =
        new("DISPLAY2", "Second", 3440, 0, 1920, 1080) { CaptureIdentity = "B" };

    private static GameCaptureObservation Observation()
    {
        var owner = new WindowOwner(new IntPtr(123), 12, 345, 6789);
        const string exe = @"C:\Games\Portal 2\portal2.exe";
        var entry = new CatalogEntry("manual:portal2", "Portal 2", CatalogSource.Manual, [], [exe], new());
        return new(owner, exe, new(CatalogMatchKind.Game, entry),
            new(owner, "Portal 2", true), true);
    }

    private static AppSettings Settings() => new()
    {
        RecordingEnabled = true,
        AllowRecordingDisplaysByDefault = false,
        AllowedRecordingDisplays = ["A", "B"],
    };

    private static ReplayEpochGameProof? Try(GameCaptureObservation observed, AppSettings settings,
        Func<WindowOwner, bool>? current = null, Func<string, bool, CatalogMatch>? match = null,
        Func<WindowOwner, string?>? display = null, IReadOnlyList<MonitorInfo>? monitors = null)
        => ReplayEpochLiveProof.TryCreate(settings, observed, monitors ?? [Display1, Display2],
            current ?? (owner => owner == observed.Owner), match ?? ((_, _) => observed.Match),
            display ?? (_ => Display1.DeviceName), 12_000_000);

    [Fact]
    public void Current_verified_game_has_exact_process_catalog_and_physical_display_evidence()
    {
        var o = Observation();
        var s = Settings();
        var proof = Try(o, s);
        Assert.NotNull(proof);
        Assert.Equal(o.Match.Entry!.Id, proof.GameId);
        Assert.Equal(o.Executable, proof.VerifiedExecutable);
        Assert.Equal(o.Owner, proof.Owner);
        Assert.Equal("A", proof.PhysicalDisplayIdentity);
        Assert.Equal(s.CapturePermissionRevision, proof.PermissionRevision);
        Assert.Equal(s.RecordingDisableRevision, proof.DisableRevision);
    }

    [Fact]
    public void Same_verified_process_may_move_between_separately_allowed_displays()
    {
        var o = Observation();
        var s = Settings();
        var a = Try(o, s)!;
        var b = Try(o, s, display: _ => Display2.DeviceName)!;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("B", b.PhysicalDisplayIdentity);
        Assert.Equal(ReplayEpochContinuityDecision.Continue,
            ReplayEpochContinuity.Decide(a, b, 10_000_000));
    }

    [Fact]
    public void Blocked_disconnected_or_unverifiable_display_cannot_authorize_archive_continuity()
    {
        var o = Observation();
        var s = Settings();
        CaptureSourcePolicy.SetDisplayPermission(s, "A", false);
        Assert.Null(Try(o, s));
        Assert.Null(Try(o, Settings(), display: _ => "NOT-AN-ACTUAL-MONITOR"));
        Assert.Null(Try(o, Settings(), monitors: [Display2]));
    }

    [Fact]
    public void A_revoked_or_changed_game_process_or_catalog_is_not_a_verified_source()
    {
        var o = Observation();
        Assert.Null(Try(o, Settings(), current: _ => false));
        Assert.Null(Try(o, Settings(), match: (_, _) => new(CatalogMatchKind.Launcher)));
        Assert.Null(Try(o, Settings(), match: (_, _) => new(CatalogMatchKind.Application)));
    }

    [Fact]
    public void Revocation_and_restore_while_the_proof_is_being_built_invalidates_it()
    {
        var o = Observation();
        var s = Settings();
        var startedAt = s.CapturePermissionRevision;
        var p = Try(o, s, match: (_, _) =>
        {
            CaptureSourcePolicy.SetDisplayPermission(s, "A", false);
            CaptureSourcePolicy.SetDisplayPermission(s, "A", true);
            return o.Match;
        });
        Assert.Null(p);
        Assert.True(s.CapturePermissionRevision > startedAt);
        Assert.True(CaptureSourcePolicy.AllowsRecordingDisplay(s, "A"));
    }
}

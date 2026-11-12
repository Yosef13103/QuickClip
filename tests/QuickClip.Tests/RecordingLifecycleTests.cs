using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class RecordingLifecycleTests
{
    [Fact]
    public void Status_describes_verified_game_and_entire_display_scope_without_claiming_window_isolation()
    {
        string display = @"\\.\DISPLAY2";
        Assert.Equal("Game: Terraria · Entire display Display 2",
            Recorder.CaptureStatusLabel(RecordingMode.GameTriggered, "Terraria", display, "Desktop"));
        Assert.Equal("Desktop recording · Display 2",
            Recorder.CaptureStatusLabel(RecordingMode.ManualDesktop, null, display, "Desktop"));
        Assert.Equal("Desktop",
            Recorder.CaptureStatusLabel(RecordingMode.GameTriggered, null, display, "Desktop"));
    }

    private static GameCaptureObservation Game(int id)
    {
        var owner = new WindowOwner((IntPtr)id, 10, (uint)id, 42);
        var exe = $"Game{id}.exe";
        var entry = new CatalogEntry($"test:{id}", exe, CatalogSource.Manual, [], [exe], new());
        return new(owner, exe, new(CatalogMatchKind.Game, entry), new(owner, exe, true), true);
    }

    private sealed class Backend
    {
        public CaptureActivation? Active;
        public List<CaptureActivation> Starts { get; } = [];
        public string? StartError;
        public int Stops;

        public string? Start(CaptureActivation activation)
        {
            Assert.Null(Active);
            Starts.Add(activation);
            if (StartError == null) Active = activation;
            return StartError;
        }

        public void Stop()
        {
            Assert.NotNull(Active);
            Active = null;
            Stops++;
        }
    }

    [Fact]
    public void Arming_does_not_start_until_an_eligible_game_and_consent_exist()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        var game = Game(1);
        lifecycle.Reconcile(false, true, "A", "B", [game], _ => throw new InvalidOperationException(), "Consent required");
        Assert.Equal(RecordingMode.Off, lifecycle.Mode);
        lifecycle.Reconcile(true, true, "A", null, [], _ => throw new InvalidOperationException(), "Consent required");
        Assert.Equal(RecordingMode.Armed, lifecycle.Mode);
        lifecycle.Reconcile(true, true, null, null, [game], _ => throw new InvalidOperationException(), "Consent required");
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        Assert.Empty(backend.Starts);
        lifecycle.Reconcile(true, true, "A", null, [game], name => name == "A", "Consent required");
        Assert.Equal(RecordingMode.GameTriggered, lifecycle.Mode);
        Assert.Equal("A", backend.Active?.Display);
        lifecycle.Reconcile(true, true, "A", null, [], _ => true, "");
        Assert.Equal(RecordingMode.Armed, lifecycle.Mode);
        Assert.Equal(1, backend.Stops);
    }

    [Fact]
    public void Sticky_game_yields_only_when_lost_and_manual_desktop_is_independent()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        var first = Game(1);
        var second = Game(2);
        void Poll(string? manual, params GameCaptureObservation[] observed) =>
            lifecycle.Reconcile(true, true, "Game display", manual, observed, _ => true, "");
        Poll(null, first);
        Poll(null, second, first with { IsForeground = false });
        Assert.Single(backend.Starts);
        Assert.Equal(first.Owner, backend.Active?.GameOwner);
        Poll(null, second);
        Assert.Equal(second.Owner, backend.Active?.GameOwner);
        Poll("Desktop display", second);
        Assert.Equal(RecordingMode.ManualDesktop, lifecycle.Mode);
        Assert.Null(backend.Active?.GameOwner);
        Poll("Desktop display");
        Assert.Equal("Desktop display", backend.Active?.Display);
        Poll(null);
        Assert.Equal(RecordingMode.Armed, lifecycle.Mode);
        Assert.Equal(3, backend.Starts.Count);
        Assert.Equal(3, backend.Stops);
    }

    [Fact]
    public void Missing_display_or_source_failure_never_starts_another_graph()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        void Poll(bool connected) => lifecycle.Reconcile(true, true, "A", null, [Game(1)], _ => connected, "");
        Poll(true);
        Poll(false);
        Assert.Null(backend.Active);
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        Poll(true);
        lifecycle.FailActive("Source stopped");
        Poll(true);
        Assert.Equal("Source stopped", lifecycle.Error);
        Assert.Equal(2, backend.Starts.Count);
        lifecycle.Reset();
        backend.StartError = "Start failed";
        Poll(true);
        Poll(true);
        Assert.Equal("Start failed", lifecycle.Error);
        Assert.Equal(3, backend.Starts.Count);
    }

    [Fact]
    public void Revalidation_does_not_promote_a_folder_candidate_and_refreshes_a_verified_name()
    {
        var game = Game(1);
        var folder = game.Match.Entry! with { Source = CatalogSource.Folder };
        Assert.Null(CaptureSourcePolicy.RefreshEligibleGame(game, _ => true,
            (_, _) => new(CatalogMatchKind.Candidate, folder)));

        var renamed = folder with { Rules = new(DisplayName: "Renamed") };
        var current = CaptureSourcePolicy.RefreshEligibleGame(game with { IsForeground = false }, _ => true,
            (_, foreground) =>
            {
                Assert.False(foreground);
                return new(CatalogMatchKind.Game, renamed);
            });
        Assert.Equal(game.Owner, current?.VerifiedTarget?.Owner);
        Assert.Equal("Renamed", current?.VerifiedTarget?.Game);
    }

    [Theory]
    [InlineData((int)CatalogMatchKind.Ignored)]
    [InlineData((int)CatalogMatchKind.Inactive)]
    [InlineData((int)CatalogMatchKind.Companion)]
    [InlineData((int)CatalogMatchKind.Launcher)]
    [InlineData((int)CatalogMatchKind.Unknown)]
    public void Disabled_unowned_and_companion_windows_cannot_activate(int matchKind)
    {
        var game = Game(1);
        Assert.True(CaptureSourcePolicy.IsEligibleGame(game with { IsForeground = false }, _ => true, (_, _) => game.Match));
        Assert.False(CaptureSourcePolicy.IsEligibleGame(game, _ => false, (_, _) => game.Match));
        Assert.False(CaptureSourcePolicy.IsEligibleGame(game, _ => true, (_, _) => game.Match with { Kind = (CatalogMatchKind)matchKind }));
    }
}

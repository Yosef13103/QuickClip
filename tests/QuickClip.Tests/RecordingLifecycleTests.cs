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
        public int Frames;

        public string? Start(CaptureActivation activation)
        {
            Assert.Null(Active);
            Assert.Equal(0, Frames);
            Starts.Add(activation);
            if (StartError == null) Active = activation;
            return StartError;
        }

        public void Stop()
        {
            Assert.NotNull(Active);
            Active = null;
            Frames = 0;
            Stops++;
        }
    }

    private static readonly MonitorInfo Display1 = new("DISPLAY1", "", 0, 0, 3440, 1440) { CaptureIdentity = "A" };
    private static readonly MonitorInfo Display2 = new("DISPLAY2", "", -1920, 0, 1920, 1080) { CaptureIdentity = "B" };

    [Theory]
    [InlineData("DISPLAY1", false)]
    [InlineData("DISPLAY2", false)]
    [InlineData("DISPLAY1", true)]
    [InlineData("DISPLAY2", true)]
    public void Catalog_game_starts_on_its_window_display_in_windowed_or_borderless_mode(string device, bool fullscreen)
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        var fixture = Game(1);
        var game = GameDetector.Observe(fixture.Owner, fixture.Executable, fixture.Match, fullscreen, new HashSet<string>());
        lifecycle.Reconcile(true, null, game, [Display1, Display2], owner =>
        {
            Assert.Equal(game.Owner, owner);
            return device;
        }, _ => true);
        Assert.Equal(RecordingMode.GameTriggered, lifecycle.Mode);
        Assert.Equal(device, backend.Active?.Display.Device);
    }

    [Fact]
    public void Game_moves_to_second_display_with_a_fresh_buffer_and_retains_identity_during_Discord_focus()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        var game = Game(1);
        var discord = GameDetector.Observe(Game(2).Owner, @"C:\Apps\Discord.exe",
            new(CatalogMatchKind.Companion), false, new HashSet<string>());
        string device = "DISPLAY1";
        var retained = GameDetector.ResolveEligibleGame(game, discord, _ => true, () => [], (_, _) => game.Match, preferRetained: true);
        void Poll(GameCaptureObservation? observation) =>
            lifecycle.Reconcile(true, null, observation, [Display1, Display2], _ => device, _ => true);
        Poll(game);
        backend.Frames = 600;
        Poll(retained);
        Assert.Equal(600, backend.Frames);
        Assert.Single(backend.Starts);
        device = "DISPLAY2";
        Poll(retained);
        Assert.Equal(1, backend.Stops);
        Assert.Equal(0, backend.Frames);
        Assert.Equal(game.Owner, backend.Active?.GameOwner);
        Assert.Equal("B", backend.Active?.Display.Identity);
        Poll(null);
        Assert.Equal(RecordingMode.Armed, lifecycle.Mode);
        Assert.Null(backend.Active);
    }

    [Fact]
    public void Idle_off_unverified_and_unauthorized_sources_cannot_start_capture()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        var game = Game(1);
        lifecycle.Reconcile(false, "A", game, [], _ => throw new InvalidOperationException(), _ => true);
        Assert.Equal(RecordingMode.Off, lifecycle.Mode);
        lifecycle.Reconcile(true, null, null, [], _ => throw new InvalidOperationException(), _ => true);
        Assert.Equal(RecordingMode.Armed, lifecycle.Mode);
        lifecycle.Reconcile(true, null, game, [Display1, Display2], _ => "DISPLAY2", identity => identity == "A");
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        lifecycle.Reconcile(true, null, game, [Display1], _ => null, _ => true);
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        lifecycle.Reconcile(true, null, game with { Target = null }, [Display1], _ => "DISPLAY1", _ => true);
        Assert.Equal(RecordingMode.Armed, lifecycle.Mode);
        Assert.Empty(backend.Starts);
    }

    [Fact]
    public void Disconnect_with_automatic_Windows_relocation_never_redirects_the_active_game()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        var game = Game(1);
        lifecycle.Reconcile(true, null, game, [Display1, Display2], _ => "DISPLAY2", _ => true);
        lifecycle.Reconcile(true, null, game, [Display1], _ => "DISPLAY1", _ => true);
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        Assert.Null(backend.Active);
        lifecycle.Reconcile(true, null, game, [Display1, Display2 with { CaptureIdentity = "Replacement" }], _ => "DISPLAY1", _ => true);
        Assert.Single(backend.Starts);
        lifecycle.Reconcile(true, null, game, [Display1, Display2], _ => "DISPLAY2", _ => true);
        Assert.Equal("B", backend.Active?.Display.Identity);
    }

    [Fact]
    public void Temporary_manual_capture_restarts_on_the_current_game_display_when_stopped()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        var game = Game(1);
        void Poll(bool enabled, string? manual, string device) =>
            lifecycle.Reconcile(enabled, manual, game, [Display1, Display2], _ => device, _ => true);
        Poll(true, null, "DISPLAY1");
        backend.Frames = 600;
        Poll(true, "A", "DISPLAY1");
        Assert.Equal(RecordingMode.ManualDesktop, lifecycle.Mode);
        Assert.Null(backend.Active?.GameOwner);
        Assert.Equal(0, backend.Frames);
        Poll(true, "A", "DISPLAY2");
        Assert.Equal("A", backend.Active?.Display.Identity);
        Poll(true, null, "DISPLAY2");
        Assert.Equal(RecordingMode.GameTriggered, lifecycle.Mode);
        Assert.Equal("B", backend.Active?.Display.Identity);
        Poll(false, "A", "DISPLAY2");
        Assert.Equal(RecordingMode.Off, lifecycle.Mode);
        Assert.Null(backend.Active);
        Assert.Equal(3, backend.Starts.Count);
        Assert.Equal(3, backend.Stops);
    }

    [Fact]
    public void Missing_manual_display_never_falls_through_to_game()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        lifecycle.Reconcile(true, "B", Game(1), [Display1, Display2], _ => "DISPLAY1", _ => true);
        lifecycle.Reconcile(true, "B", Game(1), [Display1], _ => "DISPLAY1", _ => true);
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        Assert.Null(backend.Active);
        Assert.Single(backend.Starts);
    }

    [Fact]
    public void Native_invalidation_restarts_even_if_game_returns_to_same_display_between_ticks()
    {
        var backend = new Backend();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop);
        void Poll() => lifecycle.Reconcile(true, null, Game(1), [Display1], _ => "DISPLAY1", _ => true);
        Poll();
        backend.Frames = 600;
        lifecycle.InvalidateGraph();
        Poll();
        Assert.Equal(2, backend.Starts.Count);
        Assert.Equal(1, backend.Stops);
        Assert.Equal(0, backend.Frames);
        lifecycle.FailActive("Source stopped");
        Poll();
        Assert.Equal("Source stopped", lifecycle.Error);
        Assert.Equal(2, backend.Starts.Count);
        lifecycle.Reset();
        backend.StartError = "Start failed";
        Poll();
        Poll();
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

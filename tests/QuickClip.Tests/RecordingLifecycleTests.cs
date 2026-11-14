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

    private sealed class RecoveryClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }

    [Fact]
    public void Transient_failure_retries_same_display_with_bounded_backoff_and_checks_consent()
    {
        var backend = new Backend { StartError = "Device temporarily unavailable" };
        var clock = new RecoveryClock();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop, clock);
        bool allowed = true;
        void Poll() => lifecycle.Reconcile(true, null, Game(1), [Display1], _ => "DISPLAY1", _ => allowed);
        Poll();
        Assert.Equal(RecordingMode.Recovering, lifecycle.Mode);
        clock.Advance(1);
        Poll();
        Assert.Single(backend.Starts);
        clock.Advance(1);
        Poll();
        Assert.Equal(2, backend.Starts.Count);
        clock.Advance(3);
        Poll();
        Assert.Equal(2, backend.Starts.Count);
        clock.Advance(1);
        backend.StartError = null;
        Poll();
        Assert.Equal(RecordingMode.GameTriggered, lifecycle.Mode);
        backend.Frames = 100;
        lifecycle.FailActive("Device lost");
        Assert.Equal(0, backend.Frames);
        Assert.Null(backend.Active);
        clock.Advance(30);
        allowed = false;
        Poll();
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        Assert.Equal(3, backend.Starts.Count);
        allowed = true;
        Poll();
        Assert.Equal(RecordingMode.GameTriggered, lifecycle.Mode);
        Assert.Equal("A", backend.Active?.Display.Identity);
    }
    private static readonly MonitorInfo Display2 = new("DISPLAY2", "", -1920, 0, 1920, 1080) { CaptureIdentity = "B" };

    [Fact]
    public void Recovery_caps_retries_resets_after_health_and_stops_while_paused()
    {
        var backend = new Backend { StartError = "Temporary device failure" };
        var clock = new RecoveryClock();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop, clock);
        void Poll(bool enabled = true) =>
            lifecycle.Reconcile(enabled, null, Game(1), [Display1], _ => "DISPLAY1", _ => true);
        Poll();
        foreach (int delay in new[] { 2, 4, 8, 16, 30, 30 })
        {
            int starts = backend.Starts.Count;
            clock.Advance(delay - 1);
            Poll();
            Assert.Equal(starts, backend.Starts.Count);
            clock.Advance(1);
            Poll();
            Assert.Equal(starts + 1, backend.Starts.Count);
        }

        backend.StartError = null;
        clock.Advance(30);
        Poll();
        lifecycle.MarkHealthy();
        lifecycle.FailActive("Display mode changed");
        clock.Advance(2);
        Poll();
        Assert.Equal(RecordingMode.GameTriggered, lifecycle.Mode);
        Assert.Equal("A", backend.Active?.Display.Identity);
        lifecycle.FailActive("Suspended");
        int beforePause = backend.Starts.Count;
        clock.Advance(30);
        Poll(false);
        Assert.Equal(RecordingMode.Off, lifecycle.Mode);
        Assert.Equal(beforePause, backend.Starts.Count);
        Poll();
        Assert.Equal(RecordingMode.GameTriggered, lifecycle.Mode);
        Assert.Equal(beforePause + 1, backend.Starts.Count);
    }

    [Fact]
    public void Permission_denial_never_opens_the_backend_on_later_polls()
    {
        var backend = new Backend();
        var clock = new RecoveryClock();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop, clock);
        for (int i = 0; i < 4; i++)
        {
            lifecycle.Reconcile(true, null, Game(1), [Display1], _ => "DISPLAY1", _ => false);
            Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
            Assert.Contains("permission", lifecycle.Error);
            clock.Advance(60);
        }
        Assert.Empty(backend.Starts);
    }

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
        var retained = GameDetector.ResolveEligibleGame(game, discord, _ => true, () => [], (_, _) => game.Match);
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
    public void Two_running_games_follow_authorized_foreground_in_both_directions()
    {
        var backend = new Backend();
        var clock = new RecoveryClock();
        var lifecycle = new RecordingLifecycle(backend.Start, backend.Stop, clock);
        var handoff = new ForegroundGameHandoff(clock);
        var first = Game(1);
        var second = Game(2);
        var discord = GameDetector.Observe(Game(3).Owner, @"C:\Apps\Discord.exe",
            new(CatalogMatchKind.Companion), false, new HashSet<string>());
        MonitorInfo[] displays = [Display1, Display2];
        bool allowSecondDisplay = false, secondOwnerCurrent = true;
        string DisplayOf(WindowOwner owner) => owner == first.Owner ? "DISPLAY1" : "DISPLAY2";
        bool AllowsDisplay(string identity) => identity == "A" || allowSecondDisplay;
        string? AuthorizedDisplay(GameCaptureObservation game) =>
            CaptureSourcePolicy.ResolveGameDisplay(game, displays, DisplayOf)?.CaptureIdentity is { } identity &&
                AllowsDisplay(identity) ? identity : null;
        CatalogMatch Match(string path, bool foreground) => path == first.Executable ? first.Match : second.Match;
        GameCaptureObservation? observed = null;
        void Poll(GameCaptureObservation foreground, bool advance = true)
        {
            observed = GameDetector.ResolveEligibleGame(observed, foreground, owner => owner != second.Owner || secondOwnerCurrent,
                () => [], Match, (retained, next) => handoff.PreferForeground(retained, next, AuthorizedDisplay));
            lifecycle.Reconcile(true, null, observed, displays, DisplayOf, AllowsDisplay);
            if (advance) clock.Advance(2);
        }

        Poll(first);
        backend.Frames = 600;
        Poll(second);
        Poll(second);
        Assert.Equal(first.Owner, backend.Active?.GameOwner);
        allowSecondDisplay = true;
        Poll(second, advance: false);
        Poll(second, advance: false);
        Assert.Equal(first.Owner, backend.Active?.GameOwner);
        Poll(discord);
        Poll(second);
        secondOwnerCurrent = false;
        Poll(second);
        secondOwnerCurrent = true;
        Assert.Equal(first.Owner, backend.Active?.GameOwner);
        Assert.Equal(600, backend.Frames);
        Poll(second);
        Assert.Equal(first.Owner, backend.Active?.GameOwner);
        Poll(second);
        Assert.Equal(second.Owner, backend.Active?.GameOwner);
        Assert.Equal("B", backend.Active?.Display.Identity);
        Assert.Equal(0, backend.Frames);
        Poll(first);
        Poll(first);
        Assert.Equal(first.Owner, backend.Active?.GameOwner);
        Assert.Equal("A", backend.Active?.Display.Identity);
        Assert.Equal(3, backend.Starts.Count);
        Assert.Equal(2, backend.Stops);
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
        lifecycle.Reconcile(true, null, null, [Display1], _ => null, _ => true);
        Assert.Null(backend.Active);
        lifecycle.Reset(forgetGame: false);
        var relocated = game with
        {
            Owner = game.Owner with { Handle = (IntPtr)99 },
            Target = game.Target! with { Owner = game.Owner with { Handle = (IntPtr)99 } },
        };
        lifecycle.Reconcile(true, null, relocated, [Display1], _ => "DISPLAY1", _ => true);
        Assert.Equal(RecordingMode.Unavailable, lifecycle.Mode);
        Assert.Null(backend.Active);
        lifecycle.Reconcile(true, null, relocated, [Display1, Display2 with { CaptureIdentity = "Replacement" }], _ => "DISPLAY1", _ => true);
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

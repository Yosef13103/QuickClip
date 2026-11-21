using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Win32;
using QuickClip.Discovery;
using QuickClip.Recording.Companion;
using QuickClip.Gallery;
using QuickClip.Media;
using QuickClip.Shell;

namespace QuickClip.Recording;

public sealed record SavedClip(string Path, string Folder, int Seconds);

/// <summary>What the recorder is currently doing, for the UI.</summary>
public sealed record RecorderState(bool Running, string? Error, string Summary, IReadOnlyList<string> ChatApps, IReadOnlyList<string> MusicApps)
{
    public RecordingMode Mode { get; init; }
    internal long? ReadyEpoch { get; init; }
}

internal sealed record ReadyCapture(long Epoch, RecordingMode Mode, string Device, string Summary, long StartedAt, string[] Microphones);

/// <summary>Instant-replay service: keeps the capture engine running per the settings and saves clips on demand.</summary>
internal sealed partial class Recorder
{
    public static Recorder Instance { get; } = new();

    private readonly object _gate = new();
    private readonly ReplaySaveGate _saveGate = new();
    private readonly ReplayMarkers _replayMarkers = new();
    private Guid _replaySession;
    private long _replayGeneration;
    private int _replayBufferSeconds;
    private RecordingVideoOptions? _activeVideoOptions;
    private ImmutableArray<EndpointTrack> _runningEndpointPlan = [];
    private readonly GameDetector _games = new();
    private readonly ForegroundGameHandoff _gameHandoff = new();
    private Timer? _poll;
    private int _pollGeneration;
    private readonly RecordingLifecycle _lifecycle;
    private GameCaptureObservation? _observedGame;
    private string? _manualDisplay;
    private int _manualConsentRevision;
    private long _manualRequestSequence;
    private readonly HashSet<string> _manualAuthorized = new(StringComparer.OrdinalIgnoreCase);
    private string? _sessionGameId;
    private string? _sessionGameExecutable;
    private string? _sessionGame;
    private string? _sessionDisplay;
    private string? _actualMonitorName;
    private string? _captureDevice;
    private string? _captureDisplayIdentity;
    private ReadyCapture? _readyCapture;
    private string[] _captureMicrophones = [];
    private long _captureStartedAt;
    internal bool ManualDesktopRequested => Volatile.Read(ref _manualDisplay) != null
        && AppSettings.Current.AllowsManualDesktop(Volatile.Read(ref _manualConsentRevision));
    private IReadOnlyList<string> _chatApps = [], _musicApps = [];
    private string _summary = "";
    private int? _routingError;
    private string? _lastSaveTiming;

    internal string? LastSaveTiming => AppSettings.Current.RecordingDiagnostics ? Volatile.Read(ref _lastSaveTiming) : null;
    private readonly Dictionary<uint, string> _audioProcesses = [];  // running processes that have had audio
    private string _family = "h264";
    private GameWindowTarget? _captureTarget;
    private string _monitorTarget = "";
    private ReplayDesktopSource? _desktopSource;
    private (int Width, int Height) _captureWindowSize;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }
    public int VideoKbps { get; private set; }
    public int AudioTracks { get; private set; }

    /// <summary>Raised (on a worker thread) when recording starts, stops, fails or chat or music apps change.</summary>
    public event Action<RecorderState>? StateChanged;
    public event Action<SavedClip>? ClipSaved;
    public event Action<ReplaySaveReceipt>? ReplaySaved;
    public event Action<string>? SaveFailed;

    internal RunningEndpointLane[] ReadRunningEndpointAudio()
    {
        lock (_gate)
        {
            return !IsRunning || _runningEndpointPlan.IsEmpty ? [] :
                EndpointAudio.RunningLanes(_runningEndpointPlan, CaptureEngine.ReadEndpointStatus());
        }
    }

    /// <summary>The active source and its actual capture scope, not merely the native DDA label.</summary>
    internal static string CaptureStatusLabel(RecordingMode mode, string? game, string? display, string fallback) =>
        mode switch
        {
            RecordingMode.GameTriggered when !string.IsNullOrWhiteSpace(game) =>
                $"Game: {game} · Entire display {DisplayLabel(display)}",
            RecordingMode.Companion => "Discord: authorized windows only",
            RecordingMode.ManualDesktop => $"Desktop recording · {DisplayLabel(display)}",
            _ => fallback,
        };

    private static string DisplayLabel(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return "(unknown display)";
        const string prefix = @"\\.\DISPLAY";
        return display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "Display " + display[prefix.Length..] : display;
    }

    public RecorderState State => new(IsRunning, Error,
        IsRunning
            ? AppProfile.IsPersonal
                ? CaptureStatusLabel(_discordActive ? RecordingMode.Companion : _lifecycle.Mode,
                    _sessionGame, _sessionDisplay,
                    $"{_summary} · {_captureTarget?.Game ?? _monitorTarget}")
                : $"{_summary} · {_captureTarget?.Game ?? _monitorTarget}"
            : _summary, _chatApps, _musicApps)
    {
        Mode = AppProfile.IsPersonal ? (_discordActive ? RecordingMode.Companion : _lifecycle.Mode) :
            IsRunning ? RecordingMode.ManualDesktop : RecordingMode.Off,
        ReadyEpoch = Volatile.Read(ref _readyCapture)?.Epoch,
    };

    internal ReadyCapture? Feedback => AppSettings.Current.RecordingEnabled ? Volatile.Read(ref _readyCapture) : null;

    private Recorder()
    {
        _lifecycle = new(StartPersonalGraph, StopGraph, canRetry: () => CaptureEngine.IsAvailable);
        // A display change (a resolution change, a game switching modes) or a sleep can leave the screen capture
        // stale, sending black frames or none; start it again. The replay buffer is kept.
        SystemEvents.DisplaySettingsChanged += (_, _) => RestartCapture();
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) RestartCapture();
        };
    }

    private void RestartCapture()
    {
        _ = Task.Run(() =>
        {
            RecorderState before, after;
            lock (_gate)
            {
                before = State;
                Guid session = _replaySession;
                if (AppProfile.IsPersonal && _poll != null)
                {
                    if (IsRunning) CaptureEngine.RestartVideo();
                    ReconcilePersonal();
                }
                else if (IsRunning && _replaySession == session)
                {
                    Log.Event(DiagnosticCategory.Capture, "Restarting screen capture after a system event");
                    CaptureEngine.RestartVideo();
                }
                after = State;
            }
            if (after != before) StateChanged?.Invoke(after);
        });
    }

    /// <summary>(Re)starts recording from the current settings, or stops it if recording is turned off.</summary>
    public Task ApplySettingsAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            bool enabled = AppSettings.Current.RecordingEnabled;
            StopCore(forgetGame: !enabled);
            Error = null;
            if (enabled)
            {
                if (AppProfile.IsPersonal) ReconcilePersonal();
                else StartCore();
                StartPolling();
            }
            else
            {
                _manualDisplay = null;
                _manualAuthorized.Clear();
                _observedGame = null;
            }
        }
        StateChanged?.Invoke(State);
    });

    // A saved profile edit may restart only the current authorized game graph.
    // Re-read its effective settings under the same gate as source transitions.
    internal Task RefreshActiveProfileAsync() => Task.Run(() =>
    {
        RecorderState before, after;
        lock (_gate)
        {
            if (!AppProfile.IsPersonal || !AppSettings.Current.RecordingEnabled ||
                _lifecycle.Mode != RecordingMode.GameTriggered || !IsRunning || _sessionGameId == null) return;
            before = State;
            try
            {
                var settings = AppSettings.Current;
                var next = RecordingVideoOptions.Resolve(settings, RecordingVideoOptions.LoadPolicy(_sessionGameId, settings));
                if (next == _activeVideoOptions) return;
                // Invalidate only the graph, preserving handoff and polling state.
                _lifecycle.InvalidateGraph();
                ReconcilePersonal();
            }
            catch (Exception ex)
            {
                // Do not continue recording silently with a rejected profile.
                _lifecycle.FailActive("The current game's recording profile could not be applied.");
                Error = _lifecycle.Error;
                Log.Essential(DiagnosticCategory.Capture, "Applying game recording profile failed", ex);
            }
            after = State;
        }
        if (after != before) StateChanged?.Invoke(after);
    });

    // Permission changes re-evaluate the active source without unconditionally
    // restarting a still-authorized game or a separately confirmed manual capture.
    internal Task<bool> UpdateDisplayPermissionsAsync(Action<AppSettings> update) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (!AppProfile.IsPersonal || !TryUpdateDisplayPermission(AppSettings.Current, update, settings => settings.TrySave()))
                return false;
            ReconcilePersonal();
            if (AppSettings.Current.RecordingEnabled && _poll == null) StartPolling();
        }
        StateChanged?.Invoke(State);
        return true;
    });

    internal static bool TryUpdateDisplayPermission(AppSettings settings, Action<AppSettings> update, Func<AppSettings, bool> save)
    {
        var previous = (settings.AllowedRecordingDisplays, settings.ExcludedGameDisplays, settings.AllowRecordingDisplaysByDefault);
        update(settings);
        if (save(settings)) return true;
        (settings.AllowedRecordingDisplays, settings.ExcludedGameDisplays, settings.AllowRecordingDisplaysByDefault) = previous;
        return false;
    }

    internal async Task<bool> ChangeRecordingEnabledAsync(bool? enabled = null)
    {
        bool saved = await Task.Run(() =>
        {
            lock (_gate)
            {
                var settings = AppSettings.Current;
                return settings.TrySave(enabled ?? !settings.RecordingEnabled);
            }
        }).ConfigureAwait(false);
        if (saved) await ApplySettingsAsync().ConfigureAwait(false);
        return saved;
    }

    public Task StopAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            _manualDisplay = null;
            _manualAuthorized.Clear();
            _observedGame = null;
            if (AppProfile.IsPersonal && !IsRunning) CaptureEngine.DrainAudioOnShutdown();
            StopCore();
            Error = null;
        }
        StateChanged?.Invoke(State);
    });

    internal Task SetManualDesktopAsync(string? confirmedDisplay, int consentRevision = -1, bool onlyIfActive = false)
    {
        long sequence = Interlocked.Increment(ref _manualRequestSequence);
        return Task.Run(() =>
        {
        lock (_gate)
        {
            if (!AppProfile.IsPersonal || sequence != Volatile.Read(ref _manualRequestSequence) ||
                (onlyIfActive && !ManualDesktopRequested)) return;
            var requested = string.IsNullOrWhiteSpace(confirmedDisplay) ? null : confirmedDisplay;
            if (requested == null)
            {
                if (_manualDisplay == null) return;
                _manualAuthorized.Clear();
            }
            else
            {
                if (!AppSettings.Current.AllowsManualDesktop(consentRevision) ||
                    !CaptureSourcePolicy.AllowsRecordingDisplay(AppSettings.Current, requested)) return;
                if (!ManualDesktopRequested) _manualAuthorized.Clear();
                else if (requested == _manualDisplay) return;
                _manualConsentRevision = consentRevision;
                _manualAuthorized.Add(requested);
            }
            _manualDisplay = requested;
            _gameHandoff.Reset();
            ReconcilePersonal();
        }
        StateChanged?.Invoke(State);
    });

    }

    internal Task<bool> CycleManualDisplayAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (!AppProfile.IsPersonal || !ManualDesktopRequested || _manualDisplay == null) return false;
            var allowed = _manualAuthorized.Where(identity => CaptureSourcePolicy.AllowsRecordingDisplay(AppSettings.Current, identity))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? next = CaptureSourcePolicy.NextAuthorizedDisplay(Monitors.List(), allowed, _manualDisplay);
            if (next == null) return false;
            _manualDisplay = next;
            ReconcilePersonal();
        }
        StateChanged?.Invoke(State);
        return true;
    });

    private void StartPolling()
    {
        int generation = ++_pollGeneration;
        _poll = new Timer(_ => Poll(generation), null, 2000, 2000);
    }

    private void ReconcilePersonal()
    {
        var s = AppSettings.Current;
        IReadOnlyList<MonitorInfo>? displays = null;
        IReadOnlyList<MonitorInfo> Displays() => displays ??= Monitors.List();
        bool AllowsDisplay(string identity) => CaptureSourcePolicy.AllowsRecordingDisplay(s, identity);
        string? AuthorizedDisplay(GameCaptureObservation game) =>
            CaptureSourcePolicy.ResolveGameDisplay(game, Displays(), Monitors.ForWindow)?.CaptureIdentity is { } identity &&
                AllowsDisplay(identity) ? identity : null;

        if (s.RecordingEnabled)
            _observedGame = _games.ObserveEligibleGame(_observedGame, (retained, foreground) =>
                _gameHandoff.PreferForeground(retained, foreground, AuthorizedDisplay));
        else
        {
            _observedGame = null;
            _gameHandoff.Reset();
        }

        // An exclusion revokes the current manual grant and any remembered cycle target.
        _manualAuthorized.RemoveWhere(identity => !CaptureSourcePolicy.AllowsRecordingDisplay(s, identity));
        if (_manualDisplay != null && (!ManualDesktopRequested || !CaptureSourcePolicy.AllowsRecordingDisplay(s, _manualDisplay)))
        {
            _manualDisplay = null;
            _manualAuthorized.Clear();
        }

        bool higherPriority = _manualDisplay != null || _observedGame != null;
        if (_discordActive && (higherPriority || !s.DiscordCaptureEnabled))
            StopGraph();
        if (IsRunning && CaptureEngine.VideoSourceLost())
        {
            Log.Essential(DiagnosticCategory.Capture, "Active capture source was lost");
            if (_discordActive) { ScheduleDiscordRetry(); StopGraph(); }
            else _lifecycle.FailActive("The display capture was interrupted. QuickClip will retry.");
        }
        else if (IsRunning && Environment.TickCount64 - _captureStartedAt >= 15_000 && CaptureEngine.HasVideoOutput())
            _lifecycle.MarkHealthy();
        _lifecycle.Reconcile(s.RecordingEnabled, _manualDisplay, _observedGame,
            s.RecordingEnabled && (_observedGame != null || _manualDisplay != null) ? Displays() : [],
            Monitors.ForWindow, AllowsDisplay);
        Error = _lifecycle.Error;
        ReconcileDiscord(s, higherPriority || _lifecycle.Mode is RecordingMode.Unavailable or RecordingMode.Recovering);
        if (IsRunning && _readyCapture == null && _captureDevice is { } device && CaptureEngine.HasVideoOutput())
            Volatile.Write(ref _readyCapture, new(_replayGeneration,
                _discordActive ? RecordingMode.Companion : _lifecycle.Mode, device, State.Summary,
                _captureStartedAt, _captureMicrophones));

    }

    private static bool Eligible(GameCaptureObservation observation) =>
        CaptureSourcePolicy.IsEligibleGame(observation with { IsForeground = GameDetector.IsForegroundOwner(observation.Owner) },
            GameDetector.IsObservableOwner, GameCatalogRuntime.Match);

    private string? StartPersonalGraph(CaptureActivation activation)
    {
        var game = activation.GameOwner is { } owner && _observedGame?.Owner == owner ? _observedGame : null;
        if (activation.GameOwner != null && (game == null || !Eligible(game)))
            return "The eligible game closed before capture could start.";
        if (activation.GameOwner == null && (!ManualDesktopRequested ||
            !CaptureSourcePolicy.AllowsRecordingDisplay(AppSettings.Current, activation.Display.Identity)))
            return "Manual desktop permission has expired.";
        var s = AppSettings.Current;
        if (game != null && !CaptureSourcePolicy.AllowsRecordingDisplay(s, activation.Display.Identity))
            return "The game's entire display needs permission.";
        StartCore(activation.Display.Device, CaptureEngine.MonitorBackend.MonitorOnlyDuplication,
            activation.Display.Identity, activation.GameOwner, activation.GameId);
        if (!IsRunning) return Error ?? "The selected display could not be recorded.";
        if (!s.RecordingEnabled || !CaptureSourcePolicy.AllowsRecordingDisplay(s, activation.Display.Identity) ||
            (game == null ? !ManualDesktopRequested : !Eligible(game)))
        {
            StopGraph();
            return game == null ? "Manual desktop permission has expired."
                : "The eligible game or recording permission changed while capture was starting.";
        }
        _sessionGame = game?.VerifiedTarget?.Game;
        _sessionGameId = activation.GameId;
        _sessionGameExecutable = game?.Executable;
        _sessionDisplay = _actualMonitorName ?? activation.Display.Device;
        _captureStartedAt = Environment.TickCount64;
        return null;
    }

    private void StartCore(string? display = null, CaptureEngine.MonitorBackend? fixedBackend = null,
        string? displayIdentity = null, WindowOwner? gameOwner = null, string? gameId = null,
        IReadOnlyList<WindowOwner>? companion = null)
    {
        var s = AppSettings.Current;
        Error = null;
        var backend = fixedBackend ?? s.CaptureBackend;
        if (!CaptureEngine.IsAvailable)
        {
            Error = "The recording engine (QuickClipCapture.dll) is missing.";
            return;
        }
        try
        {
            var policy = AppProfile.IsPersonal && gameOwner != null ? RecordingVideoOptions.LoadPolicy(gameId, s) : null;
            var videoOptions = RecordingVideoOptions.Resolve(s, policy);
            CaptureEngine.Initialize();
            var monitors = Monitors.List();
            var monitor = companion == null
                ? CaptureSourcePolicy.SelectMonitor(monitors, display ?? s.RecordMonitor, AppProfile.IsPersonal)
                : null;
            if (companion == null && (monitor == null || (displayIdentity != null &&
                !string.Equals(monitor.CaptureIdentity, displayIdentity, StringComparison.OrdinalIgnoreCase))))
            {
                Error = "The selected monitor is unavailable. Choose a connected display to start recording.";
                return;
            }

            var available = VideoEncoders.GetAvailableAsync().GetAwaiter().GetResult();
            string? encoder = videoOptions.SelectEncoder(available, policy?.EncoderId is { Length: > 0 });
            if (encoder == null)
            {
                Error = policy?.EncoderId is { Length: > 0 } || policy?.Codec is GameRecordingCodec.H264 or GameRecordingCodec.Hevc or GameRecordingCodec.Av1
                    ? "The game profile encoder is unavailable. Choose another encoder or reset its override."
                    : "No graphics card video encoder works on this PC (NVIDIA, AMD or Intel). Updating the graphics driver may help.";
                return;
            }

            // The aspect ratio is for the monitor: a game recorded by its window is taken as it is.
            string aspect = videoOptions.Aspect;
            bool cloakedMirror = companion is { Count: 1 } && s.DiscordCloakedMirror &&
                Environment.OSVersion.Version.Build < 20348;
            int sourceWidth = companion == null ? monitor!.Width : 1920;
            int sourceHeight = companion == null ? monitor!.Height : 1080;
            if (cloakedMirror && !DiscordMainWindow.TrySize(companion![0], out sourceWidth, out sourceHeight))
            {
                Error = "Main Discord window dimensions are unavailable.";
                return;
            }
            if (companion != null) aspect = "";
            var area = RecordingPresets.Area(sourceWidth, sourceHeight, aspect);
            bool cropped = aspect != "" && (area.Width != sourceWidth || area.Height != sourceHeight);
            var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, videoOptions.Resolution);
            if (w < 16 || h < 16) { Error = "The configured capture area is too small for this display."; return; }
            bool scaled = w != area.Width || h != area.Height;
            int captureFps = companion == null ? videoOptions.Fps : Math.Min(60, videoOptions.Fps);
            string family = VideoEncoders.Find(encoder)?.Family ?? "h264";
            VideoKbps = RecordingPresets.VideoKbps(videoOptions.Quality, w, h, captureFps, family, videoOptions.TargetKbps);
            var endpoints = EndpointAudio.Plan(s);
            var mics = endpoints == null && s.RecordMic ? Microphones.Plan(s.MicDeviceIds, CaptureEngine.EnumMicrophones()) : [];
            bool separateMics = s.SeparateMicTracks && mics.Count > 1;
            bool splitChat = RecordingPresets.SplitChat(s), splitMusic = RecordingPresets.SplitMusic(s);
            AudioTracks = endpoints?.Count ?? (1 + (splitChat ? 1 : 0) + (splitMusic ? 1 : 0) + (separateMics ? mics.Count : Math.Min(mics.Count, 1)));

            Error = CaptureEngine.Start(monitor?.DeviceName ?? "", aspect, captureFps,
                companion != null || scaled ? w : 0, companion != null || scaled ? h : 0, encoder, VideoKbps,
                RecordingPresets.AudioKbps, videoOptions.ReplaySeconds, s.CaptureCursor, splitChat, splitMusic, mics, separateMics, endpoints,
                new() { Backend = cloakedMirror ? CaptureEngine.MonitorBackend.DiscordDwmMirror : backend,
                    Preset = videoOptions.NvencPreset, Diagnostics = s.RecordingDiagnostics ? 1 : 0 },
                displayIdentity, gameOwner, companion);
            if (Error != null)
            {
                Log.Essential(DiagnosticCategory.Capture, "Capture engine failed to start");
                return;
            }
            if (mics.Count > 0 || endpoints?.Count > 0)
                Log.Event(DiagnosticCategory.Audio, "Capture audio tracks configured");

            _runningEndpointPlan = endpoints?.ToImmutableArray() ?? [];
            _replaySession = Guid.NewGuid();
            Interlocked.Increment(ref _replayGeneration);
            IsRunning = true;
            _replayBufferSeconds = videoOptions.ReplaySeconds;
            _activeVideoOptions = videoOptions;
            _family = family;
            _captureTarget = null;
            _desktopSource = companion == null ? backend switch
            {
                CaptureEngine.MonitorBackend.MonitorOnlyDuplication => new(monitor!.DeviceName, ReplayCaptureBackend.DesktopDuplication),
                CaptureEngine.MonitorBackend.MonitorOnlyGraphics => new(monitor!.DeviceName, ReplayCaptureBackend.WindowsGraphicsCapture),
                _ => null,
            } : null;
            _captureDevice = monitor?.DeviceName ?? "Discord";
            _captureDisplayIdentity = displayIdentity;
            _captureMicrophones = QuickClip.Shell.MicrophoneIndicatorSignal.SelectedMicrophones(s);
            _actualMonitorName = companion != null ? "Discord" :
                string.IsNullOrWhiteSpace(monitor!.FriendlyName) ? monitor.DeviceName : monitor.FriendlyName;
            _monitorTarget = cloakedMirror ? "Discord - main window (experimental DWM mirror)" :
                companion != null ? "Discord - authorized windows" : _desktopSource?.Backend switch
            {
                ReplayCaptureBackend.DesktopDuplication => $"Desktop · {monitor!.Label} · Desktop Duplication",
                ReplayCaptureBackend.WindowsGraphicsCapture => $"Desktop · {monitor!.Label} · Windows Graphics Capture (capture border may appear)",
                _ => monitor!.Label,
            };
            _captureWindowSize = default;
            if (endpoints != null) (_chatApps, _musicApps) = ([], []);
            lock (_audioProcesses) _audioProcesses.Clear();
            _summary = $"{h}p{captureFps}{(cropped ? $" {aspect}" : "")} · {family.ToUpperInvariant()} · {RecordingPresets.Label(videoOptions.Quality)}" +
                       (endpoints == null ? "" : endpoints.Count == 0 ? " · video only" : $" · {endpoints.Count} endpoint audio tracks");
            Log.Event(DiagnosticCategory.Capture, "Recorder started");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Essential(DiagnosticCategory.Capture, "Starting the recorder failed", ex);
        }
    }

    private void StopCore(bool forgetGame = true)
    {
        Volatile.Write(ref _lastSaveTiming, null);
        ++_pollGeneration;
        _poll?.Dispose();
        _poll = null;
        _gameHandoff.Reset();
        ResetDiscordObservation(stop: true);
        _discordManualRequested = false;
        if (AppProfile.IsPersonal) _lifecycle.Reset(forgetGame);
        else StopGraph();
    }

    private void StopGraph()
    {
        Volatile.Write(ref _readyCapture, null);
        Interlocked.Increment(ref _replayGeneration);
        bool stopNative = IsRunning;
        IsRunning = false;
        _discordActive = false;
        if (stopNative) CaptureEngine.Stop();
        _runningEndpointPlan = [];
        _replaySession = Guid.Empty;
        _activeVideoOptions = null;
        _replayMarkers.Clear();
        (_sessionGame, _sessionGameId, _sessionDisplay, _desktopSource) = (null, null, null, null);
        _sessionGameExecutable = null;
        _actualMonitorName = null;
        _captureDevice = null;
        _captureDisplayIdentity = null;
        _captureMicrophones = [];
        (_chatApps, _musicApps) = ([], []);
        _summary = "";
    }

    private void Poll(int generation)
    {
        if (!Monitor.TryEnter(_gate)) return;
        var before = State;
        try
        {
            if (generation != _pollGeneration) return;
            var s = AppSettings.Current;
            if (AppProfile.IsPersonal) ReconcilePersonal();
            if (!IsRunning) return;
            if (!AppProfile.IsPersonal)
            {
                _games.Poll();
                if (s.PreferGameWindow) UpdateCaptureWindow(s);
            }
            if (s.UseAudioEndpoints) return;

            var processes = ProcessScan.Snapshot();
            var chat = ChatApps.Find(processes);
            var music = MusicApps.Find(processes);
            List<uint> chatPids = RecordingPresets.SplitChat(s) ? [.. chat.Select(c => c.Pid)] : [];
            List<uint> musicPids = RecordingPresets.SplitMusic(s) ? [.. music.Select(m => m.Pid)] : [];
            var plan = PlanAudio(processes, chatPids, musicPids);
            CaptureEngine.SetAudioProcesses(plan);
            var chatNames = chat.Select(c => c.Name).Distinct().ToList();
            var musicNames = music.Select(m => m.Name).Distinct().ToList();
            if (!chatNames.SequenceEqual(_chatApps) || !musicNames.SequenceEqual(_musicApps))
            {
                (_chatApps, _musicApps) = (chatNames, musicNames);
            }
        }
        catch (Exception ex)
        {
            Log.Essential(DiagnosticCategory.Capture, "Recorder poll failed", ex);
            if (AppProfile.IsPersonal)
            {
                _lifecycle.FailActive("Capture stopped because the current source could not be verified.");
                Error = _lifecycle.Error;
            }
        }
        finally
        {
            var after = State;
            Monitor.Exit(_gate);
            if (after != before) StateChanged?.Invoke(after);
        }
    }

    /// <summary>
    /// "Prefer game window capture": the game in the foreground is recorded by its window, still when you tab out of
    /// it, until it closes; the monitor otherwise. Only starting or leaving a game switches, so the replay buffer
    /// isn't cut short by tabbing out.
    /// </summary>
    private void UpdateCaptureWindow(AppSettings s)
    {
        var target = GameDetector.SelectTarget(_captureTarget, _games.ForegroundGame, GameDetector.IsCurrent);
        IntPtr window = target?.Owner.Handle ?? IntPtr.Zero;
        var size = window != IntPtr.Zero ? GameDetector.WindowSize(window) : default;
        if (window != IntPtr.Zero && (size.Width < 64 || size.Height < 64)) return; // minimized: carry on as it was
        if (target?.Owner == _captureTarget?.Owner && size == _captureWindowSize) return;
        _captureTarget = target;
        _captureWindowSize = size;
        if (window == IntPtr.Zero)
        {
            CaptureEngine.SetCaptureWindow(IntPtr.Zero, 0, 0, 0);
            Log.Event(DiagnosticCategory.Capture, "Recording the monitor again after the game window closed");
            StateChanged?.Invoke(State);
            return;
        }
        var options = _activeVideoOptions ?? RecordingVideoOptions.Resolve(s, null);
        var (w, h) = RecordingPresets.OutputSize(size.Width & ~7, size.Height & ~1, options.Resolution);
        int kbps = RecordingPresets.VideoKbps(options.Quality, w, h, options.Fps, _family, options.TargetKbps);
        CaptureEngine.SetCaptureWindow(window, w, h, kbps);
        Log.Event(DiagnosticCategory.Capture, "Recorder switched to game window capture");
        StateChanged?.Invoke(State);
    }

    private AudioRouting.Plan PlanAudio(IReadOnlyList<ProcessScan.Entry> processes, List<uint> chat, List<uint> music)
    {
        uint self = (uint)Environment.ProcessId;
        try
        {
            lock (_audioProcesses)
            {
                // Without chat or music apps, Desktop is simply everything else.
                if (chat.Count + music.Count > 0) AudioRouting.Remember(_audioProcesses, processes, CoreAudio.SessionProcessIds());
                else _audioProcesses.Clear();
                var plan = AudioRouting.Make(processes, chat, music, () => _audioProcesses.Keys.ToHashSet(), ProcessScan.ExePath, self);
                _routingError = null;
                return plan;
            }
        }
        catch (Exception ex)
        {
            // Can't list the apps with audio: leave out just the first separated app, as with only one.
            if (ex.HResult != _routingError) Log.Failure(DiagnosticCategory.Audio, "Listing the apps with audio failed", ex);
            _routingError = ex.HResult;
            return new AudioRouting.Plan(chat.Concat(music).DefaultIfEmpty(self).First(), [], chat, music);
        }
    }

    public CaptureEngine.Stats Stats() => IsRunning ? CaptureEngine.GetStats() : default;

    /// <summary>Marks the most recent encoded frame. A marker alone does not keep footage in the replay buffer.</summary>
    public LiveReplayMarker? TryAddReplayMarker(string? label = null)
    {
        lock (_gate)
        {
            if (!IsRunning || (AppProfile.IsPersonal && !AppSettings.Current.RecordingEnabled) ||
                CaptureEngine.GetReplayPosition() is not { } position) return null;
            var game = _discordActive ? "Discord" : _captureTarget?.Game;
            return _replayMarkers.Add(position, _replaySession, game ?? "Desktop", game ?? _monitorTarget, label);
        }
    }

    /// <summary>Saves the buffer to the clips folder, under the game being played.</summary>
    public async Task<SavedClip?> SaveClipAsync() => (await SaveCoreAsync(null, CancellationToken.None))?.Clip;

    /// <summary>Saves the available portion of a requested replay. Cancellation is honored until publication.</summary>
    public async Task<ReplaySaveReceipt?> SaveReplayAsync(int requestedSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedSeconds);
        return (await SaveCoreAsync(requestedSeconds, cancellationToken))?.Receipt;
    }

    private async Task<(SavedClip Clip, ReplaySaveReceipt Receipt)?> SaveCoreAsync(int? requestedSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long? requestedAtTick = AppSettings.Current.RecordingDiagnostics ? Stopwatch.GetTimestamp() : null;
        long generation = Volatile.Read(ref _replayGeneration);
        if (!IsRunning)
        {
            SaveFailed?.Invoke(Error ?? (State.Mode == RecordingMode.Armed
                ? "No recording is active. Start an eligible game or turn on manual desktop capture." : "Recording is turned off."));
            return null;
        }
        return await _saveGate.RunAsync(() => Task.FromResult(SaveOnWorker(requestedSeconds, generation, requestedAtTick, cancellationToken)),
            cancellationToken, () => SaveFailed?.Invoke("A replay save is already in progress.")).ConfigureAwait(false);
    }

    private (SavedClip Clip, ReplaySaveReceipt Receipt)? SaveOnWorker(int? requestedSeconds, long generation,
        long? requestedAtTick, CancellationToken cancellationToken)
    {
        using var activity = QuickClip.Shell.BackgroundActivity.Begin("Saving clip");
        string? staged = null;
        bool complete = false;
        try
        {
            var s = AppSettings.Current;
            int seconds;
            CaptureEngine.ReplayPosition position;
            ReplayMarkers.Candidate[] candidates;
            Guid sessionId;
            int gameLookback;
            ReplayDesktopSource? desktopSource;
            string? gameContext;
            string? gameId;
            Guid? collectionId;
            lock (_gate)
            {
                if (!IsRunning || generation != _replayGeneration ||
                    (AppProfile.IsPersonal && !(_discordActive ? MaySaveDiscord(s) :
                    MaySaveFromDisplay(s, _lifecycle.Mode, _captureDisplayIdentity, ManualDesktopRequested))) ||
                    CaptureEngine.GetReplayPosition() is not { } active)
                    throw new IOException("The recording session has ended.");
                // The active graph, not a mutable global setting, owns its replay length.
                seconds = requestedSeconds ?? _replayBufferSeconds;
                position = active;
                sessionId = _replaySession;
                desktopSource = _desktopSource;
                candidates = _replayMarkers.Snapshot(position, sessionId);
                gameLookback = requestedSeconds.HasValue ? Math.Min(seconds, _replayBufferSeconds) : seconds;
                gameContext = AppProfile.IsPersonal ? _sessionGame : _captureTarget?.Game ?? _games.GameForClip(gameLookback);
                gameId = _sessionGameId;
                collectionId = AppProfile.IsPersonal && _lifecycle.Mode == RecordingMode.GameTriggered
                    ? Library.RecordingCollections.Resolve(s, gameId, _sessionGameExecutable) : null;
            }
            long positionAtTick = requestedAtTick.HasValue ? Stopwatch.GetTimestamp() : 0;
            var anchors = candidates.Select(item => new CaptureEngine.ReplayAnchor { Sequence = item.Sequence, OffsetUs = -1 }).ToArray();
            string folder = gameContext ?? "Desktop";
            string mediaTitle = desktopSource != null
                ? $"{folder} · {ReplaySaveReceipt.DescribeSource(desktopSource, false)}"
                : folder;
            string folderName = ClipNaming.SafeComponent(folder, 64);
            if (ClipLibrary.IsExcluded(folderName, null)) folderName += " (game)";
            string dir = Path.Combine(s.ClipsFolder, folderName);
            if (ClipLibrary.IsMomentsPath(dir)) throw new IOException("Choose a clips folder outside the Moments archive.");
            Directory.CreateDirectory(dir);
            DateTimeOffset requestedAt = DateTimeOffset.Now;
            if (AppProfile.IsPersonal) _ = ClipNaming.Stem(s.ClipFilenameTemplate, folder, requestedAt, null);
            string stagingPath = staged = ClipNaming.ReserveStagingPath(dir, ".mp4");
            CaptureEngine.ReplaySpan span = default;
            CaptureEngine.ReplayVideo video = default;
            cancellationToken.ThrowIfCancellationRequested();
            long nativeStarted = requestedAtTick.HasValue ? Stopwatch.GetTimestamp() : 0;
            string? error = CaptureEngine.SaveReplay(stagingPath, seconds, mediaTitle, position,
                requestedSeconds.HasValue, anchors, out span, out video);
            if (requestedAtTick is long start)
            {
                double nativeMs = Stopwatch.GetElapsedTime(nativeStarted).TotalMilliseconds;
                lock (_gate)
                {
                    if (s.RecordingDiagnostics && generation == _replayGeneration && IsRunning)
                    {
                        double dispatchMs = Stopwatch.GetElapsedTime(start, positionAtTick).TotalMilliseconds;
                        double prepareMs = Stopwatch.GetElapsedTime(positionAtTick, nativeStarted).TotalMilliseconds;
                        Volatile.Write(ref _lastSaveTiming, FormattableString.Invariant(
                            $"Replay save: request to position {dispatchMs:F0} ms, preparation {prepareMs:F0} ms, native snapshot and mux {nativeMs:F0} ms."));
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (error != null)
            {
                Log.Essential(DiagnosticCategory.Capture, "Native replay save failed");
                SaveFailed?.Invoke(error);
                return null;
            }
            DateTimeOffset? capturedAt = Mp4Info.Read(stagingPath).CaptureTime?.ToLocalTime();
            DateTimeOffset capture = capturedAt ?? requestedAt;
            string stem = AppProfile.IsPersonal ? ClipNaming.Stem(s.ClipFilenameTemplate, folder, capture, null)
                : $"{folder} {requestedAt:yyyy-MM-dd HH-mm-ss}";
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            lock (_gate)
            {
                if (AppProfile.IsPersonal && (!IsRunning || _replaySession != sessionId ||
                    !(_discordActive ? MaySaveDiscord(s) :
                        MaySaveFromDisplay(s, _lifecycle.Mode, _captureDisplayIdentity, ManualDesktopRequested)) || CaptureEngine.VideoSourceLost()))
                    throw new IOException("The recording source changed before the replay could be saved.");
                path = ClipNaming.Publish(stagingPath, dir, stem, ".mp4");
            }
            complete = true;
            TimeSpan? actual = span.DurationUs >= 0 ? ReplayMarkers.Time(span.DurationUs) : null;
            var receipt = new ReplaySaveReceipt(path, folder, sessionId, seconds, actual, ReplayMarkers.Time(span.AvailableUs),
                ReplayMarkers.Time(span.FirstUs), ReplayMarkers.Time(span.LastUs), span.FirstSource, span.LastSource,
                capturedAt, ReplayMarkers.Match(candidates, anchors, actual))
            {
                DesktopSource = desktopSource,
                GameContext = gameContext,
                GameContextId = gameId,
                VideoOutput = video.Width > 0 && video.Height > 0
                    ? new(video.Width, video.Height, video.FpsNumerator > 0 && video.FpsDenominator > 0
                        ? (double)video.FpsNumerator / video.FpsDenominator : null)
                    : null,
            };
            if (AppProfile.IsPersonal)
            {
                ClipDetailsStore.TryWrite(path, new ClipDetails(capture, folder)
                {
                    GameId = gameId,
                    RecordedCollectionId = collectionId,
                    RecordingSessionId = sessionId == Guid.Empty ? null : sessionId,
                    Bookmarks = receipt.Markers.Select(mark => new ClipBookmark(mark.Offset.TotalSeconds, mark.Marker.Label ?? "Marker")
                    { SessionId = mark.Marker.SessionId }).ToArray(),
                });
                // The successful save is a marker for later clips, not this clip's own timeline.
                // Anchor it to the original request frame so slow muxing cannot move the marker.
                lock (_gate)
                {
                    if (IsRunning && _replaySession == sessionId && CaptureEngine.GetReplayPosition() is { } active)
                        _replayMarkers.AddSavedClip(position, active, sessionId, folder, mediaTitle);
                }
            }
            int displaySeconds = requestedSeconds.HasValue ? (int)(actual?.TotalSeconds ?? 0) : seconds;
            var clip = new SavedClip(path, folder, displaySeconds);
            Log.Event(DiagnosticCategory.Capture, "Replay saved");
            try { ClipSaved?.Invoke(clip); }
            catch (Exception ex) { Log.Failure(DiagnosticCategory.Capture, "Clip saved notification failed", ex); }
            try { ReplaySaved?.Invoke(receipt); }
            catch (Exception ex) { Log.Failure(DiagnosticCategory.Capture, "Replay receipt notification failed", ex); }
            return (clip, receipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            complete = false;
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Essential(DiagnosticCategory.Capture, "Saving a clip failed", ex);
            SaveFailed?.Invoke(complete && File.Exists(staged) ? $"{ex.Message} The recording is still at {staged}." : ex.Message);
            return null;
        }
        finally
        {
            try { if (!complete && staged != null) File.Delete(staged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool MaySaveFromDisplay(AppSettings settings, RecordingMode mode, string? identity, bool manualGranted) =>
        settings.RecordingEnabled && identity != null && CaptureSourcePolicy.AllowsRecordingDisplay(settings, identity) && (mode switch
        {
            RecordingMode.GameTriggered => true,
            RecordingMode.ManualDesktop => manualGranted,
            _ => false,
        });
}

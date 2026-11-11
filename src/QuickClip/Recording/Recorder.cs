using Microsoft.Win32;
using QuickClip.Discovery;
using QuickClip.Gallery;
using QuickClip.Media;
using QuickClip.Shell;

namespace QuickClip.Recording;

public sealed record SavedClip(string Path, string Folder, int Seconds);

/// <summary>What the recorder is currently doing, for the UI.</summary>
public sealed record RecorderState(bool Running, string? Error, string Summary, IReadOnlyList<string> ChatApps, IReadOnlyList<string> MusicApps)
{
    public RecordingMode Mode { get; init; }
}

/// <summary>Instant-replay service: keeps the capture engine running per the settings and saves clips on demand.</summary>
internal sealed class Recorder
{
    public static Recorder Instance { get; } = new();

    private readonly object _gate = new();
    private readonly ReplaySaveGate _saveGate = new();
    private readonly ReplayMarkers _replayMarkers = new();
    private Guid _replaySession;
    private int _replayBufferSeconds;
    private readonly GameDetector _games = new();
    private Timer? _poll;
    private int _pollGeneration;
    private readonly RecordingLifecycle _lifecycle;
    private readonly Dictionary<WindowOwner, GameCaptureObservation> _observedGames = [];
    private string? _manualDisplay;
    private string? _sessionGameId;
    private string? _sessionGame;
    private string? _sessionDisplay;
    private long _lastFreshVideo;
    internal bool ManualDesktopRequested => Volatile.Read(ref _manualDisplay) != null;
    private IReadOnlyList<string> _chatApps = [], _musicApps = [];
    private string _summary = "";
    private string? _routingError;
    private string? _routing;
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

    public RecorderState State => new(IsRunning, Error,
        IsRunning ? $"{_summary} · {_captureTarget?.Game ?? _monitorTarget}" : _summary, _chatApps, _musicApps)
    {
        Mode = AppProfile.IsPersonal ? _lifecycle.Mode : IsRunning ? RecordingMode.ManualDesktop : RecordingMode.Off,
    };

    private Recorder()
    {
        _lifecycle = new(StartPersonalGraph, StopGraph);
        // A display change (a resolution change, a game switching modes) or a sleep can leave the screen capture
        // stale, sending black frames or none; start it again. The replay buffer is kept.
        SystemEvents.DisplaySettingsChanged += (_, _) => RestartCapture("the display settings changed");
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) RestartCapture("the PC woke from sleep");
        };
    }

    private void RestartCapture(string why)
    {
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                if (AppProfile.IsPersonal)
                {
                    if (_poll != null) ReconcilePersonal();
                    if (!IsRunning) return;
                }
                else if (!IsRunning) return;
                Log.Write($"Starting screen capture again: {why}");
                CaptureEngine.RestartVideo();
            }
        });
    }

    /// <summary>(Re)starts recording from the current settings, or stops it if recording is turned off.</summary>
    public Task ApplySettingsAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            StopCore();
            Error = null;
            if (AppSettings.Current.RecordingEnabled)
            {
                if (AppProfile.IsPersonal) ReconcilePersonal();
                else StartCore();
                StartPolling();
            }
            else
            {
                _manualDisplay = null;
                _observedGames.Clear();
            }
        }
        StateChanged?.Invoke(State);
    });

    public Task StopAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            _manualDisplay = null;
            _observedGames.Clear();
            StopCore();
            Error = null;
        }
        StateChanged?.Invoke(State);
    });

    internal Task SetManualDesktopAsync(string? confirmedDisplay) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (!AppProfile.IsPersonal || !AppSettings.Current.RecordingEnabled) return;
            var requested = string.IsNullOrWhiteSpace(confirmedDisplay) ? null : confirmedDisplay;
            if (requested == _manualDisplay) return;
            _manualDisplay = requested;
            _lifecycle.Reset();
            ReconcilePersonal();
        }
        StateChanged?.Invoke(State);
    });

    private void StartPolling()
    {
        int generation = ++_pollGeneration;
        _poll = new Timer(_ => Poll(generation), null, 2000, 2000);
    }

    private void ReconcilePersonal()
    {
        var s = AppSettings.Current;
        if (s.RecordingEnabled && s.PreferGameWindow)
        {
            _games.Poll();
            foreach (var observation in new[] { _games.ActiveObservation, _games.ForegroundObservation })
                if (observation != null && Eligible(observation))
                    _observedGames[observation.Owner] = observation;
            foreach (var owner in _observedGames.Keys.ToArray())
            {
                var observed = _observedGames[owner] with { IsForeground = owner == _games.ForegroundObservation?.Owner };
                if (Eligible(observed)) _observedGames[owner] = observed;
                else _observedGames.Remove(owner);
            }
        }
        else _observedGames.Clear();

        IReadOnlyList<MonitorInfo>? displays = null;
        _lifecycle.Reconcile(s.RecordingEnabled, s.PreferGameWindow, s.AuthorizedGameDisplay, _manualDisplay,
            _observedGames.Values.OrderByDescending(x => x.Owner == _games.ForegroundObservation?.Owner).ToArray(),
            identity => FindAuthorizedDisplay(displays ??= Monitors.List(), identity) is { } monitor
                && (!IsRunning || string.Equals(_sessionDisplay, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)),
            "No display is authorized for game replay. Use Allow selected display during games in Recording settings.");
        Error = _lifecycle.Error;

        if (!IsRunning) return;
        if (CaptureEngine.GetReplayPosition() is { Sequence: > 0 })
            _lastFreshVideo = Environment.TickCount64;
        else if (Environment.TickCount64 - _lastFreshVideo >= 10_000)
        {
            _lifecycle.FailActive("Capture stopped because the authorized display stopped producing video.");
            Error = _lifecycle.Error;
        }
    }

    private static bool Eligible(GameCaptureObservation observation) =>
        CaptureSourcePolicy.IsEligibleGame(observation, GameDetector.IsCurrent, GameCatalogRuntime.Match);

    private string? StartPersonalGraph(CaptureActivation activation)
    {
        var game = activation.GameOwner is { } owner ? _observedGames.GetValueOrDefault(owner) : null;
        if (activation.GameOwner != null && (game == null || !Eligible(game)))
            return "The eligible game closed before capture could start.";
        var monitor = FindAuthorizedDisplay(Monitors.List(), activation.Display);
        if (monitor == null) return "The authorized display is no longer connected.";
        StartCore(monitor.DeviceName, CaptureEngine.MonitorBackend.MonitorOnlyDuplication, activation.Display);
        if (!IsRunning) return Error ?? "The selected display could not be recorded.";
        if (game != null && !Eligible(game))
        {
            StopGraph();
            return "The eligible game closed while capture was starting.";
        }
        _sessionGame = game?.Target?.Game;
        _sessionGameId = activation.GameId;
        _sessionDisplay = monitor.DeviceName;
        _lastFreshVideo = Environment.TickCount64;
        return null;
    }

    private static MonitorInfo? FindAuthorizedDisplay(IReadOnlyList<MonitorInfo> displays, string identity) =>
        displays.FirstOrDefault(m => string.Equals(m.CaptureIdentity, identity, StringComparison.OrdinalIgnoreCase));

    private void StartCore(string? display = null, CaptureEngine.MonitorBackend? fixedBackend = null, string? displayIdentity = null)
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
            CaptureEngine.Initialize();
            var monitors = Monitors.List();
            var monitor = CaptureSourcePolicy.SelectMonitor(monitors, display ?? s.RecordMonitor, AppProfile.IsPersonal);
            if (monitor == null)
            {
                Error = "The selected monitor is unavailable. Choose a connected display to start recording.";
                return;
            }

            var available = VideoEncoders.GetAvailableAsync().GetAwaiter().GetResult();
            string? encoder = RecordingPresets.RecordingEncoderOrder.Contains(s.RecordEncoder) && available.Contains(s.RecordEncoder)
                ? s.RecordEncoder
                : RecordingPresets.DefaultEncoder(available)?.Id;
            if (encoder == null)
            {
                Error = "No graphics card video encoder works on this PC (NVIDIA, AMD or Intel). Updating the graphics driver may help.";
                return;
            }

            // The aspect ratio is for the monitor: a game recorded by its window is taken as it is.
            string aspect = s.RecordAspect;
            var area = RecordingPresets.Area(monitor.Width, monitor.Height, aspect);
            bool cropped = aspect != "" && (area.Width != monitor.Width || area.Height != monitor.Height);
            var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, s.RecordResolution);
            bool scaled = w != area.Width || h != area.Height;
            string family = VideoEncoders.Find(encoder)?.Family ?? "h264";
            VideoKbps = RecordingPresets.VideoKbps(s.RecordQuality, w, h, s.RecordFps, family);
            var endpoints = EndpointAudio.Plan(s);
            var mics = endpoints == null && s.RecordMic ? Microphones.Plan(s.MicDeviceIds, CaptureEngine.EnumMicrophones()) : [];
            bool separateMics = s.SeparateMicTracks && mics.Count > 1;
            bool splitChat = RecordingPresets.SplitChat(s), splitMusic = RecordingPresets.SplitMusic(s);
            AudioTracks = endpoints?.Count ?? (1 + (splitChat ? 1 : 0) + (splitMusic ? 1 : 0) + (separateMics ? mics.Count : Math.Min(mics.Count, 1)));

            Error = CaptureEngine.Start(monitor.DeviceName, aspect, s.RecordFps, scaled ? w : 0, scaled ? h : 0, encoder, VideoKbps,
                RecordingPresets.AudioKbps, s.BufferSeconds, s.CaptureCursor, splitChat, splitMusic, mics, separateMics, endpoints,
                new() { Backend = backend, Preset = s.NvencPreset, Diagnostics = s.RecordingDiagnostics ? 1 : 0 }, displayIdentity);
            if (Error != null) return;
            if (mics.Count > 0)
                Log.Write($"Recording {mics.Count} microphone{(mics.Count == 1 ? "" : "s")}{(separateMics ? " on separate tracks" : "")}: " +
                          string.Join(", ", mics.Select(m => m.DeviceId == "" ? $"{m.Title} (Windows default)" : m.Title)));

            IsRunning = true;
            _replaySession = Guid.NewGuid();
            _replayBufferSeconds = s.BufferSeconds;
            _family = family;
            _captureTarget = null;
            _desktopSource = backend switch
            {
                CaptureEngine.MonitorBackend.MonitorOnlyDuplication => new(monitor.DeviceName, ReplayCaptureBackend.DesktopDuplication),
                CaptureEngine.MonitorBackend.MonitorOnlyGraphics => new(monitor.DeviceName, ReplayCaptureBackend.WindowsGraphicsCapture),
                _ => null,
            };
            _monitorTarget = _desktopSource?.Backend switch
            {
                ReplayCaptureBackend.DesktopDuplication => $"Desktop · {monitor.Label} · Desktop Duplication",
                ReplayCaptureBackend.WindowsGraphicsCapture => $"Desktop · {monitor.Label} · Windows Graphics Capture (capture border may appear)",
                _ => monitor.Label,
            };
            _captureWindowSize = default;
            _routing = null;
            if (endpoints != null) (_chatApps, _musicApps) = ([], []);
            lock (_audioProcesses) _audioProcesses.Clear();
            _summary = $"{h}p{s.RecordFps}{(cropped ? $" {aspect}" : "")} · {family.ToUpperInvariant()} · {RecordingPresets.Label(s.RecordQuality)}" +
                       (endpoints == null ? "" : endpoints.Count == 0 ? " · video only" : $" · {endpoints.Count} endpoint audio tracks");
            Log.Write($"Recording {monitor.DeviceName}{(cropped ? $" ({area.Width}x{area.Height} at {area.X},{area.Y})" : "")} at {w}x{h}@{s.RecordFps} " +
                      $"with {encoder}, {VideoKbps} kbps, {s.BufferSeconds}s buffer");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Write("Starting the recorder failed: " + ex);
        }
    }

    private void StopCore()
    {
        ++_pollGeneration;
        _poll?.Dispose();
        _poll = null;
        if (AppProfile.IsPersonal) _lifecycle.Reset();
        else StopGraph();
    }

    private void StopGraph()
    {
        if (IsRunning) CaptureEngine.Stop();
        IsRunning = false;
        _replayMarkers.Clear();
        (_sessionGame, _sessionGameId, _sessionDisplay, _desktopSource) = (null, null, null, null);
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
            LogRouting(processes, plan);
            var chatNames = chat.Select(c => c.Name).Distinct().ToList();
            var musicNames = music.Select(m => m.Name).Distinct().ToList();
            if (!chatNames.SequenceEqual(_chatApps) || !musicNames.SequenceEqual(_musicApps))
            {
                (_chatApps, _musicApps) = (chatNames, musicNames);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Recorder poll failed: " + ex.GetType().Name);
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

    /// <summary>Notes in the log which apps each audio track records, whenever that changes.</summary>
    private void LogRouting(IReadOnlyList<ProcessScan.Entry> processes, AudioRouting.Plan plan)
    {
        var names = new Dictionary<uint, string>();
        foreach (var p in processes) names.TryAdd(p.Pid, Path.GetFileNameWithoutExtension(p.ExeName));
        string Names(IEnumerable<uint> pids) =>
            pids.Any() ? string.Join(", ", pids.Select(p => names.GetValueOrDefault(p, $"process {p}")).Distinct()) : "nothing";
        string desktop = plan.Exclude == 0 ? $"Desktop only {Names(plan.Desktop)}"
            : $"Desktop everything except {Names([plan.Exclude])}" + (plan.Desktop.Count > 0 ? $" but with {Names(plan.Desktop)}, which it started" : "") +
              (plan.Subtract.Count > 0 ? $", less {Names(plan.Subtract)}'s sound" : "");
        string routing = desktop + $"; Chat {Names(plan.Chat)}; Music {Names(plan.Music)}" +
                         (plan.Missing.Count > 0 ? $"; Desktop can't record {Names(plan.Missing)}, which started the chat or music app" : "");
        if (routing == _routing) return;
        _routing = routing;
        Log.Write("Audio tracks: " + routing);
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
            Log.Write("Recording the monitor again: the game's window closed");
            StateChanged?.Invoke(State);
            return;
        }
        var (w, h) = RecordingPresets.OutputSize(size.Width & ~7, size.Height & ~1, s.RecordResolution);
        int kbps = RecordingPresets.VideoKbps(s.RecordQuality, w, h, s.RecordFps, _family);
        CaptureEngine.SetCaptureWindow(window, w, h, kbps);
        Log.Write($"Recording the game window ({size.Width}x{size.Height}) at {w}x{h}, {kbps} kbps");
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
            if (ex.Message != _routingError) Log.Write("Listing the apps with audio failed: " + ex.Message);
            _routingError = ex.Message;
            return new AudioRouting.Plan(chat.Concat(music).DefaultIfEmpty(self).First(), [], chat, music);
        }
    }

    public CaptureEngine.Stats Stats() => IsRunning ? CaptureEngine.GetStats() : default;

    /// <summary>Marks the most recent encoded frame. A marker alone does not keep footage in the replay buffer.</summary>
    public LiveReplayMarker? TryAddReplayMarker(string? label = null)
    {
        lock (_gate)
        {
            if (!IsRunning || CaptureEngine.GetReplayPosition() is not { } position) return null;
            var game = _captureTarget?.Game;
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
        if (!IsRunning)
        {
            SaveFailed?.Invoke(Error ?? (State.Mode == RecordingMode.Armed
                ? "No recording is active. Start an eligible game or turn on manual desktop capture." : "Recording is turned off."));
            return null;
        }
        return await _saveGate.RunAsync(() => SaveUnderGateAsync(requestedSeconds, cancellationToken), cancellationToken,
            requestedSeconds.HasValue ? () => SaveFailed?.Invoke("A replay save is already in progress.") : null);
    }

    private async Task<(SavedClip Clip, ReplaySaveReceipt Receipt)?> SaveUnderGateAsync(int? requestedSeconds, CancellationToken cancellationToken)
    {
        string? staged = null;
        bool complete = false;
        try
        {
            var s = AppSettings.Current;
            int seconds = requestedSeconds ?? s.BufferSeconds;
            CaptureEngine.ReplayPosition position;
            ReplayMarkers.Candidate[] candidates;
            Guid sessionId;
            int gameLookback;
            ReplayDesktopSource? desktopSource;
            string? gameContext;
            string? gameId;
            lock (_gate)
            {
                if (!IsRunning || CaptureEngine.GetReplayPosition() is not { } active)
                    throw new IOException("The recording session has ended.");
                position = active;
                sessionId = _replaySession;
                desktopSource = _desktopSource;
                candidates = _replayMarkers.Snapshot(position, sessionId);
                gameLookback = requestedSeconds.HasValue ? Math.Min(seconds, _replayBufferSeconds) : seconds;
                gameContext = AppProfile.IsPersonal ? _sessionGame : _captureTarget?.Game ?? _games.GameForClip(gameLookback);
                gameId = _sessionGameId;
            }
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
            string? error = await Task.Run(() => CaptureEngine.SaveReplay(stagingPath, seconds, mediaTitle, position,
                requestedSeconds.HasValue, anchors, out span, out video), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (error != null)
            {
                Log.Write("Saving a clip failed: " + error);
                SaveFailed?.Invoke(error);
                return null;
            }
            complete = true;
            DateTimeOffset? capturedAt = Mp4Info.Read(stagingPath).CaptureTime?.ToLocalTime();
            DateTimeOffset capture = capturedAt ?? requestedAt;
            string stem = AppProfile.IsPersonal ? ClipNaming.Stem(s.ClipFilenameTemplate, folder, capture, null)
                : $"{folder} {requestedAt:yyyy-MM-dd HH-mm-ss}";
            cancellationToken.ThrowIfCancellationRequested();
            string path = ClipNaming.Publish(stagingPath, dir, stem, ".mp4");
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
            if (AppProfile.IsPersonal) ClipDetailsStore.TryWrite(path, new ClipDetails(capture, folder)
            {
                Bookmarks = receipt.Markers.Select(mark => new ClipBookmark(mark.Offset.TotalSeconds, mark.Marker.Label ?? "Marker")
                { SessionId = mark.Marker.SessionId }).ToArray(),
            });
            int displaySeconds = requestedSeconds.HasValue ? (int)(actual?.TotalSeconds ?? 0) : seconds;
            var clip = new SavedClip(path, folder, displaySeconds);
            Log.Write($"Saved clip {path}");
            try { ClipSaved?.Invoke(clip); }
            catch (Exception ex) { Log.Write("Clip saved notification failed: " + ex.Message); }
            try { ReplaySaved?.Invoke(receipt); }
            catch (Exception ex) { Log.Write("Replay receipt notification failed: " + ex.Message); }
            return (clip, receipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            complete = false;
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write("Saving a clip failed: " + ex.Message);
            SaveFailed?.Invoke(complete && File.Exists(staged) ? $"{ex.Message} The recording is still at {staged}." : ex.Message);
            return null;
        }
        finally
        {
            try { if (!complete && staged != null) File.Delete(staged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

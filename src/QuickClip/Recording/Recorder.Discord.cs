using System.Diagnostics;
using QuickClip.Recording.Companion;

namespace QuickClip.Recording;

internal sealed partial class Recorder
{
    private readonly DiscordGroupPolicy _discordGroup = new();
    private readonly ActivityClassifier _discordActivity = new();
    private Task<ProbeResult>? _discordTask;
    private long _discordSubmittedAt;
    private long _discordObservedAt;
    private long _discordLastEligibleAt;
    private ProbeResult? _discordObservation;
    private ActivityState _discordState = ActivityState.Unknown;
    private DiscordGroupDecision? _discordDecision;
    private bool _discordActive;
    private bool _discordManualRequested;
    private long _discordRetryAfter;
    private int _discordRetryDelaySeconds;
    private int _discordGate = -1;

    // Transition-only diagnostic codes (never window titles, PIDs or call data):
    // 1 = game/desktop priority, 2 = awaiting fresh probe,
    // 3 = awaiting call evidence, 4 = window/policy ineligible,
    // 5 = retry backoff, 6 = eligible starting, 7 = capturing.
    private void TraceDiscordGate(int gate)
    {
        if (_discordGate == gate) return;
        _discordGate = gate;
        Log.Event(DiagnosticCategory.Capture, "Discord capture eligibility changed", code: gate);
        if (gate == 3 && _discordObservation is { Complete: true } probe)
        {
            // Counts only. Cross-process Discord call activity can otherwise
            // look identical to an inactive call; never log endpoint IDs/PIDs.
            Log.Event(DiagnosticCategory.Capture, "Discord voice input owners", code: probe.MicrophoneOwners);
            Log.Event(DiagnosticCategory.Capture, "Discord UDP owners", code: probe.UdpOwners);
            Log.Event(DiagnosticCategory.Capture, "Discord matched voice network owners", code: probe.MatchingOwners);
        }
    }

    private void ScheduleDiscordRetry()
    {
        _discordRetryDelaySeconds = Math.Min(
            _discordRetryDelaySeconds == 0 ? 2 : _discordRetryDelaySeconds * 2, 30);
        _discordRetryAfter = Stopwatch.GetTimestamp() +
            (long)(_discordRetryDelaySeconds * (double)Stopwatch.Frequency);
    }

    private void ClearDiscordRetry()
    {
        _discordRetryAfter = 0;
        _discordRetryDelaySeconds = 0;
    }


    internal bool ManualDiscordRequested { get { lock (_gate) return _discordManualRequested; } }

    internal string DiscordStatus
    {
        get
        {
            lock (_gate)
            {
                if (!AppSettings.Current.DiscordCaptureEnabled) return "Off";
                if (!AppSettings.Current.RecordingEnabled) return "Recording master switch is off";
                if (_observedGame != null || ManualDesktopRequested) return "Waiting for game or desktop recording";
                if (_discordActive) return AppSettings.Current.DiscordCloakedMirror &&
                    Environment.OSVersion.Version.Build < 20348
                    ? $"Recording {(_discordDecision?.Targets.Count ?? 0)} Discord windows (experimental cloaked mirror)"
                    : $"Recording {(_discordDecision?.Targets.Count ?? 0)} Discord windows";
                return _discordDecision?.Status switch
                {
                    DiscordGroupStatus.Unknown => "Checking Discord...",
                    DiscordGroupStatus.TooManyWindows => "Too many Discord windows (limit 4)",
                    DiscordGroupStatus.Waiting when _discordState == ActivityState.Joining => "Joining a call...",
                    DiscordGroupStatus.Waiting when _discordState == ActivityState.Leaving => "Checking disconnection...",
                    DiscordGroupStatus.Waiting when AppSettings.Current.DiscordCaptureMode == DiscordCaptureMode.DuringCalls =>
                        "Waiting for verified Discord call activity",
                    _ => "Waiting for Discord"
                };
            }
        }
    }

    internal Task SetManualDiscordAsync(bool requested) => Task.Run(() =>
    {
        RecorderState previous, next;
        lock (_gate)
        {
            previous = State;
            _discordManualRequested = requested && AppSettings.Current.DiscordCaptureEnabled &&
                AppSettings.Current.DiscordCaptureMode == DiscordCaptureMode.Manual;
            if (!_discordManualRequested && _discordActive &&
                AppSettings.Current.DiscordCaptureMode == DiscordCaptureMode.Manual)
                StopGraph();
            ReconcilePersonal();
            next = State;
        }
        if (previous != next) StateChanged?.Invoke(next);
    });

    private void ResetDiscordObservation(bool stop)
    {
        if (stop && _discordActive) StopGraph();
        _discordActivity.Reset();
        _discordState = ActivityState.Unknown;
        _discordObservation = null;
        _discordDecision = null;
        _discordTask = null; // a pending task may finish but has no capture authority
        _discordObservedAt = 0;
        _discordLastEligibleAt = 0;
    }

    private void ReconcileDiscord(AppSettings settings, bool higherPriority)
    {
        if (!settings.RecordingEnabled || !settings.DiscordCaptureEnabled ||
            settings.DiscordCaptureMode == DiscordCaptureMode.Disabled || higherPriority)
        {
            TraceDiscordGate(higherPriority ? 1 : 4);
            ResetDiscordObservation(stop: true);
            return;
        }

        // The expensive COM/UDP probe runs once per poll and never on the UI thread.
        // Only the consumer under _gate may turn an observation into capture eligibility.
        if (_discordTask is { IsCompleted: true } pending)
        {
            _discordTask = null;
            ProbeResult sample;
            try { sample = pending.GetAwaiter().GetResult(); }
            catch (Exception) { sample = new(false,0,0,0,0,null,"Probe unavailable",0); }
            bool fresh = Stopwatch.GetElapsedTime(_discordSubmittedAt) <= TimeSpan.FromSeconds(3);
            if (!fresh) sample = sample with { Complete = false, Targets = [] };
            _discordObservation = sample;
            _discordObservedAt = Stopwatch.GetTimestamp();
            _discordState = _discordActivity.Update(new(sample.Complete, sample.Processes != 0,
                sample.MatchingOwners != 0,
                TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency)));
        }
        if (_discordTask == null)
        {
            _discordSubmittedAt = Stopwatch.GetTimestamp();
            bool callEvidence = settings.DiscordCaptureMode == DiscordCaptureMode.DuringCalls;
            _discordTask = Task.Run(() => ActivityProbe.Read("Discord", callEvidence));
        }

        var observation = _discordObservation;
        if (observation == null || _discordObservedAt == 0 ||
            Stopwatch.GetElapsedTime(_discordObservedAt) > TimeSpan.FromSeconds(3))
        {
            // Delayed probes cannot authorize new windows. Preserve only live,
            // previously verified HWNDs for a bounded three-second grace period.
            var held = _discordActive && _discordLastEligibleAt != 0
                ? DiscordGroupPolicy.HoldVerifiedSurvivors(_discordDecision,
                    DiscordGroupStatus.Unknown,
                    Stopwatch.GetElapsedTime(_discordLastEligibleAt), GameDetector.IsCurrent)
                : null;
            if (held is { } safe && CaptureEngine.UpdateCompanionWindows(safe.Targets))
            {
                _discordDecision = safe;
                return;
            }
            if (_discordActive) StopGraph();
            _discordDecision = new(DiscordGroupStatus.Unknown, []);
            TraceDiscordGate(2);
            return;
        }
        var previous = _discordDecision;
        var decision = _discordGroup.Decide(settings.RecordingEnabled, settings.DiscordCaptureMode,
            settings.DiscordCaptureEnabled, higherPriority, _discordManualRequested,
            _discordState, observation, true, GameDetector.IsCurrent);
        if (decision.Status == DiscordGroupStatus.Eligible)
            _discordLastEligibleAt = Stopwatch.GetTimestamp();
        else if (_discordActive && _discordLastEligibleAt != 0)
        {
            // Transient enumeration mismatches are common when Squirrel/Electron
            // closes a pop-out. Keep the existing native encoder/ring, but remove
            // invalid HWNDs immediately. Never broaden captured source identity.
            decision = DiscordGroupPolicy.HoldVerifiedSurvivors(previous, decision.Status,
                Stopwatch.GetElapsedTime(_discordLastEligibleAt), GameDetector.IsCurrent)
                ?? decision;
        }
        _discordDecision = decision;
        if (decision.Status != DiscordGroupStatus.Eligible)
        {
            TraceDiscordGate(decision.Status == DiscordGroupStatus.Waiting &&
                settings.DiscordCaptureMode == DiscordCaptureMode.DuringCalls ? 3 : 4);
            if (_discordActive) StopGraph();
            return;
        }
        if (_discordActive)
        {
            TraceDiscordGate(7);
            if (!CaptureEngine.UpdateCompanionWindows(decision.Targets))
            {
                ScheduleDiscordRetry();
                StopGraph();
            }
            else if (CaptureEngine.HasVideoOutput() &&
                Environment.TickCount64 - _captureStartedAt >= 15_000)
                ClearDiscordRetry();
            return;
        }
        if (IsRunning || _lifecycle.Mode is not RecordingMode.Armed)
        {
            TraceDiscordGate(4);
            return;
        }
        if (_discordRetryAfter != 0 && Stopwatch.GetTimestamp() < _discordRetryAfter)
        {
            TraceDiscordGate(5);
            return;
        }
        if (!decision.Targets.All(GameDetector.IsCurrent))
        {
            TraceDiscordGate(4);
            return;
        }
        TraceDiscordGate(6);
        StartCore(companion: decision.Targets);
        _discordActive = IsRunning;
        if (!_discordActive)
        {
            ScheduleDiscordRetry();
            TraceDiscordGate(5);
            return;
        }
        TraceDiscordGate(7);
        _sessionGame = "Discord";
        _sessionGameId = null;
        _sessionGameExecutable = null;
        _sessionDisplay = "Discord windows";
    }

    private bool MaySaveDiscord(AppSettings settings) =>
        settings.RecordingEnabled && settings.DiscordCaptureEnabled && _discordActive &&
        _discordDecision is { Status: DiscordGroupStatus.Eligible } decision &&
        decision.Targets.Count > 0 && decision.Targets.All(GameDetector.IsCurrent) &&
        !CaptureEngine.VideoSourceLost();
}

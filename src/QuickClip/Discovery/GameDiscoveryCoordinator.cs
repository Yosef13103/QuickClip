using System.Collections.Immutable;

namespace QuickClip.Discovery;

/// <summary>Serializes discovery work while keeping each enabled source's cursor and publication independent.</summary>
internal sealed class GameDiscoveryCoordinator : IDisposable
{
    private sealed record SourcePlan(CatalogScope Scope, CatalogRequest Request, string Signature);

    private sealed class Work(SourcePlan plan) : IDisposable
    {
        public SourcePlan Plan { get; } = plan;
        public CancellationTokenSource Cancellation { get; } = new();
        public GameCatalogService.Scan? Scan { get; set; }

        public void Dispose()
        {
            Scan?.Dispose();
            Cancellation.Dispose();
        }
    }

    private readonly object _gate = new();
    private readonly Func<GameCatalogSettings> _read;
    private readonly Action<GameCatalogSettings, bool> _publish;
    private readonly Func<CatalogRequest, GameCatalogService.Scan> _create;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _clock;
    private readonly int _segmentsPerSlice;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private Dictionary<string, SourcePlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Work> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogResult> _results = new(StringComparer.Ordinal);
    private Work? _active;
    private Task _running = Task.CompletedTask;
    private bool _started, _disposed, _paused, _autoScan;
    private bool _canceled;

    internal GameDiscoveryCoordinator(Func<GameCatalogSettings> read, Action<GameCatalogSettings, bool> publish,
        Func<CatalogRequest, GameCatalogService.Scan>? create = null, TimeProvider? clock = null, int segmentsPerSlice = 12)
    {
        if (segmentsPerSlice < 1) throw new ArgumentOutOfRangeException(nameof(segmentsPerSlice));
        _read = read;
        _publish = publish;
        _create = create ?? (request => GameCatalogService.CreateScan(request));
        _clock = clock ?? TimeProvider.System;
        _segmentsPerSlice = segmentsPerSlice;
    }

    public event Action? Changed;
    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "";
    public bool IsPaused => _paused && _pending.Count > 0;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _ = RunTimerAsync(_lifetime.Token);
        }
    }

    private async Task RunTimerAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RequestAsync();
                await Task.Delay(RefreshInterval, _clock, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public Task RequestAsync(bool manual = false, string? sourceKey = null)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            var state = _read();
            SyncPlans(state, scheduleChanges: false);
            if (!manual && (!state.AutoScan || _paused)) return Task.CompletedTask;
            if (IsRunning)
            {
                if (manual && sourceKey != null && _plans.TryGetValue(sourceKey, out var existing))
                    Queue(existing);
                return _running;
            }
            bool resume = _paused;
            _paused = false;
            if (!resume)
            {
                _results.Clear();
                foreach (var plan in _plans.Values)
                {
                    string key = plan.Scope.Key;
                    if (sourceKey != null && key != sourceKey) continue;
                    if (manual || !_completed.TryGetValue(key, out long last) || _clock.GetElapsedTime(last) >= RefreshInterval)
                        Queue(plan);
                }
            }
            if (_pending.Count == 0 && manual)
            {
                Status = "Add a game folder or enable a launcher.";
                Changed?.Invoke();
            }
            return StartWork();
        }
    }

    /// <summary>Pauses at the next slice boundary. Scan Now resumes the current batch.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            _paused = true;
            Status = "Pausing after the current scan batch…";
            Changed?.Invoke();
        }
    }

    public Task SettingsChangedAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            var state = _read();
            bool changed = SyncPlans(state, scheduleChanges: _started);
            var pruned = CatalogClaims.Reconfigure(state);
            if (pruned.SourceClaims != null && (changed || !ReferenceEquals(pruned, state)))
                Publish(pruned with { ScanPreview = null }, _pending.Count == 0, persist: true);
            return _paused ? Task.CompletedTask : StartWork();
        }
    }

    private static IEnumerable<SourcePlan> Plans(GameCatalogSettings settings) =>
        CatalogScope.Enabled(settings).Select(scope =>
        {
            var request = scope.Request(settings.Request);
            return new SourcePlan(scope, request, CatalogScope.Signature(request));
        });

    private bool SyncPlans(GameCatalogSettings state, bool scheduleChanges)
    {
        bool stoppedAutomatic = _autoScan && !state.AutoScan;
        bool startedAutomatic = !_autoScan && state.AutoScan;
        _autoScan = state.AutoScan;
        var next = Plans(state).ToDictionary(plan => plan.Scope.Key, StringComparer.Ordinal);
        bool changed = false;
        if (!IsRunning && _pending.Count == 0 && next.Any(item =>
            !_plans.TryGetValue(item.Key, out var prior) || prior.Signature != item.Value.Signature))
            _results.Clear();
        foreach (var (key, old) in _plans)
        {
            bool invalidated = !next.TryGetValue(key, out var current) || current.Signature != old.Signature;
            if (invalidated || stoppedAutomatic)
            {
                if (_pending.Remove(key, out var work))
                {
                    _canceled = true;
                    if (ReferenceEquals(_active, work) && IsRunning) work.Cancellation.Cancel();
                    else
                    {
                        work.Dispose();
                        if (ReferenceEquals(_active, work)) _active = null;
                    }
                }
                if (invalidated)
                {
                    _completed.Remove(key);
                    _results.Remove(key);
                    changed = true;
                }
            }
        }
        foreach (var (key, plan) in next)
        {
            bool affected = !_plans.TryGetValue(key, out var old) || old.Signature != plan.Signature;
            if (affected) changed = true;
            if (scheduleChanges && !stoppedAutomatic && (affected || (startedAutomatic && !_completed.ContainsKey(key))))
                Queue(plan);
        }
        if (_pending.Count == 0) _paused = false;
        _plans = next;
        return changed;
    }

    private void Queue(SourcePlan plan)
    {
        if (!_pending.ContainsKey(plan.Scope.Key)) _pending.Add(plan.Scope.Key, new Work(plan));
    }

    private Task StartWork()
    {
        if (IsRunning) return _running;
        if (_pending.Count == 0 || _paused) return Task.CompletedTask;
        IsRunning = true;
        _canceled = false;
        Status = "Scanning game libraries…";
        _running = RunAsync();
        Changed?.Invoke();
        return _running;
    }

    private async Task RunAsync()
    {
        using var activity = QuickClip.Shell.BackgroundActivity.Begin("Discovering games");
        await Task.Yield();
        while (true)
        {
            Work work;
            lock (_gate)
            {
                if (_disposed || _paused || _pending.Count == 0)
                {
                    IsRunning = false;
                    if (_paused && _read().Snapshot is { } snapshot) Status = Describe(_read(), "Paused", snapshot);
                    else if (_canceled && !_disposed && Status.StartsWith("Scanning", StringComparison.Ordinal))
                    {
                        var state = _read();
                        if (state.Snapshot is { InProgress: true } canceled)
                            _publish(state with { ScanPreview = null, Snapshot = canceled with { InProgress = false } }, true);
                        Status = "Scan canceled";
                    }
                    if (!_disposed) Changed?.Invoke();
                    return;
                }
                _active = work = _pending.Values.First();
            }

            var scope = work.Plan.Scope;
            var token = work.Cancellation.Token;
            bool keep = false;
            long lastProgress = _clock.GetTimestamp();
            long lastDiagnostic = lastProgress;
            try
            {
                if (work.Scan == null) Log.Event(DiagnosticCategory.GameDetection, "Game source scan started");
                work.Scan ??= _create(work.Plan.Request);
                while (true)
                {
                    var result = await work.Scan.ContinueAsync(_segmentsPerSlice, TimeSpan.FromMilliseconds(250), token, throttleSnapshots: true);
                    activity.Report($"Discovering games · {LocalLaunchers.Name(scope.Kind)} · {result.VisitedEntries:N0} items checked");
                    bool pause;
                    lock (_gate)
                    {
                        if (!IsCurrent(work)) break;
                        pause = _paused;
                        if (!result.InProgress)
                        {
                            _pending.Remove(scope.Key);
                            _completed[scope.Key] = _clock.GetTimestamp();
                            _results[scope.Key] = result;
                            var updated = CatalogClaims.Apply(_read(), scope, result, preview: false);
                            Publish(updated, _pending.Count == 0, persist: true);
                            Log.Event(DiagnosticCategory.GameDetection, "Game source scan completed");
                            break;
                        }
                        if (!pause && work.Scan.HasCatalogChanges)
                        {
                            var updated = CatalogClaims.Apply(_read(), scope, result, preview: true);
                            Publish(updated, false, persist: false);
                        }
                        // A source may check many irrelevant entries without changing the catalog.
                        // Report bounded progress rather than publishing an unchanged catalog again.
                        if (_clock.GetElapsedTime(lastProgress) >= TimeSpan.FromSeconds(2))
                        {
                            lastProgress = _clock.GetTimestamp();
                            Status = $"Scanning {LocalLaunchers.Name(scope.Kind)} · {result.VisitedEntries:N0} items checked · {_pending.Count:N0} source(s) remaining";
                            Changed?.Invoke();
                        }
                        if (Log.Enabled && _clock.GetElapsedTime(lastDiagnostic) >= TimeSpan.FromSeconds(10))
                        {
                            lastDiagnostic = _clock.GetTimestamp();
                            Log.Event(DiagnosticCategory.GameDetection, "Game source scan progress (items checked)", code: result.VisitedEntries);
                        }
                    }

                    lock (_gate) pause |= _paused;
                    if (pause)
                    {
                        var snapshot = await work.Scan.SnapshotAsync(token);
                        lock (_gate)
                        {
                            if (IsCurrent(work) && _paused)
                            {
                                var updated = CatalogClaims.Apply(_read(), scope, snapshot, preview: true);
                                Publish(updated, false, persist: true);
                                keep = true;
                                break;
                            }
                        }
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Log.Event(DiagnosticCategory.GameDetection, "Game source scan canceled");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                lock (_gate)
                {
                    if (IsCurrent(work))
                    {
                        _pending.Remove(scope.Key);
                        _completed[scope.Key] = _clock.GetTimestamp();
                        var result = new CatalogResult([], [new(scope.Root ?? scope.Kind.ToString(), CatalogIssueKind.Unavailable)],
                            0, false, _clock.GetUtcNow());
                        _results[scope.Key] = result;
                        Publish(CatalogClaims.Apply(_read(), scope, result, preview: false), _pending.Count == 0, persist: true);
                        Log.Failure(DiagnosticCategory.GameDetection, "Game source scan failed", ex);
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _active = null;
                    if (!keep)
                    {
                        if (_pending.TryGetValue(scope.Key, out var queued) && ReferenceEquals(queued, work))
                            _pending.Remove(scope.Key);
                        work.Dispose();
                    }
                }
            }
        }
    }

    private bool IsCurrent(Work work) => !_disposed && !work.Cancellation.IsCancellationRequested &&
        _pending.TryGetValue(work.Plan.Scope.Key, out var pending) && ReferenceEquals(work, pending) &&
        Plans(_read()).Any(plan => plan.Scope.Key == work.Plan.Scope.Key && plan.Signature == work.Plan.Signature);

    private void Publish(GameCatalogSettings state, bool finished, bool persist)
    {
        var preview = state.ScanPreview;
        var results = _results.Values.Concat(preview != null ? [preview.Result] : []).ToArray();
        // Missing optional launcher discovery metadata is normal on a machine
        // without that launcher. Retain raw source claims for cache/authorization
        // reconciliation; only omit expected absence from the user-facing report.
        var issues = _results.SelectMany(pair => pair.Value.Issues.Where(issue =>
                issue.Kind != CatalogIssueKind.SourceMissing ||
                (_plans.TryGetValue(pair.Key, out var plan) && plan.Scope.Root != null)))
            .Concat(preview == null ? [] : preview.Result.Issues.Where(issue =>
                issue.Kind != CatalogIssueKind.SourceMissing || preview.Scope.Root != null))
            .Distinct().Take(129).ToArray();
        var summary = new CatalogResult([], [.. issues.Take(128)], results.Sum(result => result.VisitedEntries),
            results.Any(result => result.LimitReached), _clock.GetUtcNow())
        {
            HasMoreIssues = issues.Length > 128 || results.Any(result => result.HasMoreIssues),
            HasUnreportedFailures = results.Any(result => result.HasUnreportedFailures),
            SourceUnavailable = results.Any(result => result.SourceUnavailable),
            CandidateExecutablesVisited = results.Sum(result => result.CandidateExecutablesVisited),
        };
        var updated = CatalogClaims.Summarize(state, summary, finished, _results.Keys.ToHashSet(StringComparer.Ordinal));
        _publish(updated, persist);
        bool missing = issues.Any(issue => issue.Kind == CatalogIssueKind.SourceMissing);
        bool incomplete = summary.SourceUnavailable || summary.LimitReached || summary.HasUnreportedFailures ||
            issues.Any(issue => issue.Kind is not (CatalogIssueKind.SourceMissing or CatalogIssueKind.ReparsePoint)) ||
            (missing && !updated.RetainedIds.IsEmpty);
        var label = !finished ? (_paused ? "Paused" : "Scanning") : incomplete ? "Incomplete" :
            missing ? "Completed · source missing" : "Completed";
        Status = Describe(updated, label, summary);
        Changed?.Invoke();
    }

    internal static string Describe(GameCatalogSettings state, string label, CatalogResult result) =>
        $"{label} · {result.CandidateExecutablesVisited:N0} candidate executable visits · {state.DiscoveredCount ?? 0:N0} discovered this scan";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            foreach (var work in _pending.Values)
            {
                if (ReferenceEquals(work, _active)) work.Cancellation.Cancel();
                else work.Dispose();
            }
            _pending.Clear();
        }
    }
}

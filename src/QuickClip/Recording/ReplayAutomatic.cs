using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Recording;

internal enum ReplayOverlapMode { Separate, ExtendPrevious, CombineAfterRecording }

internal readonly record struct ReplayOverlapOptions(ReplayOverlapMode Mode, int Seconds)
{
    internal static ReplayOverlapOptions Read(AppSettings settings) => new(
        AppProfile.IsPersonal && settings.EnableLibraryIndex && Enum.IsDefined(settings.OverlapMode)
            ? settings.OverlapMode : ReplayOverlapMode.Separate,
        Math.Clamp(settings.MaximumCombinedReplaySeconds, 30, 3600));
    internal TimeSpan Limit => TimeSpan.FromSeconds(Seconds);
}

internal sealed record AutomaticReplay(ReplayProvenance Provenance, ClipDetails Details);

/// <summary>Bounded session orchestration only; every proof and remux uses the existing MediaJobs queue.</summary>
internal sealed class ReplayAutomaticCoordinator(Func<ReplayOverlapOptions> options,
    Func<MediaSource, CancellationToken, Task<AutomaticReplay?>> read,
    Func<ReplayCluster, IReadOnlyList<ClipDetails>, ReplayOverlapOptions, Func<bool>, CancellationToken, Task<AutomaticReplay?>> combine)
{
    internal static ReplayAutomaticCoordinator Current { get; } = new(
        () => ReplayOverlapOptions.Read(AppSettings.Current), ReadAsync, CombineAsync);
    private const int MaxSessions = 8;
    private const int MaxLivePending = 32;
    private const int MaxDeferred = 128;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private bool _stopping;

    internal sealed class Ticket(MediaSource? source)
    {
        internal MediaSource? Source { get; } = source;
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool> Ready => _ready.Task;
        internal void Complete(Task<MediaJobView>? proof)
        {
            if (proof == null) _ready.TrySetResult(false);
            else _ = SettleAsync(proof);
        }
        private async Task SettleAsync(Task<MediaJobView> proof)
        {
            try { _ready.TrySetResult((await proof.ConfigureAwait(false)).State == MediaJobState.Completed); }
            catch { _ready.TrySetResult(false); }
        }
    }

    private sealed class Session(Guid id, ReplayOverlapOptions policy)
    {
        internal Guid Id { get; } = id;
        internal ReplayOverlapOptions Policy { get; } = policy;
        internal Queue<Ticket> Pending { get; } = new();
        internal AutomaticReplay? Previous;
        internal bool Ended, Abandoned, Processing;
    }

    // Reserve at successful publication under Recorder's lifecycle lock.
    // No filesystem/SQLite work or waiting occurs on the save/stop boundary.
    internal Ticket? Reserve(Guid session, MediaSource? source)
    {
        lock (_gate)
        {
            var policy = options();
            if (_stopping || session == Guid.Empty) return null;
            if (_sessions.TryGetValue(session, out var prior) && prior.Policy != policy)
            {
                prior.Abandoned = true;
                prior.Pending.Clear();
                _sessions.Remove(session);
            }
            if (policy.Mode == ReplayOverlapMode.Separate) return null;
            if (!_sessions.TryGetValue(session, out var state))
            {
                if (_sessions.Count >= MaxSessions) return null;
                _sessions.Add(session, state = new(session, policy));
            }
            if (state.Ended || state.Abandoned) return null;
            int maximum = policy.Mode == ReplayOverlapMode.ExtendPrevious ? MaxLivePending : MaxDeferred;
            if (state.Pending.Count >= maximum)
            {
                state.Abandoned = true;
                state.Pending.Clear();
                // Safe fallback: preserve unprocessed originals, no retry storm.
                return null;
            }
            var ticket = new Ticket(source);
            state.Pending.Enqueue(ticket);
            Pump();
            return ticket;
        }
    }

    internal void EndSession(Guid session)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var state)) return;
            state.Ended = true;
            if (state.Abandoned || (!state.Processing && state.Pending.Count == 0)) _sessions.Remove(session);
            Pump();
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            foreach (var state in _sessions.Values) state.Abandoned = true;
            _sessions.Clear();
        }
    }

    internal async Task StopAsync()
    {
        Task? worker;
        lock (_gate)
        {
            _stopping = true;
            foreach (var state in _sessions.Values) state.Abandoned = true;
            _sessions.Clear();
            worker = _worker;
        }
        _shutdown.Cancel();
        if (worker != null) await worker.ConfigureAwait(false);
    }

    internal Task CurrentWork { get { lock (_gate) return _worker ?? Task.CompletedTask; } }

    private void Pump()
    {
        if (_worker == null && !_stopping && _sessions.Values.Any(Ready))
            _worker = Task.Run(DrainAsync);
    }

    private static bool Ready(Session state) => !state.Abandoned && state.Pending.Count > 0 &&
        (state.Policy.Mode == ReplayOverlapMode.ExtendPrevious || state.Ended);

    private bool Allowed(Session state)
    {
        lock (_gate) return !_stopping && !state.Abandoned && state.Policy == options();
    }

    private async Task DrainAsync()
    {
        var token = _shutdown.Token;
        while (true)
        {
            Session state;
            Ticket[] tickets;
            lock (_gate)
            {
                var next = _sessions.Values.FirstOrDefault(Ready);
                if (_stopping || next == null)
                {
                    _worker = null;
                    return;
                }
                state = next;
                state.Processing = true;
                if (state.Policy.Mode == ReplayOverlapMode.ExtendPrevious) tickets = [state.Pending.Dequeue()];
                else
                {
                    tickets = state.Pending.ToArray();
                    state.Pending.Clear();
                }
            }
            try
            {
                if (!Allowed(state)) continue;
                if (state.Policy.Mode == ReplayOverlapMode.ExtendPrevious)
                    await ExtendAsync(state, tickets[0], token).ConfigureAwait(false);
                else await DeferredAsync(state, tickets, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                state.Previous = null;
                Log.Failure(DiagnosticCategory.Library, "Automatic replay combination kept originals", ex);
            }
            finally
            {
                lock (_gate)
                {
                    state.Processing = false;
                    if (state.Ended && state.Pending.Count == 0 &&
                        _sessions.TryGetValue(state.Id, out var current) && ReferenceEquals(state, current))
                        _sessions.Remove(state.Id);
                }
            }
        }
    }

    private async Task<AutomaticReplay?> ResolveAsync(Session session, Ticket ticket, CancellationToken token)
    {
        if (!await ticket.Ready.WaitAsync(token).ConfigureAwait(false) || ticket.Source == null || !Allowed(session))
            return null;
        var result = await read(ticket.Source, token).ConfigureAwait(false);
        return result?.Provenance.Session == session.Id && result.Details.IndexRevision == 0 &&
            !result.Details.IsFavorite ? result : null;
    }

    private async Task ExtendAsync(Session state, Ticket ticket, CancellationToken token)
    {
        var current = await ResolveAsync(state, ticket, token).ConfigureAwait(false);
        var previous = state.Previous;
        state.Previous = current;
        if (current == null || previous == null || !Allowed(state)) return;
        // Only extend the immediately previous logical recording; a gap,
        // failed proof or cap starts a new logical recording.
        var refreshed = await read(previous.Provenance.Media, token).ConfigureAwait(false);
        if (refreshed == null || refreshed.Details.IsFavorite || refreshed.Details.IndexRevision != 0) return;
        var inputs = new[] { refreshed, current };
        var plan = Partition(inputs.Select(i => i.Provenance), state.Policy.Limit).SingleOrDefault();
        if (plan == null || !Allowed(state)) return;
        var byId = inputs.ToDictionary(i => i.Provenance.Id);
        var merged = await combine(plan, plan.Clips.Select(c => byId[c.Id].Details).ToArray(),
            state.Policy, () => Allowed(state), token).ConfigureAwait(false);
        if (merged != null) state.Previous = merged;
    }

    private async Task DeferredAsync(Session state, Ticket[] tickets, CancellationToken token)
    {
        var candidates = new List<AutomaticReplay>();
        foreach (var ticket in tickets)
        {
            token.ThrowIfCancellationRequested();
            if (!Allowed(state)) return;
            if (await ResolveAsync(state, ticket, token).ConfigureAwait(false) is { } candidate) candidates.Add(candidate);
        }
        var byId = candidates.ToDictionary(c => c.Provenance.Id);
        foreach (var plan in Partition(candidates.Select(c => c.Provenance), state.Policy.Limit))
        {
            if (!Allowed(state)) return;
            try
            {
                await combine(plan, plan.Clips.Select(c => byId[c.Id].Details).ToArray(), state.Policy,
                    () => Allowed(state), token).ConfigureAwait(false);
            }
            catch (ExportException ex)
            { Log.Failure(DiagnosticCategory.Library, "Deferred replay group kept originals", ex); }
        }
    }

    // Partition once into chronological, consecutive groups with a hard cap.
    // Never bridge gaps or incompatible epochs simply because a session ended.
    internal static ReplayCluster[] Partition(IEnumerable<ReplayProvenance> candidates, TimeSpan limit)
    {
        var result = new List<ReplayCluster>();
        foreach (var cluster in ReplayPlanner.Find(candidates))
        {
            int first = 0;
            while (first < cluster.Clips.Count)
            {
                int last = first;
                long end = cluster.Clips[first].EndUs;
                while (last + 1 < cluster.Clips.Count && last - first + 1 < ReplayCombine.MaxSources)
                {
                    var next = cluster.Clips[last + 1];
                    if (next.FirstUs >= end || Math.Max(end, next.EndUs) - cluster.Clips[first].FirstUs > limit.Ticks / 10)
                        break;
                    end = Math.Max(end, next.EndUs);
                    last++;
                }
                if (last > first) result.Add(ReplayPlanner.Subset(cluster, first, last, limit));
                first = last + 1;
            }
        }
        return result.ToArray();
    }

    private static async Task<AutomaticReplay?> ReadAsync(MediaSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReplayProvenanceStore.Current.ReadCandidates(new HashSet<string> { LibraryFiles.PathKey(source.Path) })
            .SingleOrDefault() is not { } provenance || provenance.Media != source)
            return null;
        var details = await PersonalLibrary.Current.RegisterReplayAsync(source, token).ConfigureAwait(false);
        if (details == null || details.IndexRevision != 0 || details.IsFavorite || EditorRecovery.Exists(source.Stamp) ||
            await PersonalLibrary.Current.ReadEditDraftAsync(source.Path, source.Stamp).ConfigureAwait(false) != null)
            return null;
        return new(provenance, details);
    }

    private static async Task<AutomaticReplay?> CombineAsync(ReplayCluster plan, IReadOnlyList<ClipDetails> details,
        ReplayOverlapOptions policy, Func<bool> allowed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!allowed()) return null;
        string destination = ClipNaming.AvailablePath(Path.GetDirectoryName(plan.Clips[0].Media.Path)!, "Combined replay", ".mp4");
        var job = ReplayCombine.Submit(plan, destination, policy.Limit, details,
            replacement: ReplayReplacementService.Current, automatic: true, automaticAllowed: allowed);
        using var cancellation = token.Register(() => MediaJobs.Current.Cancel(job.Id));
        var result = await job.Completion.ConfigureAwait(false);
        if (result.State != MediaJobState.Completed || result.Output == null || !allowed()) return null;
        return await ReadAsync(MediaSource.Capture(result.Output), token).ConfigureAwait(false);
    }
}
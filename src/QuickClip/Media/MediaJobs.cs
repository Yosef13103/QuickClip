using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Media;

internal enum MediaJobState { Queued, Running, Retrying, Validating, Completed, Failed, Cancelled }

internal sealed record MediaJobView(Guid Id, string Name, bool Share, MediaJobState State, double? Progress,
    TimeSpan Elapsed, TimeSpan? Remaining, string? Output, string? Message, bool CanRetry)
{
    internal bool Active => State is MediaJobState.Queued or MediaJobState.Running or MediaJobState.Retrying or MediaJobState.Validating;
}

internal sealed record MediaJobRequest(string Name, bool Share, IReadOnlyList<MediaSource> Sources, string Destination,
    Func<MediaJobContext, CancellationToken, Task<string>> Run)
{
    internal Func<string, CancellationToken, Task<string>>? Replace { get; init; }
}

internal sealed class MediaJobContext(Action<MediaJobState, double?> report, SemaphoreSlim hardware)
{
    internal void Progress(int attempt, double value) =>
        report(attempt == 1 ? MediaJobState.Running : MediaJobState.Retrying, value);

    internal void Validating() => report(MediaJobState.Validating, null);

    internal async Task<T> EncodeAsync<T>(string encoder, Func<Task<T>> work, CancellationToken ct)
    {
        if (VideoEncoders.Find(encoder)?.Hardware != true) return await work();
        await hardware.WaitAsync(ct);
        try { return await work(); }
        finally { hardware.Release(); }
    }
}

/// <summary>Bounded app-level queue. Each request owns its input and output reservations until it settles.</summary>
internal sealed class MediaJobs
{
    internal static MediaJobs Current { get; } = new(MediaOwnership.Current,
        () => AppSettings.Current.MediaJobConcurrency,
        () => Recorder.Instance.IsRunning);

    private const int MaxPending = 32;
    private const int MaxHistory = 30;
    private readonly object _gate = new();
    private readonly MediaOwnership _ownership;
    private readonly Func<int> _concurrency;
    private readonly Func<bool> _capturing;
    private readonly SemaphoreSlim _hardware = new(1);
    private readonly List<Entry> _entries = [];
    private int _running;
    private bool _stopping;

    internal MediaJobs(MediaOwnership ownership, Func<int> concurrency, Func<bool>? capturing = null) =>
        (_ownership, _concurrency, _capturing) = (ownership, concurrency, capturing ?? (() => false));

    internal bool Busy { get { lock (_gate) return _entries.Any(e => e.Active); } }

    internal (Guid Id, Task<MediaJobView> Completion) Submit(MediaJobRequest request)
    {
        request = request with { Destination = Path.GetFullPath(request.Destination), Sources = request.Sources.ToArray() };
        lock (_gate)
        {
            if (_stopping) throw new ExportException("QuickClip is closing.");
            if (_entries.Count(e => e.Active) >= MaxPending)
                throw new ExportException("The media queue is full. Cancel a job or wait for one to finish.");
            if (request.Replace == null && Path.Exists(request.Destination))
                throw new ExportException("The destination already exists. Choose a new filename.");
            var lease = _ownership.TryAcquire(request.Sources, [request.Destination]) ??
                throw new ExportException("A source or destination is reserved by another media operation.");
            var entry = new Entry(request, lease);
            _entries.Add(entry);
            Pump();
            return (entry.Id, entry.Completion.Task);
        }
    }

    internal MediaJobView[] Snapshot()
    {
        lock (_gate) return _entries.Select(e => e.View()).ToArray();
    }

    internal void Cancel(Guid id)
    {
        lock (_gate)
        {
            if (_entries.Find(e => e.Id == id && e.Active) is not { } entry) return;
            if (entry.State == MediaJobState.Queued) Finish(entry, MediaJobState.Cancelled);
            else _ = entry.Cancel.CancelAsync();
        }
    }

    internal void Retry(Guid id)
    {
        lock (_gate)
        {
            var entry = _entries.Find(e => e.Id == id && e.State is MediaJobState.Failed or MediaJobState.Cancelled);
            if (entry == null || entry.Request.Replace != null) return;
            Submit(entry.Request);
        }
    }

    internal async Task StopAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            _stopping = true;
            var active = _entries.Where(e => e.Active).ToArray();
            foreach (var entry in active) Cancel(entry.Id);
            pending = active.Select(e => e.Completion.Task).ToArray();
        }
        await Task.WhenAll(pending);
    }

    private void Pump()
    {
        int limit = _capturing() ? 1 : Math.Clamp(_concurrency(), 1, 2);
        while (!_stopping && _running < limit && _entries.Find(e => e.State == MediaJobState.Queued) is { } entry)
        {
            entry.State = MediaJobState.Running;
            entry.Started = Environment.TickCount64;
            _running++;
            _ = Task.Run(() => RunAsync(entry));
        }
    }

    private async Task RunAsync(Entry entry)
    {
        var ct = entry.Cancel.Token;
        var handles = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
        using var activity = BackgroundActivity.Begin(entry.Request.Share ? "Preparing share" : "Exporting clip");
        MediaJobState state = MediaJobState.Completed;
        try
        {
            foreach (var source in entry.Request.Sources)
            {
                ct.ThrowIfCancellationRequested();
                handles.Add(source.OpenVerified());
            }
            var context = new MediaJobContext((phase, value) =>
            {
                lock (_gate)
                {
                    long now = Environment.TickCount64;
                    if (phase == entry.State && value is > 0 and < 1 && now - entry.LastReport < 150) return;
                    if (phase != entry.State || value == 0) entry.PhaseStarted = now;
                    entry.LastReport = now;
                    entry.State = phase;
                    entry.Progress = value;
                }
                activity.Report(phase == MediaJobState.Validating ? "Validating media" :
                    phase == MediaJobState.Retrying ? "Retrying media encode" :
                    entry.Request.Share ? "Preparing share" : "Exporting clip", value);
            }, _hardware);
            string output = await entry.Request.Run(context, ct);
            if (entry.Request.Replace is { } replace)
            {
                foreach (var handle in handles) handle.Dispose();
                handles.Clear();
                ct.ThrowIfCancellationRequested();
                output = await replace(output, ct);
            }
            entry.Output = output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { state = MediaJobState.Cancelled; }
        catch (Exception ex)
        {
            state = MediaJobState.Failed;
            entry.Message = ex is ExportException ? ex.Message : "Could not process this media. Check the source and available disk space.";
            Log.Event(DiagnosticCategory.Editor, "Background media job failed", DiagnosticSeverity.Error);
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
            lock (_gate)
            {
                _running--;
                Finish(entry, state);
                Pump();
            }
        }
    }

    private void Finish(Entry entry, MediaJobState state)
    {
        entry.State = state;
        entry.Finished = Environment.TickCount64;
        entry.Reservation.Dispose();
        entry.Cancel.Dispose();
        entry.Completion.TrySetResult(entry.View());
        int excess = _entries.Count(e => !e.Active) - MaxHistory;
        if (excess > 0)
        {
            var expired = _entries.Where(e => !e.Active).OrderBy(e => e.Finished).Take(excess).ToArray();
            foreach (var item in expired) _entries.Remove(item);
        }
    }

    private sealed class Entry(MediaJobRequest request, IDisposable reservation)
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly MediaJobRequest Request = request;
        internal readonly IDisposable Reservation = reservation;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource<MediaJobView> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MediaJobState State;
        internal long Started, Finished, LastReport, PhaseStarted = Environment.TickCount64;
        internal double? Progress;
        internal string? Output, Message;
        internal bool Active => State is MediaJobState.Queued or MediaJobState.Running or MediaJobState.Retrying or MediaJobState.Validating;

        internal MediaJobView View()
        {
            long now = Environment.TickCount64;
            var elapsed = TimeSpan.FromMilliseconds(Started == 0 ? 0 : (Finished == 0 ? now : Finished) - Started);
            double phaseSeconds = (now - Math.Max(Started, PhaseStarted)) / 1000d;
            TimeSpan? remaining = Active && Progress is >= 0.1 and < 1 && phaseSeconds >= 3
                ? TimeSpan.FromSeconds(phaseSeconds * (1 / Progress.Value - 1)) : null;
            return new(Id, Request.Name, Request.Share, State, State == MediaJobState.Completed ? 1 : Progress,
                elapsed, remaining, Active ? null : Output, Active ? null : Message,
                Request.Replace == null && State is MediaJobState.Failed or MediaJobState.Cancelled);
        }
    }
}

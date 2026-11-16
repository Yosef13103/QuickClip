using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows.Threading;

namespace QuickClip;

/// <summary>Drains the existing bounded diagnostic buffer independently of the UI.</summary>
internal sealed class DevelopmentDiagnostics
{
    internal const long FileLimit = 1024 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    internal DevelopmentDiagnostics(DiagnosticBuffer buffer, string directory) =>
        _worker = Task.Run(() => DrainAsync(buffer, directory));

    internal Task StopAsync()
    {
        _stop.Cancel();
        return _worker;
    }

    private async Task DrainAsync(DiagnosticBuffer buffer, string directory)
    {
        string session = Guid.NewGuid().ToString("N");
        string build = typeof(Log).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        string profile = AppProfile.IsPersonal ? "Personal" : "Upstream";
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        DiagnosticCursor cursor = default;
        string path = Path.Combine(directory, "development.log");
        string header = $"{session} {DateTimeOffset.UtcNow:O} session start build={build} profile={profile}";
        bool start = true;
        while (true)
        {
            bool stopping = _stop.IsCancellationRequested;
            var batch = buffer.Read(ref cursor, incremental: true);
            if (start || batch != null)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    WriteBatch(path, session, header, start, batch);
                    start = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (stopping) break;
            try { await timer.WaitForNextTickAsync(_stop.Token); }
            catch (OperationCanceledException) { } // Drain once more for an orderly exit.
        }
    }

    private static void WriteBatch(string path, string session, string header, bool start, DiagnosticSnapshot? batch)
    {
        FileStream? file = null;
        long length = 0;
        try
        {
            if (start) Append(header);
            if (batch?.Dropped > 0) Append($"{session} events dropped={batch.Dropped}");
            if (batch != null)
                foreach (var entry in batch.Entries) Append($"{session} {entry.ToLine()}");
            file?.Flush(flushToDisk: true);
        }
        finally { file?.Dispose(); }

        void Append(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            if (file == null)
            {
                file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                length = file.Position;
            }
            if (length + bytes.Length > FileLimit)
            {
                file.Flush(flushToDisk: true);
                file.Dispose();
                file = null;
                File.Move(path, path + ".old", overwrite: true);
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                length = 0;
                if (line != header)
                {
                    byte[] startBytes = Encoding.UTF8.GetBytes(header + "\n");
                    file.Write(startBytes);
                    length = startBytes.Length;
                }
            }
            file.Write(bytes);
            length += bytes.Length;
        }
    }
}

internal enum DiagnosticOperationResult { Failed, Completed, Cancelled }

internal sealed class DiagnosticOperation : IDisposable
{
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DiagnosticCategory _category;
    private readonly string _operation;
    private DiagnosticOperationResult _result;
    private int? _code;
    private bool _disposed;

    internal DiagnosticOperation(DiagnosticCategory category, string operation)
    {
        _category = category;
        _operation = operation;
        Log.OperationEvent(category, $"{operation} id={_id} start");
    }

    internal void Complete() => _result = DiagnosticOperationResult.Completed;
    internal void Cancel() => _result = DiagnosticOperationResult.Cancelled;
    internal void Fail(int code) => _code = code;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        long elapsed = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        Log.OperationEvent(_category,
            FormattableString.Invariant($"{_operation} id={_id} {_result} elapsed_ms={elapsed}"),
            _result == DiagnosticOperationResult.Failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Information, _code);
    }
}


// One outstanding dispatcher message at a time. Pending asynchronous work keeps
// acknowledging the probe, while a blocked or saturated UI produces a warning.
internal sealed class DispatcherProbe : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Timer _timer;
    private long _queuedAt;
    private int _warned;
    private bool _disposed;

    internal DispatcherProbe(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Sample()
    {
        if (Volatile.Read(ref _disposed) || _dispatcher.HasShutdownStarted || !Log.Enabled) return;
        long now = Environment.TickCount64;
        long pending = Interlocked.CompareExchange(ref _queuedAt, now, 0);
        if (pending != 0)
        {
            if (now - pending >= 3000 && Interlocked.Exchange(ref _warned, 1) == 0)
                Log.Event(DiagnosticCategory.General, "UI dispatcher has not responded", DiagnosticSeverity.Warning);
            return;
        }
        _dispatcher.BeginInvoke(() =>
        {
            long queued = Interlocked.Exchange(ref _queuedAt, 0);
            if (Interlocked.Exchange(ref _warned, 0) != 0)
                Log.Event(DiagnosticCategory.General, "UI dispatcher responded", code: (int)Math.Min(int.MaxValue,
                    Environment.TickCount64 - queued));
        }, DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        _timer.Dispose();
    }
}

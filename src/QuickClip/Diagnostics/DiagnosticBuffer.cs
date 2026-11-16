namespace QuickClip;

// These values are also used by the native diagnostics callback.
internal enum DiagnosticCategory { General = 0, Capture = 1, GameDetection = 2, Library = 3, Audio = 4, Settings = 5, Editor = 6, Updater = 7 }
internal enum DiagnosticSeverity { Information, Warning, Error }

internal readonly record struct DiagnosticEntry(DateTimeOffset Utc, DiagnosticCategory Category,
    DiagnosticSeverity Severity, string Message, int? Code)
{
    public static string CategoryLabel(DiagnosticCategory category) => category switch
    {
        DiagnosticCategory.Capture => "Capture / Encoder",
        DiagnosticCategory.GameDetection => "Game detection",
        DiagnosticCategory.Library => "Library / Index",
        _ => category.ToString()
    };

    public string ToLine() => FormattableString.Invariant(
        $"{Utc:O} [{Severity}] [{CategoryLabel(Category)}] {Message}{(Code is int code ? $" (0x{code:X8})" : "")}");
}

internal readonly record struct DiagnosticCursor(long Session, long Count, long Contended);
internal sealed record DiagnosticSnapshot(DiagnosticEntry[] Entries, long Dropped)
{
    public IEnumerable<DiagnosticEntry> Filter(DiagnosticCategory? category, bool errorsOnly, string search) =>
        Entries.Where(entry => (category == null || entry.Category == category) &&
            (!errorsOnly || entry.Severity == DiagnosticSeverity.Error) &&
            (search.Length == 0 || entry.ToLine().Contains(search, StringComparison.OrdinalIgnoreCase)));

    public static void Export(string path, IReadOnlyList<DiagnosticEntry> entries)
    {
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
        foreach (DiagnosticEntry entry in entries) writer.WriteLine(entry.ToLine());
    }
}

/// <summary>A single bounded producer buffer and retained history. Viewers pull a batch when visible.</summary>
internal sealed class DiagnosticBuffer(int capacity = 2048) : IDisposable
{
    private readonly object _lifecycle = new();
    private Session? _session;
    private bool _disposed;
    private long _generation;

    public bool Enabled => Volatile.Read(ref _session) != null;

    public void SetEnabled(bool enabled)
    {
        lock (_lifecycle)
        {
            if (!enabled) Clear();
            else if (!_disposed && _session == null)
                Volatile.Write(ref _session, new Session(++_generation, capacity));
        }
    }

    public void Publish(DiagnosticCategory category, DiagnosticSeverity severity, string message, int? code = null,
        bool realtime = false) =>
        Volatile.Read(ref _session)?.Publish(category, severity, message, code, realtime);

    public DiagnosticSnapshot? Read(ref DiagnosticCursor cursor, bool incremental = false) =>
        Volatile.Read(ref _session)?.Read(ref cursor, incremental);

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            Clear();
        }
    }

    private void Clear() => Interlocked.Exchange(ref _session, null)?.Close();

    private sealed class Session(long generation, int capacity)
    {
        private readonly object _gate = new();
        private DiagnosticEntry[]? _entries = new DiagnosticEntry[capacity];
        private long _count;
        private long _contended;

        public void Publish(DiagnosticCategory category, DiagnosticSeverity severity, string message, int? code, bool realtime)
        {
            if (realtime)
            {
                if (!Monitor.TryEnter(_gate))
                {
                    Interlocked.Increment(ref _contended);
                    return;
                }
            }
            else Monitor.Enter(_gate);

            try
            {
                if (_entries == null) return;
                _entries[_count % capacity] = new(DateTimeOffset.UtcNow, category, severity, message, code);
                _count++;
            }
            finally { Monitor.Exit(_gate); }
        }

        public DiagnosticSnapshot? Read(ref DiagnosticCursor cursor, bool incremental)
        {
            lock (_gate)
            {
                long contended = Interlocked.Read(ref _contended);
                var current = new DiagnosticCursor(generation, _count, contended);
                if (_entries == null || cursor == current) return null;
                long previous = cursor.Session == generation && incremental ? cursor.Count : 0;
                long dropped = Math.Max(0, _count - previous - capacity) +
                    contended - (incremental && cursor.Session == generation ? cursor.Contended : 0);
                cursor = current;
                int count = (int)Math.Min(_count - previous, capacity);
                int first = (int)((_count - count) % capacity);
                int contiguous = Math.Min(count, capacity - first);
                var batch = new DiagnosticEntry[count];
                Array.Copy(_entries, first, batch, 0, contiguous);
                Array.Copy(_entries, 0, batch, contiguous, count - contiguous);
                return new(batch, dropped);
            }
        }

        public void Close()
        {
            lock (_gate) _entries = null;
        }
    }
}

using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Media;

internal sealed record MediaSource(string Path, FileStamp Stamp)
{
    internal static MediaSource Capture(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (ClipLibrary.IsMomentsPath(path))
            throw new ExportException("Choose media outside the Moments archive.");
        if (LibraryFiles.InspectPath(path, directory: false) != ClipAvailability.Available)
            throw new ExportException("The source is unavailable or linked through a reparse point.");
        using var handle = LibraryFiles.OpenMedia(path);
        return new(path, LibraryFiles.ReadStamp(handle));
    }

    internal void Verify()
    {
        using var handle = OpenVerified();
    }

    internal Microsoft.Win32.SafeHandles.SafeFileHandle OpenVerified()
    {
        if (ClipLibrary.IsMomentsPath(Path) || LibraryFiles.InspectPath(Path, directory: false) != ClipAvailability.Available)
            throw new ExportException("The source is unavailable or linked through a reparse point.");
        var handle = LibraryFiles.OpenMedia(Path);
        try
        {
            if (LibraryFiles.ReadStamp(handle) == Stamp) return handle;
            throw new ExportException("A source file changed. Select the current file and try again.");
        }
        catch { handle.Dispose(); throw; }
    }
}

/// <summary>Shared reads and exclusive mutations, reserved before a job enters the queue.</summary>
internal sealed class MediaOwnership
{
    internal static MediaOwnership Current { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _editorPins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _replayWriters = new(StringComparer.OrdinalIgnoreCase);

    // Editor pins fence replay replacement, not ordinary editor-owned
    // save-over-original or explicitly confirmed existing deletion workflows.
    internal IDisposable? TryPinEditor(MediaSource source)
    {
        var keys = Keys(source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            if (keys.Any(_replayWriters.Contains)) return null;
            foreach (string key in keys) _editorPins[key] = _editorPins.GetValueOrDefault(key) + 1;
        }
        return new Release(() =>
        {
            lock (_gate)
                foreach (string key in keys)
                    if (--_editorPins[key] == 0) _editorPins.Remove(key);
        });
    }

    internal IDisposable? TryAcquire(IEnumerable<MediaSource> sources, IEnumerable<string> destinations,
        bool protectEditorSources = false)
    {
        string[] writePaths = destinations.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var inputs = sources.ToArray();
        var writes = writePaths.SelectMany(DestinationKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] reads = inputs.SelectMany(Keys).Except(writes, StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            if (reads.Any(_writers.Contains) || writes.Any(key => _writers.Contains(key) || _readers.ContainsKey(key) ||
                protectEditorSources && _editorPins.ContainsKey(key)))
                return null;
            foreach (string key in reads) _readers[key] = _readers.GetValueOrDefault(key) + 1;
            foreach (string key in writes)
            {
                _writers.Add(key);
                if (protectEditorSources) _replayWriters.Add(key);
            }
        }
        return new Release(() =>
        {
            lock (_gate)
            {
                foreach (string key in reads)
                    if (--_readers[key] == 0) _readers.Remove(key);
                foreach (string key in writes)
                {
                    _writers.Remove(key);
                    if (protectEditorSources) _replayWriters.Remove(key);
                }
            }
        });
    }

    internal IDisposable? TryMutate(string path) => TryAcquire([], [path]);

    private static IEnumerable<string> DestinationKeys(string path)
    {
        if (ClipLibrary.IsMomentsPath(path)) throw new ExportException("Choose a location outside the Moments archive.");
        MediaProcessing.RequireSafeDirectory(Path.GetDirectoryName(path)!);
        yield return path;
        if (File.Exists(path) && MediaSource.Capture(path).Stamp.Identity is { } id)
            yield return id;
    }

    private static IEnumerable<string> Keys(MediaSource source)
    {
        yield return source.Path;
        if (source.Stamp.Identity is { } id) yield return id;
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

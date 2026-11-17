using QuickClip.Gallery;
using QuickClip.Library;
using System.Text.Json;

namespace QuickClip.Media;

internal static class MediaProcessing
{
    internal static MediaSource[] Sources(string path, IReadOnlyList<TrackExport>? tracks)
    {
        string[] sounds = tracks?.Where(track => track.SoundPath != null).Select(track => track.SoundPath!).ToArray() ?? [];
        return new[] { path }.Concat(sounds).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(MediaSource.Capture).ToArray();
    }

    internal static async Task<string> PublishAsync(string destination, string extension, ClipDetails? details,
        Func<string, Task> produce, CancellationToken ct)
    {
        using var staging = MediaStaging.Reserve(Path.GetDirectoryName(destination)!, extension);
        await produce(staging.Path);
        ct.ThrowIfCancellationRequested();
        File.Move(staging.Path, destination);
        if (details != null) ClipDetailsStore.TryWrite(destination, details);
        return destination;
    }

    internal static void RequireSafeDirectory(string directory)
    {
        if (ClipLibrary.IsMomentsPath(directory))
            throw new ExportException("Choose a location outside the Moments archive.");
        string? current = Path.GetFullPath(directory);
        while (current != null && LibraryFiles.InspectPath(current, directory: true) == ClipAvailability.Missing)
            current = Path.GetDirectoryName(current);
        if (current == null || LibraryFiles.InspectPath(current, directory: true) != ClipAvailability.Available)
            throw new ExportException("Choose an available directory without symbolic links or junctions.");
    }

    internal static void CheckFreeSpace(string directory, long expectedBytes)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (root == null || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
        long available = new DriveInfo(root).AvailableFreeSpace;
        if (available < expectedBytes + 16 * 1024 * 1024)
            throw new ExportException("The destination drive has too little free space for the staged export.");
    }
}

/// <summary>Tracks only QuickClip-created staging files. Recovery matches the same file identity, never file age.</summary>
internal sealed class MediaStaging(string path, string receipt) : IDisposable
{
    private static readonly Lazy<bool> Recovery = new(RecoverEntries);
    private static string Journal => System.IO.Path.Combine(AppProfile.LocalDirectory, "media-staging");
    internal string Path => path;

    private sealed record Receipt(string Path, long Created, string Identity);

    internal static MediaStaging Reserve(string directory, string extension)
    {
        Recover();
        MediaProcessing.RequireSafeDirectory(directory);
        Directory.CreateDirectory(Journal);
        string staged = ClipNaming.ReserveStagingPath(directory, extension);
        string receipt = System.IO.Path.Combine(Journal, System.IO.Path.GetFileNameWithoutExtension(staged) + ".json");
        try
        {
            using var handle = LibraryFiles.OpenMedia(staged);
            var stamp = LibraryFiles.ReadStamp(handle);
            if (stamp.Identity == null)
                throw new ExportException("Choose an export drive that can identify staged files.");
            using var stream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, new Receipt(staged, stamp.Created, stamp.Identity));
            stream.Flush(flushToDisk: true);
            return new(staged, receipt);
        }
        catch
        {
            File.Delete(staged);
            throw;
        }
    }

    public void Dispose() => Clean(receipt);

    internal static void Recover() => _ = Recovery.Value;

    private static bool RecoverEntries()
    {
        try
        {
            if (Directory.Exists(Journal))
                foreach (string receipt in Directory.EnumerateFiles(Journal, ".quickclip-tmp-*.json"))
                    Clean(receipt);
        }
        catch (Exception ex) when (LibraryFiles.IsAccessError(ex))
        { Log.Event(DiagnosticCategory.Editor, "Media staging recovery could not open its journal", DiagnosticSeverity.Warning); }
        return true;
    }

    private static void Clean(string receipt)
    {
        try
        {
            if (new FileInfo(receipt).Length > 16_384) return;
            using var json = File.OpenRead(receipt);
            var entry = JsonSerializer.Deserialize<Receipt>(json);
            json.Close();
            if (entry == null || !System.IO.Path.IsPathFullyQualified(entry.Path) || ClipLibrary.IsMomentsPath(entry.Path)) return;
            string name = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
            const string prefix = ".quickclip-tmp-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
                name != System.IO.Path.GetFileNameWithoutExtension(receipt)) return;
            var availability = LibraryFiles.InspectPath(entry.Path, directory: false);
            if (availability == ClipAvailability.Unavailable) return;
            if (availability == ClipAvailability.Available)
            {
                using var handle = LibraryFiles.OpenMedia(entry.Path);
                var stamp = LibraryFiles.ReadStamp(handle);
                if (stamp.Created != entry.Created || stamp.Identity != entry.Identity) return;
                handle.Dispose();
                File.Delete(entry.Path);
            }
            File.Delete(receipt);
        }
        catch (Exception ex) when (LibraryFiles.IsAccessError(ex) || ex is JsonException or ArgumentException)
        { Log.Event(DiagnosticCategory.Editor, "A media staging entry could not be cleaned", DiagnosticSeverity.Warning); }
    }
}

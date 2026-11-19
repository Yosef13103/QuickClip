using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickClip.Library;

internal sealed record DetailFields(string Title, string Game, bool Favorite, string[] Tags, string PendingTag);
internal sealed record DetailRecovery(DetailFields Baseline, DetailFields Draft);

/// <summary>Profile-local fallback when the primary save cannot finish before closing.</summary>
internal sealed record EditorRecovery(FileStamp Source, DetailRecovery? Details, string? PreviousEdit, ClipEditDraft? Edit)
{
    private const int MaxBytes = 256 * 1024;
    private static string RecoveryPath(FileStamp source)
    {
        string key = FormattableString.Invariant($"{source.Identity}:{source.Created}:{source.Modified}:{source.Bytes}");
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppProfile.LocalDirectory, "editor-recovery", name + ".json");
    }

    internal void Write(CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(this);
        if (data.Length > MaxBytes) throw new InvalidDataException("The recovery draft is too large.");
        string path = RecoveryPath(Source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(staged, path, overwrite: true);
        }
        finally { File.Delete(staged); }
    }

    // An unreadable or malformed unsaved recovery is still a reason not to consume media.
    internal static bool Exists(FileStamp source) => source.Identity != null &&
        LibraryFiles.InspectPath(RecoveryPath(source), false) != ClipAvailability.Missing;

    internal static EditorRecovery? Read(FileStamp source)
    {
        if (source.Identity == null) return null;
        string path = RecoveryPath(source);
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length <= MaxBytes && JsonSerializer.Deserialize<EditorRecovery>(stream) is { } recovery &&
                recovery.Source == source && recovery.Edit is not { IsValid: false }) return recovery;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return null;
    }

    internal static void Remove(FileStamp source)
    {
        try { File.Delete(RecoveryPath(source)); }
        catch (DirectoryNotFoundException) { }
    }
}

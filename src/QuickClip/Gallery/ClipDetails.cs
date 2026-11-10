using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickClip.Gallery;

internal sealed record ClipDetails(DateTimeOffset CapturedAt, string Game, string? Title = null);

/// <summary>Profile-local metadata for this milestone. The media and its filename remain independent.</summary>
internal static class ClipDetailsStore
{
    private sealed record Entry(long CreatedTicks, long ModifiedTicks, long Length, ClipDetails Details);

    private static string MetadataPath(FileInfo file) => Path.Combine(AppProfile.LocalDirectory, "clip-details",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.FullName.ToUpperInvariant()))) + ".json");

    public static ClipDetails Read(FileInfo file, string game, DateTimeOffset? embedded)
    {
        if (!AppProfile.IsPersonal) return new ClipDetails(new DateTimeOffset(file.CreationTime), game);
        string path = MetadataPath(file);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)) is { } entry &&
                entry.CreatedTicks == file.CreationTimeUtc.Ticks && entry.ModifiedTicks == file.LastWriteTimeUtc.Ticks &&
                entry.Length == file.Length && entry.Details is { Game.Length: > 0 } details && details.CapturedAt != default)
                return details;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("Could not read saved clip details: " + ex.Message);
        }
        DateTime fallback = file.CreationTimeUtc <= file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
        return new ClipDetails((embedded ?? new DateTimeOffset(fallback)).ToLocalTime(), game);
    }

    public static void Write(string mediaPath, ClipDetails details)
    {
        var file = new FileInfo(mediaPath);
        string path = MetadataPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staged, JsonSerializer.Serialize(new Entry(file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks, file.Length, details)));
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    public static void TryWrite(string path, ClipDetails details)
    {
        try { Write(path, details); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not save clip details: " + ex.Message);
        }
    }
}

using System.Text.Json;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed record DraftTrack(int Index, bool Include, double Volume, string Name);
internal sealed record DraftCrop(bool Enabled, double X, double Y, double Width, double Height, int Aspect);
internal sealed record ClipEditDraft(double Start, double End, double Position, bool Loop, DraftTrack[] Tracks, DraftCrop? Crop)
{
    public const int Version = 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Start) && double.IsFinite(End) && double.IsFinite(Position) &&
        Start >= 0 && End > Start && Position >= 0 &&
        Tracks is { Length: <= 128 } && Tracks.All(track => track is { Index: >= 0, Volume: >= 0 and <= 2, Name.Length: <= 256 }) &&
        Tracks.Select(track => track.Index).Distinct().Count() == Tracks.Length &&
        (Crop == null || double.IsFinite(Crop.X) && double.IsFinite(Crop.Y) &&
            double.IsFinite(Crop.Width) && double.IsFinite(Crop.Height) &&
            Crop.X >= 0 && Crop.Y >= 0 && Crop.Width > 0 && Crop.Height > 0 && Crop.Aspect >= 0);

    public string Serialize() => JsonSerializer.Serialize(this);
}

internal readonly record struct ClipDraftRange(FileStamp Source, double Start, double End);
internal sealed record StoredClipDraft(string Payload, ClipEditDraft? State);

internal sealed partial class LibraryIndex
{
    private const string MatchDraft = "file_id=$id AND created_ticks=$created AND modified_ticks=$modified AND bytes=$bytes";

    private static (string, object?)[] DraftKey(FileStamp stamp) =>
        [("$id", stamp.Identity), ("$created", stamp.Created), ("$modified", stamp.Modified), ("$bytes", stamp.Bytes)];

    internal Task<StoredClipDraft?> ReadEditDraftAsync(string path, FileStamp expected) =>
        Run<StoredClipDraft?>(default, _ =>
        {
            using var source = OpenDraftSource(path, expected);
            using var connection = _database.Open();
            using var query = Command(connection, null,
                $"SELECT version,payload FROM edit_drafts WHERE {MatchDraft};", DraftKey(expected));
            using var reader = query.ExecuteReader();
            if (!reader.Read()) return null;
            if (reader.GetInt32(0) != ClipEditDraft.Version)
                throw new InvalidDataException("This edit draft was saved by a newer version of QuickClip.");
            string payload = reader.GetString(1);
            ClipEditDraft? state = null;
            try
            {
                if (payload.Length <= 128 * 1024 && JsonSerializer.Deserialize<ClipEditDraft>(payload) is { IsValid: true } parsed)
                    state = parsed;
            }
            catch (JsonException) { }
            return new(payload, state);
        });

    internal Task SaveEditDraftAsync(string path, FileStamp expected, string? previous, ClipEditDraft? draft) =>
        Run(default, _ =>
        {
            // Hold a read lease during the transaction, fencing a concurrent source replacement.
            using var source = OpenDraftSource(path, expected);
            if (draft is { IsValid: false }) throw new ArgumentException("The edit draft is invalid.");
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            var key = DraftKey(expected);
            using (var query = Command(connection, transaction,
                $"SELECT version,payload FROM edit_drafts WHERE {MatchDraft};", key))
            using (var reader = query.ExecuteReader())
            {
                bool exists = reader.Read();
                if ((exists ? reader.GetString(1) : null) != previous ||
                    (exists && reader.GetInt32(0) != ClipEditDraft.Version))
                    throw new LibraryConflictException("The saved edits changed. Reopen the clip to load them.");
            }

            if (draft == null)
                Execute(connection, transaction, $"DELETE FROM edit_drafts WHERE {MatchDraft};", key);
            else
                Execute(connection, transaction, """
                    INSERT INTO edit_drafts(file_id,created_ticks,modified_ticks,bytes,version,start,end,payload)
                    VALUES($id,$created,$modified,$bytes,$version,$start,$end,$payload)
                    ON CONFLICT DO UPDATE SET version=excluded.version,start=excluded.start,end=excluded.end,payload=excluded.payload;
                    """, [.. key, ("$version", ClipEditDraft.Version), ("$start", draft.Start), ("$end", draft.End), ("$payload", draft.Serialize())]);
            transaction.Commit();
            return true;
        });

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDraftSource(string path, FileStamp expected)
    {
        if (expected.Identity == null || ClipLibrary.IsMomentsPath(path) ||
            LibraryFiles.InspectPath(path, directory: false) != ClipAvailability.Available)
            throw new IOException("A stable source identity is unavailable for this clip.");
        var handle = LibraryFiles.OpenMedia(path);
        try
        {
            if (LibraryFiles.ReadStamp(handle) != expected)
                throw new IOException("The source file changed. Reopen the clip before saving its edits.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // The Gallery already has the cheap file facts. Resolve native identities only for
    // cards that might have a draft, with one bounded SQL query per page of candidates.
    internal Task<Dictionary<string, ClipDraftRange>> ReadDraftRangesAsync(IReadOnlyList<ClipItem> clips, CancellationToken token) =>
        Run(token, cancellation =>
        {
            var ranges = new Dictionary<string, ClipDraftRange>(StringComparer.OrdinalIgnoreCase);
            if (clips.Count == 0 || !File.Exists(_database.PathName)) return ranges;
            using var connection = _database.Open();
            foreach (var batch in clips.Chunk(256))
            {
                cancellation.ThrowIfCancellationRequested();
                string stamps = JsonSerializer.Serialize(batch.Select(clip =>
                    new[] { clip.File.CreationTimeUtc.Ticks, clip.File.LastWriteTimeUtc.Ticks, clip.Size }));
                using var candidates = Command(connection, null, """
                    SELECT DISTINCT stamp.key FROM json_each($stamps) stamp JOIN edit_drafts draft
                    ON draft.created_ticks=stamp.value ->> 0 AND draft.modified_ticks=stamp.value ->> 1 AND draft.bytes=stamp.value ->> 2
                    WHERE draft.version=$version;
                    """, ("$stamps", stamps), ("$version", ClipEditDraft.Version));
                using var rows = candidates.ExecuteReader();
                while (rows.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var clip = batch[rows.GetInt32(0)];
                    if (ClipLibrary.IsMomentsPath(clip.Path)) continue;
                    try
                    {
                        using var file = LibraryFiles.OpenMedia(clip.Path);
                        var stamp = LibraryFiles.ReadStamp(file);
                        using var query = Command(connection, null,
                            $"SELECT start,end FROM edit_drafts WHERE {MatchDraft} AND version={ClipEditDraft.Version};", DraftKey(stamp));
                        using var range = query.ExecuteReader();
                        if (!range.Read()) continue;
                        double start = range.GetDouble(0), end = range.GetDouble(1);
                        if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start)
                            ranges[clip.Path] = new(stamp, start, end);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            return ranges;
        });
}

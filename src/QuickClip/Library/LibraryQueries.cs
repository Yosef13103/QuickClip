using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    private const string EffectiveAvailability = "CASE WHEN r.availability=2 THEN 2 ELSE c.availability END";

    public Task<ImmutableArray<IndexedClip>> QueryClipsAsync(ClipQuery query, CancellationToken cancellationToken = default) =>
        Run(cancellationToken, token =>
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.Limit is < 1 or > 200 || query.Offset < 0 ||
                query.MinDuration < TimeSpan.Zero || query.MaxDuration < TimeSpan.Zero ||
                query.MinDuration > query.MaxDuration || query.CapturedFrom > query.CapturedBefore ||
                (query.Category is { } category && !Enum.IsDefined(category)) ||
                (query.Availability is { } availability && !Enum.IsDefined(availability)))
                throw new ArgumentOutOfRangeException(nameof(query));

            string order = query.Sort switch
            {
                ClipSort.CapturedAt => "c.captured_ticks", ClipSort.Game => "c.game_key",
                ClipSort.Title => "c.title_key", ClipSort.Duration => "c.duration_ticks",
                ClipSort.Category => "r.category", _ => throw new ArgumentOutOfRangeException(nameof(query))
            };
            var filters = new List<string>();
            var values = new List<(string, object?)>();
            void Filter(string condition, string name, object? value)
            {
                if (value is null) return;
                filters.Add(condition);
                values.Add((name, value));
            }

            Filter("(instr(c.title_key,$search)>0 OR instr(c.game_key,$search)>0)", "$search", QueryKey(query.Search));
            Filter("c.game_key=$game", "$game", QueryKey(query.Game));
            Filter("c.game_id=$gameId", "$gameId", query.GameId);
            Filter("""
                EXISTS(SELECT 1 FROM clip_tags ct JOIN tags t ON t.id=ct.tag_id WHERE ct.clip_id=c.id AND t.name_key=$tag)
                """, "$tag", QueryKey(query.Tag));
            Filter("c.root_id=$root", "$root", query.RootId?.ToString("N"));
            Filter("r.category=$category", "$category", (int?)query.Category);
            Filter($"({EffectiveAvailability})=$availability", "$availability", (int?)query.Availability);
            Filter("c.favorite=$favorite", "$favorite", query.Favorite is bool favorite ? (favorite ? 1 : 0) : null);
            Filter("c.captured_ticks >= $from", "$from", query.CapturedFrom?.UtcTicks);
            Filter("c.captured_ticks < $before", "$before", query.CapturedBefore?.UtcTicks);
            Filter("c.duration_ticks >= $min", "$min", query.MinDuration?.Ticks);
            Filter("c.duration_ticks <= $max", "$max", query.MaxDuration?.Ticks);
            values.Add(("$limit", query.Limit));
            values.Add(("$offset", query.Offset));
            string where = filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : "";
            string direction = query.Descending ? "DESC" : "ASC";

            using var connection = _database.Open();
            using var command = Command(connection, null, $"""
                SELECT c.id,c.root_id,c.path,r.category,c.captured_ticks,c.captured_offset,c.date_source,c.game,c.title,
                    c.game_id,c.duration_ticks,c.bytes,{EffectiveAvailability},c.legacy_metadata,c.favorite,c.revision,c.original_id,c.session_id
                FROM clips c JOIN roots r ON r.id=c.root_id
                {where} ORDER BY {order} {direction},c.id LIMIT $limit OFFSET $offset;
                """, values.ToArray());
            using var reader = command.ExecuteReader();
            var rows = ImmutableArray.CreateBuilder<IndexedClip>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                rows.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                    (MediaCategory)reader.GetInt32(3), ReadDate(reader, 4, 5), (CaptureDateSource)reader.GetInt32(6),
                    reader.GetString(7), reader.GetString(8), ReadText(reader, 9),
                    reader.IsDBNull(10) ? null : TimeSpan.FromTicks(reader.GetInt64(10)), reader.GetInt64(11),
                    (ClipAvailability)reader.GetInt32(12), (LegacyMetadataStatus)reader.GetInt32(13),
                    reader.GetBoolean(14), reader.GetInt64(15), ReadGuid(reader, 16), ReadGuid(reader, 17)));
            }
            token.ThrowIfCancellationRequested();
            return rows.ToImmutable();
        });

    private static string? QueryKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : LibraryFiles.TextKey(RequiredText(value, 256, nameof(value)));

    private static string? ReadText(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
    private static Guid? ReadGuid(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : Guid.Parse(reader.GetString(column));
    private static DateTimeOffset ReadDate(SqliteDataReader reader, int ticks, int offset) =>
        new DateTimeOffset(reader.GetInt64(ticks), TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(reader.GetInt32(offset)));

    public Task<StorageInventory> GetStorageInventoryAsync(CancellationToken cancellationToken = default) =>
        Run(cancellationToken, token =>
        {
            ImmutableArray<LibraryRoot> roots;
            var groups = ImmutableArray.CreateBuilder<StorageGroup>();
            var volumeIds = new Dictionary<Guid, string>();
            long known, unknown;
            int missing, unavailable, withoutExport;
            using (var connection = _database.Open())
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                roots = ReadRoots(connection, transaction);
                using (var command = Command(connection, transaction, "SELECT id,volume FROM roots WHERE volume IS NOT NULL;"))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) volumeIds.Add(Guid.Parse(reader.GetString(0)), reader.GetString(1));

                using (var command = Command(connection, transaction, $"""
                    SELECT r.id,r.category,min(c.game),count(*),sum(c.bytes)
                    FROM clips c JOIN roots r ON r.id=c.root_id WHERE ({EffectiveAvailability})=0
                    GROUP BY r.id,r.category,c.game_key ORDER BY r.id,c.game_key;
                    """))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        groups.Add(new(Guid.Parse(reader.GetString(0)), (MediaCategory)reader.GetInt32(1),
                            reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4)));
                    }

                using (var command = Command(connection, transaction, $"""
                    WITH available AS (
                        SELECT c.file_id,c.created_ticks,c.bytes FROM clips c JOIN roots r ON r.id=c.root_id
                        WHERE ({EffectiveAvailability})=0
                    )
                    SELECT
                        (SELECT coalesce(sum(bytes),0) FROM (
                            SELECT max(bytes) AS bytes FROM available WHERE file_id IS NOT NULL GROUP BY file_id,created_ticks)),
                        (SELECT coalesce(sum(bytes),0) FROM available WHERE file_id IS NULL),
                        (SELECT count(*) FROM clips c JOIN roots r ON r.id=c.root_id WHERE ({EffectiveAvailability})=1),
                        (SELECT count(*) FROM clips c JOIN roots r ON r.id=c.root_id WHERE ({EffectiveAvailability})=2),
                        (SELECT count(*) FROM clips c JOIN roots r ON r.id=c.root_id
                            WHERE r.category=$original AND ({EffectiveAvailability})=0
                            AND NOT EXISTS(SELECT 1 FROM clips e JOIN roots er ON er.id=e.root_id
                                WHERE e.original_id=c.id AND er.category=$export AND e.availability=0 AND er.availability=0));
                    """, ("$original", (int)MediaCategory.QuickClipOriginal), ("$export", (int)MediaCategory.Export)))
                using (var reader = command.ExecuteReader())
                {
                    reader.Read();
                    known = reader.GetInt64(0);
                    unknown = reader.GetInt64(1);
                    missing = reader.GetInt32(2);
                    unavailable = reader.GetInt32(3);
                    withoutExport = reader.GetInt32(4);
                }
                transaction.Commit();
            }

            var space = ImmutableArray.CreateBuilder<RootSpace>();
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                long? bytes = null;
                if (root.Availability == ClipAvailability.Available &&
                    LibraryFiles.InspectPath(root.Directory, directory: true) == ClipAvailability.Available)
                {
                    try
                    {
                        if (!volumeIds.TryGetValue(root.Id, out var volume) || volume == LibraryFiles.RootVolume(root.Directory))
                            bytes = new DriveInfo(Path.GetPathRoot(root.Directory)!).AvailableFreeSpace;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                space.Add(new(root.Id, bytes));
            }
            return new StorageInventory(groups.ToImmutable(), known, unknown, missing, unavailable, withoutExport, space.ToImmutable());
        });
}

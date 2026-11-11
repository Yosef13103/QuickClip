using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

/// <summary>Profile-local catalog. All filesystem and SQLite work runs outside the caller's UI context.</summary>
internal sealed partial class LibraryIndex
{
    private readonly LibraryDatabase _database;
    private readonly string? _legacyDetails;
    private readonly Func<string, ClipHeader> _readHeader;

    public LibraryIndex(string databasePath, string? legacyDetailsDirectory = null, Func<string, ClipHeader>? readHeader = null)
    {
        _database = new(databasePath);
        if (legacyDetailsDirectory != null && !Path.IsPathFullyQualified(legacyDetailsDirectory))
            throw new ArgumentException("Use an absolute metadata directory.", nameof(legacyDetailsDirectory));
        _legacyDetails = legacyDetailsDirectory;
        _readHeader = readHeader ?? Mp4Info.Read;
    }

    public static LibraryIndex ForCurrentProfile()
    {
        if (!AppProfile.IsPersonal) throw new InvalidOperationException("The library index is available in QuickClip Personal.");
        return new(Path.Combine(AppProfile.LocalDirectory, "library", "clips.sqlite3"),
            Path.Combine(AppProfile.LocalDirectory, "clip-details"));
    }

    public Task<Guid> RegisterRootAsync(string directory, MediaCategory category = MediaCategory.ImportedFinished,
        IEnumerable<string>? exclusions = null, CancellationToken cancellationToken = default) =>
        Run(cancellationToken, token =>
        {
            if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
            string path = LibraryFiles.RootPath(directory);
            var excluded = (exclusions ?? []).Take(129).Select(LibraryFiles.RelativePath).ToImmutableArray();
            if (excluded.Length > 128) throw new ArgumentException("Too many directory exclusions.", nameof(exclusions));
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var existing in ReadRoots(connection, transaction))
            {
                if (path.Equals(existing.Directory, StringComparison.OrdinalIgnoreCase))
                {
                    if (category != existing.Category || !excluded.SequenceEqual(existing.ExcludedDirectories, StringComparer.OrdinalIgnoreCase))
                        throw new LibraryConflictException("The directory is already registered with different settings.");
                    return existing.Id;
                }
                if (LibraryFiles.Within(path, existing.Directory) && !LibraryFiles.IsExcluded(existing.Directory, path, existing.ExcludedDirectories) ||
                    LibraryFiles.Within(existing.Directory, path) && !LibraryFiles.IsExcluded(path, existing.Directory, excluded))
                    throw new ArgumentException("Library roots must not index overlapping directories.", nameof(directory));
            }
            var id = Guid.NewGuid();
            Execute(connection, transaction, """
                INSERT INTO roots(id,path,path_key,category,exclusions) VALUES($id,$path,$key,$category,$exclusions);
                """, ("$id", Id(id)), ("$path", path), ("$key", LibraryFiles.PathKey(path)),
                ("$category", (int)category), ("$exclusions", JsonSerializer.Serialize(excluded)));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return id;
        });

    public Task<ImmutableArray<LibraryRoot>> GetRootsAsync(CancellationToken cancellationToken = default) =>
        Run(cancellationToken, _ =>
        {
            using var connection = _database.Open();
            return ReadRoots(connection, null);
        });

    public Task BackupAsync(string destination, CancellationToken cancellationToken = default) =>
        Run(cancellationToken, _ => { _database.Backup(destination); return true; });

    public Task<LibraryScanResult> ReconcileAsync(Guid rootId, LibraryScanOptions? options = null,
        CancellationToken cancellationToken = default) => Run(cancellationToken, token => Reconcile(rootId, options ?? new(), token));

    private static Task<T> Run<T>(CancellationToken token, Func<CancellationToken, T> action) =>
        Task.Run(() => { token.ThrowIfCancellationRequested(); return action(token); }, token);

    private static string Id(Guid value) => value.ToString("N");

    private static ImmutableArray<LibraryRoot> ReadRoots(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,path,category,exclusions,availability,scanned_ticks FROM roots ORDER BY path_key;");
        using var reader = command.ExecuteReader();
        var roots = ImmutableArray.CreateBuilder<LibraryRoot>();
        while (reader.Read()) roots.Add(ReadRoot(reader));
        return roots.ToImmutable();
    }

    private static LibraryRoot ReadRoot(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), (MediaCategory)reader.GetInt32(2),
        JsonSerializer.Deserialize<ImmutableArray<string>>(reader.GetString(3)), (ClipAvailability)reader.GetInt32(4),
        reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero));

    private sealed record Existing(string Id, string Path, string Key, FileStamp Stamp, ClipAvailability Availability);
    private sealed record Observation(string Path, FileStamp Stamp, ClipHeader? Header, LegacyRead? Legacy);

    private LibraryScanResult Reconcile(Guid id, LibraryScanOptions options, CancellationToken token)
    {
        if (options.MaxEntries is < 1 or > 250_000 || options.MaxDepth is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options));
        using var connection = _database.Open();
        LibraryRoot root;
        long revision;
        string? priorVolume;
        using (var command = Command(connection, null,
            "SELECT id,path,category,exclusions,availability,scanned_ticks,scan_revision,volume FROM roots WHERE id=$id;", ("$id", Id(id))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new KeyNotFoundException("Library root is not registered.");
            root = ReadRoot(reader);
            revision = reader.GetInt64(6);
            priorVolume = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        string scope = options.RelativeDirectory == null ? root.Directory :
            Path.Combine(root.Directory, LibraryFiles.RelativePath(options.RelativeDirectory));
        if (LibraryFiles.IsExcluded(root.Directory, scope, root.ExcludedDirectories))
            throw new ArgumentException("The selected scan scope is excluded.", nameof(options));

        string? volume = null;
        bool online = LibraryFiles.InspectPath(root.Directory, directory: true) == ClipAvailability.Available;
        if (online)
        {
            online = new DriveInfo(Path.GetPathRoot(root.Directory)!).DriveType is DriveType.Fixed or DriveType.Removable;
            if (online) volume = LibraryFiles.RootVolume(root.Directory);
            online = online && (priorVolume == null || volume == priorVolume);
        }
        if (!online) return Unavailable();

        var existing = LoadExisting(connection, id, token);
        var seen = new Dictionary<string, Observation>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        int visited = 0, headerReads = 0, errors = 0;
        bool complete = true;

        void Visit(string directory, int depth)
        {
            if (LibraryFiles.InspectPath(directory, directory: true) != ClipAvailability.Available)
            {
                complete = false;
                errors++;
                return;
            }
            try
            {
                using var enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                while (visited < options.MaxEntries && enumerator.MoveNext())
                {
                    token.ThrowIfCancellationRequested();
                    visited++;
                    string path = enumerator.Current;
                    if (LibraryFiles.IsExcluded(root.Directory, path, root.ExcludedDirectories)) continue;
                    string key = LibraryFiles.PathKey(path);
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if (LibraryFiles.ShouldSkip(attributes)) continue;
                        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if (LibraryFiles.IsUnavailable(attributes))
                        {
                            if (!isDirectory && LibraryFiles.EligibleFile(path)) unreadable.Add(key);
                            continue;
                        }
                        if (isDirectory)
                        {
                            if (depth == options.MaxDepth) complete = false;
                            else Visit(path, depth + 1);
                            continue;
                        }
                        if (!LibraryFiles.EligibleFile(path)) continue;

                        // Hold a read handle while taking facts and reading the header.
                        using var handle = LibraryFiles.OpenMedia(path);
                        var stamp = LibraryFiles.ReadStamp(handle);
                        if (stamp.Bytes == 0) { unreadable.Add(key); continue; }
                        if (existing.TryGetValue(key, out var previous) && previous.Stamp == stamp)
                        {
                            seen[key] = new(path, stamp, null, null);
                            continue;
                        }
                        var header = _readHeader(path);
                        headerReads++;
                        var legacy = previous != null && previous.Stamp.SameOccupant(stamp)
                            ? null : LegacyLibraryMetadata.Read(_legacyDetails, path, stamp);
                        seen[key] = new(path, stamp, header, legacy);
                    }
                    catch (Exception ex) when (LibraryFiles.IsAccessError(ex))
                    {
                        unreadable.Add(key);
                        complete = false;
                    }
                }
                if (visited >= options.MaxEntries) complete = false;
            }
            catch (Exception ex) when (LibraryFiles.IsAccessError(ex)) { complete = false; errors++; }
        }

        var scopeState = LibraryFiles.InspectPath(scope, directory: true);
        if (scopeState == ClipAvailability.Available) Visit(scope, 0);
        else if (scopeState == ClipAvailability.Unavailable) { complete = false; errors++; }

        token.ThrowIfCancellationRequested();
        if (LibraryFiles.InspectPath(root.Directory, directory: true) != ClipAvailability.Available ||
            (volume != null && LibraryFiles.RootVolume(root.Directory) != volume))
            return Unavailable();

        // Assign in-place occupants first. An old path must be confirmed absent or replaced before
        // an unambiguous file identity can transfer an ID to a new path.
        var matches = new Dictionary<string, Existing>(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, observation) in seen)
            if (existing.TryGetValue(key, out var old) && old.Stamp.SameOccupant(observation.Stamp))
            {
                matches.Add(key, old);
                assigned.Add(old.Id);
            }
        var identityMatches = existing.Values.Where(x => x.Stamp.Identity != null)
            .ToLookup(x => (x.Stamp.Identity, x.Stamp.Created));
        var newIdentityCounts = seen.Where(x => !matches.ContainsKey(x.Key) && x.Value.Stamp.Identity != null)
            .GroupBy(x => (x.Value.Stamp.Identity, x.Value.Stamp.Created)).ToDictionary(x => x.Key, x => x.Count());
        foreach (var (key, observation) in seen)
        {
            token.ThrowIfCancellationRequested();
            var identity = (observation.Stamp.Identity, observation.Stamp.Created);
            if (matches.ContainsKey(key) || newIdentityCounts.GetValueOrDefault(identity) != 1) continue;
            var candidates = identityMatches[identity];
            if (candidates.Count() != 1) continue;
            var old = candidates.Single();
            if (!assigned.Contains(old.Id) && !unreadable.Contains(old.Key) &&
                (seen.TryGetValue(old.Key, out var atOldPath) ? !old.Stamp.SameOccupant(atOldPath.Stamp)
                    : LibraryFiles.InspectPath(old.Path, directory: false) == ClipAvailability.Missing))
            {
                matches.Add(key, old);
                assigned.Add(old.Id);
            }
        }

        var missing = new List<Existing>();
        if (complete)
            foreach (var old in existing.Values)
            {
                token.ThrowIfCancellationRequested();
                if (assigned.Contains(old.Id) || seen.ContainsKey(old.Key) || unreadable.Contains(old.Key) ||
                    !LibraryFiles.Within(old.Path, scope)) continue;
                switch (LibraryFiles.InspectPath(old.Path, directory: false))
                {
                    case ClipAvailability.Missing: missing.Add(old); break;
                    case ClipAvailability.Unavailable: unreadable.Add(old.Key); break;
                }
            }

        using (var transaction = connection.BeginTransaction())
        {
            SaveRootState(connection, transaction, id, revision, ClipAvailability.Available, volume);
            // Vacate changed paths first so rename swaps and replacements are independent of traversal order.
            foreach (var (key, _) in seen)
            {
                token.ThrowIfCancellationRequested();
                if (existing.TryGetValue(key, out var old) && matches.GetValueOrDefault(key)?.Id != old.Id)
                    Execute(connection, transaction, "UPDATE clips SET path_key=NULL,availability=1 WHERE id=$id;", ("$id", old.Id));
            }
            foreach (var (key, observation) in seen)
            {
                token.ThrowIfCancellationRequested();
                ApplyObservation(connection, transaction, root, key, observation, matches.GetValueOrDefault(key));
            }
            int markedMissing = 0;
            foreach (var old in missing)
                markedMissing += Execute(connection, transaction,
                    "UPDATE clips SET availability=1 WHERE id=$id AND availability<>1;", ("$id", old.Id));
            foreach (string key in unreadable)
                if (existing.TryGetValue(key, out var old) && !assigned.Contains(old.Id))
                    Execute(connection, transaction, "UPDATE clips SET availability=2 WHERE id=$id;", ("$id", old.Id));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(id, visited, seen.Count, headerReads, markedMissing, unreadable.Count + errors, complete, ClipAvailability.Available);
        }

        LibraryScanResult Unavailable()
        {
            token.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            SaveRootState(connection, transaction, id, revision, ClipAvailability.Unavailable, null);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(id, 0, 0, 0, 0, 0, false, ClipAvailability.Unavailable);
        }
    }

    private static Dictionary<string, Existing> LoadExisting(SqliteConnection connection, Guid rootId, CancellationToken token)
    {
        using var command = Command(connection, null, """
            SELECT id,path,path_key,created_ticks,modified_ticks,bytes,file_id,availability FROM clips
            WHERE root_id=$root AND path_key IS NOT NULL;
            """, ("$root", Id(rootId)));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, Existing>(StringComparer.Ordinal);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string key = reader.GetString(2);
            result.Add(key, new(reader.GetString(0), reader.GetString(1), key,
                new(reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6)),
                (ClipAvailability)reader.GetInt32(7)));
        }
        return result;
    }

    private static void SaveRootState(SqliteConnection connection, SqliteTransaction transaction, Guid rootId,
        long revision, ClipAvailability availability, string? volume)
    {
        if (Execute(connection, transaction, """
            UPDATE roots SET availability=$state,scanned_ticks=$now,volume=coalesce(volume,$volume),scan_revision=scan_revision+1
            WHERE id=$id AND scan_revision=$revision;
            """, ("$state", (int)availability), ("$now", DateTime.UtcNow.Ticks), ("$volume", volume),
            ("$id", Id(rootId)), ("$revision", revision)) != 1)
            throw new LibraryConflictException("The library root was reconciled by another scan. Retry using the current index.");
    }

    private static void ApplyObservation(SqliteConnection connection, SqliteTransaction transaction, LibraryRoot root,
        string key, Observation observation, Existing? existing)
    {
        if (existing is { Availability: ClipAvailability.Available } && existing.Path == observation.Path &&
            existing.Stamp == observation.Stamp) return;
        string title = Path.GetFileNameWithoutExtension(observation.Path);
        string game = ClipLibrary.InitialGame(root.Directory, observation.Path);
        var stamp = observation.Stamp;
        var duration = observation.Header?.Duration;
        long? ticks = duration is >= 0 && double.IsFinite(duration.Value) &&
            duration < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(duration.Value).Ticks : null;
        if (existing != null)
        {
            Execute(connection, transaction, """
                UPDATE clips SET path=$path,path_key=$key,file_id=$identity,created_ticks=$created,
                    modified_ticks=$modified,bytes=$bytes,availability=0,
                    duration_ticks=CASE WHEN $read=1 THEN $duration ELSE duration_ticks END,
                    title=CASE WHEN title_edited=0 THEN $title ELSE title END,
                    title_key=CASE WHEN title_edited=0 THEN $titleKey ELSE title_key END,
                    game=CASE WHEN game_edited=0 THEN $game ELSE game END,
                    game_key=CASE WHEN game_edited=0 THEN $gameKey ELSE game_key END
                WHERE id=$id;
                """, ("$path", observation.Path), ("$key", key), ("$identity", stamp.Identity), ("$created", stamp.Created),
                ("$modified", stamp.Modified), ("$bytes", stamp.Bytes), ("$read", observation.Header.HasValue ? 1 : 0),
                ("$duration", ticks), ("$title", title), ("$titleKey", LibraryFiles.TextKey(title)),
                ("$game", game), ("$gameKey", LibraryFiles.TextKey(game)), ("$id", existing.Id));
            return;
        }

        var legacy = observation.Legacy?.Details;
        var captured = legacy?.CapturedAt ?? observation.Header?.CaptureTime ??
            new DateTimeOffset(Math.Min(stamp.Created, stamp.Modified), TimeSpan.Zero);
        var source = legacy != null ? CaptureDateSource.LegacyDetails :
            observation.Header?.CaptureTime != null ? CaptureDateSource.Embedded : CaptureDateSource.FileTime;
        title = legacy?.Title ?? title;
        game = legacy?.Game ?? game;
        string clipId = Id(Guid.NewGuid());
        Execute(connection, transaction, """
            INSERT INTO clips(id,root_id,path,path_key,file_id,created_ticks,modified_ticks,bytes,captured_ticks,captured_offset,
                date_source,duration_ticks,availability,legacy_metadata,title,title_key,game,game_key,title_edited,game_edited)
            VALUES($id,$root,$path,$key,$identity,$created,$modified,$bytes,$captured,$offset,$source,$duration,0,
                $legacy,$title,$titleKey,$game,$gameKey,$titleEdited,$gameEdited);
            """, ("$id", clipId), ("$root", Id(root.Id)), ("$path", observation.Path), ("$key", key),
            ("$identity", stamp.Identity), ("$created", stamp.Created), ("$modified", stamp.Modified), ("$bytes", stamp.Bytes),
            ("$captured", captured.UtcTicks), ("$offset", (int)captured.Offset.TotalMinutes), ("$source", (int)source),
            ("$duration", ticks), ("$legacy", (int)(observation.Legacy?.Status ?? LegacyMetadataStatus.None)),
            ("$title", title), ("$titleKey", LibraryFiles.TextKey(title)), ("$game", game), ("$gameKey", LibraryFiles.TextKey(game)),
            ("$titleEdited", legacy?.Title != null ? 1 : 0), ("$gameEdited", legacy != null ? 1 : 0));
        if (legacy != null)
            WriteGalleryAnnotations(connection, transaction, clipId, legacy.Normalize(), ticks);
    }
}

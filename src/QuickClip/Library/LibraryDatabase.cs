using Microsoft.Data.Sqlite;

namespace QuickClip.Library;

internal sealed class LibraryDatabase
{
    internal const int SchemaVersion = 6;
    private const int ApplicationId = 0x51434C49; // QCLI
    private readonly string _connectionString;
    private readonly object _initializationGate = new();
    private bool _openedOnce;

    public LibraryDatabase(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute database path.", nameof(path));
        PathName = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = PathName, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true,
            DefaultTimeout = 5, Pooling = false
        }.ToString();
    }

    public string PathName { get; }

    public SqliteConnection Open()
    {
        if (Volatile.Read(ref _openedOnce)) return OpenCore();
        // SQLite starts in rollback-journal mode. Concurrent initialization and
        // WAL negotiation can contend before either caller gets its transaction.
        lock (_initializationGate)
        {
            var connection = OpenCore();
            Volatile.Write(ref _openedOnce, true);
            return connection;
        }
    }

    private SqliteConnection OpenCore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            if (ReadVersion(connection, null) != SchemaVersion) Initialize(connection);
            if (!string.Equals(Scalar(connection, null, "PRAGMA journal_mode;") as string, "wal", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Scalar(connection, null, "PRAGMA journal_mode=WAL;") as string, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The library database could not enable WAL mode.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static int ReadVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT application_id,user_version FROM pragma_application_id(),pragma_user_version();");
        using var reader = command.ExecuteReader();
        reader.Read();
        long id = reader.GetInt64(0);
        int version = reader.GetInt32(1);
        if (version < 0 || version > SchemaVersion || (id != ApplicationId && (id != 0 || version != 0)))
            throw new InvalidDataException("Unsupported QuickClip library database.");
        return version;
    }

    private static void Initialize(SqliteConnection connection)
    {
        using (var transaction = connection.BeginTransaction())
        {
            int version = ReadVersion(connection, transaction);
            if (version == 0)
            {
                if (Convert.ToInt64(Scalar(connection, transaction,
                    "SELECT count(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*';")) != 0)
                    throw new InvalidDataException("The database belongs to another application.");
                Execute(connection, transaction, BaseSchema);
                Execute(connection, transaction, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;");
                version = 1;
            }
            if (version == 1)
            {
                Execute(connection, transaction, AnnotationSchema);
                Execute(connection, transaction, "PRAGMA user_version=2;");
                version = 2;
            }
            if (version == 2)
            {
                Execute(connection, transaction, DraftSchema);
                Execute(connection, transaction, "PRAGMA user_version=3;");
                version = 3;
            }
            if (version == 3)
            {
                Execute(connection, transaction, OrganizationSchema);
                Execute(connection, transaction, "PRAGMA user_version=4;");
                version = 4;
            }
            if (version == 4)
            {
                // B has an independently numbered v4. Do not pretend its schema
                // is E1; integration needs an explicit migration for both layouts.
                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM pragma_table_info('tags') WHERE name='color';")) != 1 ||
                    Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM pragma_table_info('clips') WHERE name='hidden';")) != 1)
                    throw new InvalidDataException("This preview requires the E1 library schema. Keep the replay-consolidation database separate.");
                Execute(connection, transaction, CollectionSchema);
                Execute(connection, transaction, "PRAGMA user_version=5;");
                version = 5;
            }
            if (version == 5)
            {
                Execute(connection, transaction, ScopedTagSchema);
                Execute(connection, transaction, "PRAGMA user_version=6;");
            }
            transaction.Commit();
        }
    }

    public void Backup(string destination)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Use an absolute backup path.", nameof(destination));
        string output = Path.GetFullPath(destination);
        if (output.Equals(PathName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a separate backup path.", nameof(destination));
        using var source = Open();
        using (new FileStream(output, FileMode.CreateNew, FileAccess.Write)) { }
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = output, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5
        }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    // E1 draft migration. Reconcile with B's independently numbered replay
    // schema before either feature uses a production database.
    private const string OrganizationSchema = """
        ALTER TABLE clips ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0 CHECK(hidden IN (0,1));
        CREATE INDEX clips_hidden ON clips(hidden,captured_ticks DESC,id);
        ALTER TABLE tags ADD COLUMN color TEXT;
        """;

    private const string CollectionSchema = """
        CREATE TABLE collections (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL,
            game_id TEXT NOT NULL DEFAULT '', color TEXT, folder_leaf TEXT, folder_key TEXT,
            CHECK((folder_leaf IS NULL) = (folder_key IS NULL)),
            UNIQUE(game_id,name_key)
        );
        CREATE TABLE clip_collections (
            clip_id TEXT PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE,
            collection_id TEXT NOT NULL REFERENCES collections(id)
        ) WITHOUT ROWID;
        CREATE INDEX clip_collections_reverse ON clip_collections(collection_id,clip_id);
        """;

    // Preserve existing IDs, colors and memberships; all preexisting tags
    // remain global. AUTOINCREMENT prevents stale UI IDs from being reused.
    private const string ScopedTagSchema = """
        CREATE TABLE tags_v6 (
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, name_key TEXT NOT NULL,
            game_id TEXT NOT NULL DEFAULT '', color TEXT, UNIQUE(game_id,name_key)
        );
        INSERT INTO tags_v6(id,name,name_key,color) SELECT id,name,name_key,color FROM tags;
        CREATE TABLE clip_tags_v6 (
            clip_id TEXT NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags_v6(id), PRIMARY KEY(clip_id,tag_id)
        );
        INSERT INTO clip_tags_v6(clip_id,tag_id) SELECT clip_id,tag_id FROM clip_tags;
        DROP TABLE clip_tags;
        DROP TABLE tags;
        ALTER TABLE tags_v6 RENAME TO tags;
        ALTER TABLE clip_tags_v6 RENAME TO clip_tags;
        CREATE INDEX clip_tags_reverse ON clip_tags(tag_id,clip_id);
        CREATE INDEX tags_name_scope ON tags(name_key,game_id);
        """;

    private const string DraftSchema = """
        CREATE TABLE edit_drafts (
            file_id TEXT NOT NULL, created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
            bytes INTEGER NOT NULL, version INTEGER NOT NULL, start REAL NOT NULL, end REAL NOT NULL, payload TEXT NOT NULL,
            PRIMARY KEY (created_ticks, modified_ticks, bytes, file_id)
        ) WITHOUT ROWID;
        """;

    internal const string BaseSchema = """
        CREATE TABLE roots (
            id TEXT PRIMARY KEY, path TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE,
            category INTEGER NOT NULL, exclusions TEXT NOT NULL,
            availability INTEGER NOT NULL DEFAULT 2, scanned_ticks INTEGER,
            scan_revision INTEGER NOT NULL DEFAULT 0, volume TEXT
        );
        CREATE TABLE clips (
            id TEXT PRIMARY KEY, root_id TEXT NOT NULL REFERENCES roots(id),
            path TEXT NOT NULL, path_key TEXT, file_id TEXT,
            created_ticks INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, bytes INTEGER NOT NULL CHECK(bytes >= 0),
            captured_ticks INTEGER NOT NULL, captured_offset INTEGER NOT NULL, date_source INTEGER NOT NULL,
            duration_ticks INTEGER CHECK(duration_ticks >= 0),
            availability INTEGER NOT NULL, legacy_metadata INTEGER NOT NULL,
            title TEXT NOT NULL, title_key TEXT NOT NULL, game TEXT NOT NULL, game_key TEXT NOT NULL,
            title_edited INTEGER NOT NULL DEFAULT 0, game_edited INTEGER NOT NULL DEFAULT 0,
            game_id TEXT, original_id TEXT REFERENCES clips(id), session_id TEXT,
            favorite INTEGER NOT NULL DEFAULT 0 CHECK(favorite IN (0,1)), revision INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX clips_path ON clips(root_id, path_key);
        CREATE INDEX clips_identity ON clips(root_id, file_id, created_ticks);
        CREATE INDEX clips_captured ON clips(captured_ticks DESC, id);
        CREATE INDEX clips_game ON clips(game_key, id);
        CREATE INDEX clips_title ON clips(title_key, id);
        CREATE INDEX clips_duration ON clips(duration_ticks, id);
        CREATE INDEX clips_favorite ON clips(favorite, captured_ticks DESC, id);
        CREATE INDEX clips_original ON clips(original_id);
        CREATE INDEX clips_session ON clips(session_id);
        CREATE INDEX clips_game_id ON clips(game_id);
        """;

    private const string AnnotationSchema = """
        CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE);
        CREATE TABLE clip_tags (
            clip_id TEXT NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags(id),
            PRIMARY KEY(clip_id,tag_id)
        );
        CREATE INDEX clip_tags_reverse ON clip_tags(tag_id,clip_id);
        CREATE TABLE bookmarks (
            id TEXT PRIMARY KEY, clip_id TEXT NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
            offset_ticks INTEGER NOT NULL CHECK(offset_ticks >= 0), label TEXT, origin INTEGER NOT NULL,
            session_id TEXT, captured_ticks INTEGER, captured_offset INTEGER
        );
        CREATE INDEX bookmarks_clip ON bookmarks(clip_id,offset_ticks,id);
        """;
}

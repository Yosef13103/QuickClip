using Microsoft.Data.Sqlite;

namespace QuickClip.Library;

internal sealed class LibraryDatabase
{
    internal const int SchemaVersion = 2;
    private const int ApplicationId = 0x51434C49; // QCLI
    private readonly string _connectionString;

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

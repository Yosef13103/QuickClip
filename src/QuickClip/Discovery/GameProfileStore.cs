using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QuickClip.Discovery;

internal sealed record GameProfileDocument
{
    internal const int CurrentVersion = 2;
    public int SchemaVersion { get; init; } = CurrentVersion;
    public ImmutableDictionary<string, GameRecordingPolicy> Profiles { get; init; } =
        ImmutableDictionary<string, GameRecordingPolicy>.Empty;

    public GameRecordingPolicy Resolve(string? profileId, GameRecordingPolicy defaults) =>
        (profileId == null ? new() : Profiles.GetValueOrDefault(profileId) ?? new()).ResolveAgainst(defaults);

    public void Validate()
    {
        if (SchemaVersion != CurrentVersion || Profiles == null || Profiles.Count > 256)
            throw new ArgumentException("Invalid recording profile document.");
        foreach (var (id, policy) in Profiles)
        {
            if (!GameRecordingPolicy.IsId(id, 128) || policy == null)
                throw new ArgumentException("Invalid recording profile entry.");
            policy.Validate();
        }
    }
}

internal enum GameProfileLoadStatus { Missing, Loaded, Migrated, Invalid, UnsupportedVersion, Unavailable }
internal sealed record GameProfileLoadResult(GameProfileLoadStatus Status, GameProfileDocument Document);

/// <summary>Explicit local profile persistence. Loading never changes the source file.</summary>
internal sealed class GameProfileStore
{
    private const int MaxFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public GameProfileStore(string settingsDirectory)
    {
        if (!Path.IsPathFullyQualified(settingsDirectory))
            throw new ArgumentException("An absolute profile settings directory is required.", nameof(settingsDirectory));
        FilePath = Path.Combine(Path.GetFullPath(settingsDirectory), "game-recording-profiles.json");
    }

    public static GameProfileStore ForCurrentProfile() => new(AppProfile.SettingsDirectory);
    public string FilePath { get; }

    public GameProfileLoadResult Load()
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return new(GameProfileLoadStatus.Invalid, new());
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return new(GameProfileLoadStatus.Invalid, new());

            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) is not JsonObject root ||
                root["schemaVersion"] is not JsonValue versionNode || !versionNode.TryGetValue(out int version))
                return new(GameProfileLoadStatus.Invalid, new());

            if (version is not (1 or GameProfileDocument.CurrentVersion))
                return new(GameProfileLoadStatus.UnsupportedVersion, new());
            if (version == 1) MigrateVersion1(root);
            var document = root.Deserialize<GameProfileDocument>(JsonOptions) ?? throw new JsonException();
            document.Validate();
            return new(version == 1 ? GameProfileLoadStatus.Migrated : GameProfileLoadStatus.Loaded, document);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(GameProfileLoadStatus.Missing, new());
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(GameProfileLoadStatus.Invalid, new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(GameProfileLoadStatus.Unavailable, new());
        }
    }

    public void Save(GameProfileDocument document)
    {
        document.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("Recording profile document is too large.", nameof(document));

        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".game-profiles-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void MigrateVersion1(JsonObject root)
    {
        // Version 1 is the draft import format, with bufferSeconds instead of replaySeconds.
        if (root["profiles"] is not JsonObject profiles) throw new JsonException();
        foreach (var profile in profiles)
        {
            if (profile.Value is not JsonObject fields) throw new JsonException();
            if (!fields.Remove("bufferSeconds", out var duration)) continue;
            if (fields.ContainsKey("replaySeconds")) throw new JsonException();
            fields.Add("replaySeconds", duration);
        }
        root["schemaVersion"] = GameProfileDocument.CurrentVersion;
    }
}

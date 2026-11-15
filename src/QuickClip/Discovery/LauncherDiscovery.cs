using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace QuickClip.Discovery;

internal static partial class GameCatalogService
{
    internal sealed partial class Scan
    {
        private IEnumerable<ScanStep> ScanLocalLauncher(CatalogSource source)
        {
            var records = _localInstalls?.Invoke(source) ?? source switch
            {
                CatalogSource.Xbox => ReadXbox(DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed)
                    .Select(drive => drive.Name)),
                CatalogSource.Heroic => ReadHeroic(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                CatalogSource.Origin => ReadOrigin(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
                CatalogSource.Amazon => ReadAmazon(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                CatalogSource.Itch => ReadItch(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                CatalogSource.BattleNet => ReadBattleNet(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
                _ => LocalLaunchers.ReadRegistry(source),
            };
            foreach (var record in records)
            {
                _cancel.ThrowIfCancellationRequested();
                _visited++;
                _relevantSteps++;
                if (record?.Issue is { } issue)
                    AddIssue(LocalLaunchers.Name(source), issue);
                else if (record != null)
                    AdmitLauncherInstall(source, record);
                yield return default;
            }
        }

        private void AdmitLauncherInstall(CatalogSource source, LauncherInstall record)
        {
            string? path = CatalogPaths.LocalPath(record.Directory);
            string? name = string.IsNullOrWhiteSpace(record.Name) ? Path.GetFileName(path) : record.Name.Trim();
            if (path == null || IsSystemRoot(path) || record.Id is not { Length: > 0 and <= 200 } id || id.Any(char.IsControl) ||
                name is not { Length: > 0 and <= 160 } || name.Any(char.IsControl))
            {
                AddIssue(LocalLaunchers.Name(source), CatalogIssueKind.InvalidManifest);
                return;
            }
            if (ApprovedRoot(path) is not { } install) return;
            var game = GetCandidate($"{source.ToString().ToLowerInvariant()}:{id.ToLowerInvariant()}", name, source);
            if (!game.InstallDirectories.Add(install)) return;
            string? GetExecutable(string? relative)
            {
                if (relative == null || Path.IsPathRooted(relative) ||
                    CatalogPaths.LocalPath(Path.Combine(install, relative)) is not { } exe ||
                    !CatalogPaths.IsWithin(exe, install) || !CatalogPaths.IsExecutable(exe) ||
                    !MetadataExists(exe)) return null;
                return exe;
            }
            if (GetExecutable(record.Launcher) is { } launcher)
            {
                game.DetectedLaunchers.Add(launcher);
                game.Executables.Add(launcher);
            }
            string? renderer = GetExecutable(record.Renderer);
            _launcherInstalls.Enqueue(() => ScanInstall(install, 0, game, renderer, allowFolderFallback: true,
                requireInstallNamedFallback: true));
        }

        private static bool IsSystemRoot(string path)
        {
            if (Path.GetDirectoryName(path) is not { Length: 3 }) return false;
            return Path.GetFileName(path).ToUpperInvariant() is "WINDOWS" or "USERS" or "PROGRAMDATA" or "PROGRAM FILES" or "PROGRAM FILES (X86)";
        }

        private IEnumerable<LauncherInstall?> ReadBattleNet(string programData)
        {
            string? path = CatalogPaths.LocalPath(programData) is { } root
                ? Path.Combine(root, @"Battle.net\Agent\product.db") : null;
            bool agent = path != null && MetadataExists(path);
            foreach (var record in LocalLaunchers.ReadRegistry(CatalogSource.BattleNet))
                if (!agent || record?.Issue != CatalogIssueKind.SourceMissing) yield return record;
            if (agent && ReadMetadata(path!, 4 * 1024 * 1024) is { } bytes)
                foreach (var record in BattleNetAgentDatabase.Read(bytes)) yield return record;
        }

        internal IEnumerable<LauncherInstall?> ReadXbox(IEnumerable<string> drives)
        {
            bool present = false;
            foreach (string drive in drives)
            {
                _cancel.ThrowIfCancellationRequested();
                var libraries = new HashSet<string>(Paths);
                string modifiable = Path.Combine(drive, "Program Files", "ModifiableWindowsApps");
                if (MetadataExists(modifiable, directory: true)) libraries.Add(modifiable);
                string marker = Path.Combine(drive, ".GamingRoot");
                if (MetadataExists(marker))
                {
                    present = true;
                    var bytes = ReadMetadata(marker, 128 * 1024);
                    if (bytes != null && ParseGamingRoot(bytes) is { } folders)
                        libraries.UnionWith(folders.Select(folder => Path.Combine(drive, folder)));
                    else if (bytes != null)
                        yield return new(Issue: CatalogIssueKind.InvalidManifest);
                }
                foreach (string library in libraries)
                {
                    present = true;
                    if (ApprovedRoot(library) == null) continue;
                    foreach (var entry in VisitEntries(library, count: false))
                    {
                        if (entry is not { IsDirectory: true } install)
                        {
                            yield return null;
                            continue;
                        }
                        yield return ReadXboxInstall(install.Path);
                    }
                }
            }
            if (!present) yield return new(Issue: CatalogIssueKind.SourceMissing);
        }

        internal static string[]? ParseGamingRoot(byte[] bytes)
        {
            if (bytes.Length < 10 || (bytes.Length & 1) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x58424752) return null;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            if (count is 0 or >= 255) return null;
            var paths = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).Split('\0');
            if (paths.Length != count + 1 || paths[^1].Length != 0) return null;
            var folders = paths[..^1].Select(path => path.TrimStart('\\', '/')).ToArray();
            return folders.All(path => path.Length <= 260 && path.Split('\\', '/').All(CatalogPaths.SafeLeaf)) ? folders : null;
        }

        private LauncherInstall? ReadXboxInstall(string install)
        {
            foreach (string directory in new[] { install, Path.Combine(install, "Content") })
            foreach (string name in new[] { "MicrosoftGame.config", "AppxManifest.xml" })
            {
                string path = Path.Combine(directory, name);
                if (!MetadataExists(path)) continue;
                var bytes = ReadMetadata(path);
                if (bytes != null) return ParseXboxManifest(bytes, directory);
                return new(Issue: CatalogIssueKind.Unavailable);
            }
            return null;
        }

        internal static LauncherInstall ParseXboxManifest(byte[] bytes, string directory)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 1_048_576 });
                var root = XDocument.Load(reader).Root!;
                XNamespace ns = root.Name.Namespace;
                bool gdk = root.Name.LocalName == "Game";
                if (!gdk && root.Name.LocalName != "Package") return new(Issue: CatalogIssueKind.InvalidManifest);
                string? id = (string?)root.Element(ns + "Identity")?.Attribute("Name");
                // AppX also permits launcher and proxy applications. Keep those manifests as metadata,
                // with normal independent renderer checks for the readable install.
                string? name = gdk ? (string?)root.Element(ns + "ShellVisuals")?.Attribute("DefaultDisplayName") :
                    (string?)root.Element(ns + "Properties")?.Element(ns + "DisplayName");
                string? exe = null;
                if (gdk)
                {
                    var executables = root.Element(ns + "ExecutableList")?.Elements(ns + "Executable")
                        .Where(element => (string?)element.Attribute("TargetDeviceFamily") is null or "PC")
                        .Where(element => (string?)element.Attribute("IsDevOnly") is not ("true" or "1"))
                        .Select(element => (string?)element.Attribute("Name")).Distinct().Take(2).ToArray();
                    if (executables is [var single]) exe = single;
                }
                return new(id, name?.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) == true
                    ? Path.GetFileName(directory) : name, directory, exe);
            }
            catch (XmlException) { return new(Issue: CatalogIssueKind.InvalidManifest); }
        }

        internal IEnumerable<LauncherInstall?> ReadHeroic(string appData)
        {
            if (CatalogPaths.LocalPath(appData) is not { } root) yield break;
            bool present = false;
            foreach (var (relative, gog) in new[]
            {
                (@"heroic\gog_store\installed.json", true),
                (@"heroic\legendaryConfig\legendary\installed.json", false),
            })
            {
                string path = Path.Combine(root, relative);
                if (!MetadataExists(path)) continue;
                present = true;
                var bytes = ReadMetadata(path, 4 * 1024 * 1024);
                if (bytes == null) continue;
                foreach (var record in ParseHeroic(bytes, gog)) yield return record;
            }
            if (!present && !MetadataExists(Path.Combine(root, "heroic"), directory: true))
                yield return new(Issue: CatalogIssueKind.SourceMissing);
        }

        internal static IEnumerable<LauncherInstall?> ParseHeroic(byte[] bytes, bool gog)
        {
            JsonDocument? document;
            try { document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 }); }
            catch (JsonException) { document = null; }
            using (document)
            {
                if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root)
                    yield return new(Issue: CatalogIssueKind.InvalidManifest);
                else if (gog)
                {
                    if (!root.TryGetProperty("installed", out var entries) || entries.ValueKind != JsonValueKind.Array)
                        yield return new(Issue: CatalogIssueKind.InvalidManifest);
                    else
                        foreach (var entry in entries.EnumerateArray()) yield return ParseHeroicRecord(entry, null, gog);
                }
                else
                    foreach (var entry in root.EnumerateObject()) yield return ParseHeroicRecord(entry.Value, entry.Name, gog);
            }
        }

        private static LauncherInstall? ParseHeroicRecord(JsonElement entry, string? key, bool gog)
        {
            if (entry.ValueKind != JsonValueKind.Object) return new(Issue: CatalogIssueKind.InvalidManifest);
            string? Read(string field) => entry.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
            if (entry.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True) return null;
            if (Read("platform") is { } platform && !platform.Equals("Windows", StringComparison.OrdinalIgnoreCase)) return null;
            string? id = gog ? Read("appName") : key;
            if (string.IsNullOrWhiteSpace(id) || (gog && !GogRegistry.TryProductId(id, out _)))
                return new(Issue: CatalogIssueKind.InvalidManifest);
            // The Heroic executable is launch configuration, so verify the renderer independently.
            return new((gog ? "gog:" : "epic:") + id, Read("title"), Read("install_path"));
        }

        internal IEnumerable<LauncherInstall?> ReadOrigin(string programData)
        {
            if (CatalogPaths.LocalPath(programData) is not { } root) yield break;
            string directory = Path.Combine(root, "Origin", "LocalContent");
            if (!MetadataExists(directory, directory: true))
            {
                yield return new(Issue: CatalogIssueKind.SourceMissing);
                yield break;
            }
            foreach (var file in VisitEntries(directory, name => name.EndsWith(".mfst", StringComparison.OrdinalIgnoreCase), count: false))
                yield return file is { } entry && ReadMetadata(entry.Path) is { } bytes ? ParseOrigin(bytes) : null;
        }

        internal static LauncherInstall? ParseOrigin(byte[] bytes)
        {
            var fields = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? bytes.AsSpan(3) : bytes));
            string? id = fields.GetValues("id")?.FirstOrDefault();
            if (id?.EndsWith("@steam", StringComparison.OrdinalIgnoreCase) == true) return null;
            string? path = fields.GetValues("dipInstallPath")?.MaxBy(value => value.Length);
            return new(id, null, path);
        }

        internal IEnumerable<LauncherInstall?> ReadItch(string appData)
        {
            if (CatalogPaths.LocalPath(appData) is not { } root) yield break;
            string directory = Path.Combine(root, "itch", "apps");
            if (!MetadataExists(directory, directory: true))
            {
                yield return new(Issue: CatalogIssueKind.SourceMissing);
                yield break;
            }
            foreach (var item in VisitEntries(directory, count: false))
            {
                if (item is not { IsDirectory: true } install)
                {
                    yield return null;
                    continue;
                }
                string path = Path.Combine(install.Path, ".itch", "receipt.json.gz");
                yield return MetadataExists(path) && ReadMetadata(path) is { } bytes ? ParseItch(bytes, install.Path) : null;
            }
        }

        internal static LauncherInstall? ParseItch(byte[] bytes, string install)
        {
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                byte[] buffer = new byte[8192];
                int count;
                while ((count = gzip.Read(buffer)) != 0)
                {
                    if (decoded.Length + count > 4 * 1024 * 1024) return new(Issue: CatalogIssueKind.InvalidManifest);
                    decoded.Write(buffer, 0, count);
                }
                decoded.Position = 0;
                using var document = JsonDocument.Parse(decoded, new() { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.Object ||
                    !game.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                    !id.TryGetInt64(out long number) || number <= 0)
                    return new(Issue: CatalogIssueKind.InvalidManifest);
                string? title = game.TryGetProperty("title", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                return new(number.ToString(System.Globalization.CultureInfo.InvariantCulture), title, install);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                return new(Issue: CatalogIssueKind.InvalidManifest);
            }
        }

        internal IEnumerable<LauncherInstall?> ReadAmazon(string localAppData)
        {
            if (CatalogPaths.LocalPath(localAppData) is not { } root) yield break;
            string path = Path.Combine(root, @"Amazon Games\Data\Games\Sql\GameInstallInfo.sqlite");
            if (!MetadataExists(path))
            {
                yield return new(Issue: CatalogIssueKind.SourceMissing);
                yield break;
            }
            var bytes = ReadMetadata(path, 8 * 1024 * 1024);
            // A memory snapshot cannot include live WAL or rollback data.
            if (bytes == null || MetadataExists(path + "-wal") || MetadataExists(path + "-journal") ||
                bytes is not { Length: >= 100 } || bytes[18] != 1 || bytes[19] != 1)
            {
                yield return new(Issue: CatalogIssueKind.Unavailable);
                yield break;
            }
            foreach (var entry in ParseAmazon(bytes)) yield return entry;
        }

        internal static IReadOnlyList<LauncherInstall> ParseAmazon(byte[] bytes)
        {
            if (!bytes.AsSpan().StartsWith("SQLite format 3\0"u8))
                return [new(Issue: CatalogIssueKind.InvalidManifest)];
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                using var db = new SqliteConnection("Data Source=:memory:;Pooling=False;Default Timeout=1");
                db.Open();
                const int readOnly = 4; // SQLITE_DESERIALIZE_READONLY
                if (raw.sqlite3_deserialize(db.Handle, "main", buffer, bytes.Length, bytes.Length, readOnly) != raw.SQLITE_OK)
                    return [new(Issue: CatalogIssueKind.InvalidManifest)];
                using var query = db.CreateCommand();
                query.CommandText = "PRAGMA trusted_schema=OFF";
                query.ExecuteNonQuery();
                query.CommandText = "SELECT sql FROM main.sqlite_schema WHERE type='table' AND name='DbSet'";
                if (query.ExecuteScalar() is not string schema || !schema.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                    return [new(Issue: CatalogIssueKind.InvalidManifest)];
                query.CommandText = "SELECT Id, ProductTitle, InstallDirectory FROM main.DbSet WHERE Installed = 1 LIMIT 20001";
                using var rows = query.ExecuteReader();
                var installs = new List<LauncherInstall>();
                while (rows.Read())
                {
                    string? Read(int index) => rows.GetValue(index) as string;
                    if (installs.Count == 20_000)
                    {
                        installs.Add(new(Issue: CatalogIssueKind.Incomplete));
                        break;
                    }
                    installs.Add(new(Read(0), Read(1), Read(2)));
                }
                return installs;
            }
            catch (SqliteException) { return [new(Issue: CatalogIssueKind.InvalidManifest)]; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private bool MetadataExists(string path, bool directory = false)
        {
            if (IsExcluded(path)) return false;
            var parents = new Stack<string>();
            for (string? parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
                parents.Push(parent);
            try
            {
                foreach (string parent in parents)
                {
                    _cancel.ThrowIfCancellationRequested();
                    if (!File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) continue;
                    AddIssue(parent, CatalogIssueKind.Unavailable);
                    return false;
                }
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    AddIssue(path, CatalogIssueKind.Unavailable);
                    return false;
                }
                return attributes.HasFlag(FileAttributes.Directory) == directory;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue(path, CatalogIssueKind.Unavailable); return false; }
        }

        private byte[]? ReadMetadata(string path, int limit = 1024 * 1024)
        {
            if (!CheckDirectory(Path.GetDirectoryName(path)!) || !MetadataExists(path)) return null;
            try
            {
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length <= limit)
                {
                    var bytes = new byte[(int)file.Length];
                    file.ReadExactly(bytes);
                    if (file.ReadByte() == -1) return bytes;
                }
                AddIssue(path, CatalogIssueKind.InvalidManifest);
            }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue(path, CatalogIssueKind.Unavailable); }
            return null;
        }
    }
}

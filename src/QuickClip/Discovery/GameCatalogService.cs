using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QuickClip.Discovery;

/// <summary>One explicitly requested catalog scan. No background polling or executable inspection.</summary>
internal static class GameCatalogService
{
    public static Task<CatalogResult> ScanAsync(CatalogRequest request, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null) =>
        Task.Run(() => new Scan(request, cancellationToken, progress).Run(), cancellationToken);

    private sealed class Scan
    {
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        private static readonly HashSet<string> IgnoredNames = new(Paths)
        {
            ".git", "node_modules", "$Recycle.Bin", "System Volume Information",
            "_CommonRedist", "_Redist", "redist", "redistributables"
        };
        private const int ManifestBudget = 8 * SteamManifestParser.MaxDocumentSize;
        private readonly CatalogRequest _request;
        private readonly CancellationToken _cancel;
        private readonly IProgress<int>? _progress;
        private readonly string[] _excluded;
        private readonly Dictionary<string, Candidate> _games = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Candidate> _steamFolders = new(Paths);
        private readonly List<CatalogIssue> _issues = [];
        private int _visited, _manifestBytes;
        private bool _limitReached;

        public Scan(CatalogRequest request, CancellationToken cancel, IProgress<int>? progress)
        {
            if (request.MaxEntries is < 1 or > 100_000 || request.MaxDepth is < 0 or > 16 ||
                request.GameRoots.Length + request.SteamLibraryRoots.Length > 128)
                throw new ArgumentException("Invalid catalog scan limits.", nameof(request));
            _request = request;
            _cancel = cancel;
            _progress = progress;
            _excluded = request.ExcludedDirectories.Select(path => CatalogPaths.LocalPath(path)
                ?? throw new ArgumentException("Excluded directories must be absolute local drive paths.", nameof(request))).ToArray();
        }

        public CatalogResult Run()
        {
            // Launcher identities take precedence when both sources name the same install folder.
            var steamRoots = new HashSet<string>(Paths);
            foreach (var raw in _request.SteamLibraryRoots)
            {
                if (!CanVisitMore()) break;
                if (ApprovedRoot(raw) is { } library && steamRoots.Add(library))
                    ScanSteam(library);
            }

            var gameRoots = new HashSet<string>(Paths);
            foreach (var root in _request.GameRoots)
            {
                if (!CanVisitMore()) break;
                if (ApprovedRoot(root.Path) is not { } path || !gameRoots.Add(path)) continue;
                if (_steamFolders.ContainsKey(path)) continue;
                if (root.IsCollection) ScanCollection(path);
                else ScanFolder(path);
            }

            foreach (var manual in _request.ManualEntries)
            {
                _cancel.ThrowIfCancellationRequested();
                AddManual(manual);
            }

            var rules = new Dictionary<string, CatalogRules>(StringComparer.Ordinal);
            foreach (var entry in _request.Overrides)
                if (!string.IsNullOrWhiteSpace(entry.Id)) rules[entry.Id] = entry.Rules;

            var entries = _games.Values.Select(game => game.ToEntry(rules.GetValueOrDefault(game.Id) ?? new()))
                .OrderBy(game => game.DisplayName, Paths).ThenBy(game => game.Id, StringComparer.Ordinal)
                .ToImmutableArray();
            _cancel.ThrowIfCancellationRequested();
            return new(entries, [.. _issues], _visited, _limitReached, DateTimeOffset.UtcNow);
        }

        private void ScanSteam(string library)
        {
            string steamApps = Path.Combine(library, "steamapps");
            if (!CheckDirectory(steamApps)) return;
            string common = Path.Combine(steamApps, "common");
            VisitEntries(steamApps, (path, attributes) =>
            {
                if (attributes.HasFlag(FileAttributes.Directory)) return;
                string name = Path.GetFileName(path);
                const string prefix = "appmanifest_";
                const string suffix = ".acf";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    !SteamManifestParser.TryId(name[prefix.Length..^suffix.Length], out uint id))
                    return;

                var manifest = ReadManifest(path);
                if (manifest == null) return;
                if (manifest.AppId != id)
                {
                    AddIssue(path, CatalogIssueKind.InvalidManifest);
                    return;
                }

                string install = Path.Combine(common, manifest.InstallDirectory);
                if (!CheckDirectory(install)) return;
                string key = "steam:" + id.ToString(CultureInfo.InvariantCulture);
                var game = GetCandidate(key, manifest.Name, CatalogSource.Steam);
                if (game.InstallDirectories.Add(install))
                {
                    _steamFolders.TryAdd(install, game);
                    ScanExecutables(install, 0, game);
                }
            });
        }

        private SteamAppManifest? ReadManifest(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long size = stream.Length;
                if (size > SteamManifestParser.MaxDocumentSize)
                {
                    AddIssue(path, CatalogIssueKind.InvalidManifest);
                    return null;
                }
                if (size > ManifestBudget - _manifestBytes)
                {
                    _limitReached = true;
                    return null;
                }
                _manifestBytes += (int)size;
                var bytes = new byte[(int)size];
                stream.ReadExactly(bytes);
                _cancel.ThrowIfCancellationRequested();
                if (stream.ReadByte() == -1 &&
                    SteamManifestParser.ParseAppManifest(new UTF8Encoding(false, true).GetString(bytes)) is { } manifest)
                    return manifest;
                AddIssue(path, CatalogIssueKind.InvalidManifest);
            }
            catch (DecoderFallbackException) { AddIssue(path, CatalogIssueKind.InvalidManifest); }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue(path, CatalogIssueKind.Unavailable); }
            return null;
        }

        private void ScanCollection(string path)
        {
            var rootGame = FolderCandidate(path);
            VisitEntries(path, (entry, attributes) =>
            {
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (_request.MaxDepth == 0) _limitReached = true;
                    else if (!_steamFolders.ContainsKey(entry)) ScanFolder(entry, 1);
                }
                else AddExecutable(rootGame, entry);
            });
            RemoveEmptyFolder(rootGame);
        }

        private void ScanFolder(string path, int depth = 0)
        {
            var game = FolderCandidate(path);
            ScanExecutables(path, depth, game);
            RemoveEmptyFolder(game);
        }

        private void ScanExecutables(string path, int depth, Candidate game)
        {
            VisitEntries(path, (entry, attributes) =>
            {
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (depth == _request.MaxDepth) _limitReached = true;
                    else if (!_steamFolders.ContainsKey(entry)) ScanExecutables(entry, depth + 1, game);
                }
                else AddExecutable(game, entry);
            });
        }

        private static void AddExecutable(Candidate game, string path)
        {
            if (!CatalogPaths.IsExecutable(path)) return;
            if (CatalogPaths.IsDiscordProcess(path)) return;
            game.Executables.Add(path);
        }

        private void AddManual(ManualCatalogEntry manual)
        {
            var executables = manual.Executables.Select(CatalogPaths.LocalPath).ToArray();
            if (manual.Id == Guid.Empty || string.IsNullOrWhiteSpace(manual.DisplayName) || executables.Length == 0 ||
                executables.Any(p => p == null || !CatalogPaths.IsExecutable(p)))
            {
                AddIssue(manual.DisplayName, CatalogIssueKind.InvalidManualEntry);
                return;
            }
            string id = "manual:" + manual.Id.ToString("N");
            // Explicit entries can refer to offline files and are never opened during a scan.
            var game = GetCandidate(id, manual.DisplayName.Trim(), CatalogSource.Manual);
            game.Executables.UnionWith(executables.OfType<string>());
        }

        private Candidate FolderCandidate(string path)
        {
            string id = "folder:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            var game = GetCandidate(id, Path.GetFileName(path), CatalogSource.Folder);
            game.InstallDirectories.Add(path);
            return game;
        }

        private Candidate GetCandidate(string id, string name, CatalogSource source)
        {
            if (!_games.TryGetValue(id, out var game))
                _games.Add(id, game = new(id, name, source));
            return game;
        }

        private void RemoveEmptyFolder(Candidate game)
        {
            if (game.Executables.Count == 0) _games.Remove(game.Id);
        }

        private string? ApprovedRoot(string raw)
        {
            _cancel.ThrowIfCancellationRequested();
            string? path = CatalogPaths.LocalPath(raw);
            if (path == null || Path.GetPathRoot(path) is not { } drive || path.Equals(drive, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(raw, CatalogIssueKind.InvalidPath);
                return null;
            }
            try
            {
                if (new DriveInfo(drive).DriveType != DriveType.Fixed)
                {
                    AddIssue(raw, CatalogIssueKind.InvalidPath);
                    return null;
                }
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                AddIssue(raw, CatalogIssueKind.Unavailable);
                return null;
            }
            return CheckDirectory(path) ? path : null;
        }

        private bool CheckDirectory(string path)
        {
            if (IsExcluded(path)) return false;
            var ancestors = new Stack<string>();
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
                ancestors.Push(current);
            foreach (string current in ancestors)
            {
                _cancel.ThrowIfCancellationRequested();
                if (IgnoredNames.Contains(Path.GetFileName(current))) return false;
                if (!TryAttributes(current, out var attributes)) return false;
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    AddIssue(current, CatalogIssueKind.Unavailable);
                    return false;
                }
            }
            return true;
        }

        private void VisitEntries(string directory, Action<string, FileAttributes> visit)
        {
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                while (CanVisitMore() && entries.MoveNext())
                {
                    _cancel.ThrowIfCancellationRequested();
                    _visited++;
                    if (_progress != null && (_visited == 1 || _visited % 256 == 0))
                    {
                        _progress.Report(_visited);
                        _cancel.ThrowIfCancellationRequested();
                    }
                    string path = entries.Current;
                    string leaf = Path.GetFileName(path);
                    if (!CatalogPaths.SafeLeaf(leaf) || IgnoredNames.Contains(leaf) || IsExcluded(path)) continue;
                    if (TryAttributes(path, out var attributes)) visit(path, attributes);
                }
            }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue(directory, CatalogIssueKind.Unavailable); }
        }

        private bool TryAttributes(string path, out FileAttributes attributes)
        {
            attributes = default;
            try
            {
                attributes = File.GetAttributes(path);
                if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
                AddIssue(path, CatalogIssueKind.ReparsePoint);
            }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue(path, CatalogIssueKind.Unavailable); }
            return false;
        }

        private bool IsExcluded(string path) => _excluded.Any(directory => CatalogPaths.IsWithin(path, directory));

        private bool CanVisitMore()
        {
            _cancel.ThrowIfCancellationRequested();
            if (_visited < _request.MaxEntries) return true;
            _limitReached = true;
            return false;
        }

        private void AddIssue(string path, CatalogIssueKind kind)
        {
            if (_issues.Count < 128) _issues.Add(new(path, kind));
        }

        private static bool IsAccessError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

        private sealed class Candidate(string id, string name, CatalogSource source)
        {
            public string Id { get; } = id;
            public HashSet<string> InstallDirectories { get; } = new(Paths);
            public HashSet<string> Executables { get; } = new(Paths);

            public CatalogEntry ToEntry(CatalogRules rules) =>
                new(Id, name, source, [.. InstallDirectories.Order(Paths)], [.. Executables.Order(Paths)], rules);
        }
    }
}

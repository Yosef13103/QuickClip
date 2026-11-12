using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace QuickClip.Discovery;

/// <summary>One bounded catalog scan. Discovered executables are never launched.</summary>
internal static class GameCatalogService
{
    public static async Task<CatalogResult> ScanAsync(CatalogRequest request, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null, Func<IEnumerable<string>>? steamInstallations = null) =>
        await CreateScan(request, steamInstallations)
            .ContinueAsync(int.MaxValue, TimeSpan.FromSeconds(45), cancellationToken, progress).ConfigureAwait(false);

    internal static Scan CreateScan(CatalogRequest request, Func<IEnumerable<string>>? steamInstallations = null,
        Func<string?>? epicManifests = null) =>
        new(request, steamInstallations ?? FindSteamInstallations, epicManifests ?? FindEpicManifests);

    private static string? FindEpicManifests()
    {
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return common.Length == 0 ? null : Path.Combine(common, "Epic", "EpicGamesLauncher", "Data", "Manifests");
    }

    private static IEnumerable<string> FindSteamInstallations()
    {
        foreach (var (hive, keyName) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Valve\Steam"),
            (Registry.LocalMachine, @"Software\Valve\Steam"),
        })
        {
            using var key = hive.OpenSubKey(keyName);
            if ((key?.GetValue("SteamPath") ?? key?.GetValue("InstallPath")) is string path)
                yield return path;
        }
        string conventional = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (File.Exists(Path.Combine(conventional, "Steam.exe"))) yield return conventional;
    }

    internal sealed class Scan
    {
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        private static readonly HashSet<string> IgnoredNames = new(Paths)
        {
            ".git", "node_modules", "$Recycle.Bin", "System Volume Information",
            "_CommonRedist", "_Redist", "redist", "redistributables"
        };
        private const int ManifestBudget = 8 * SteamManifestParser.MaxDocumentSize;
        private readonly CatalogRequest _request;
        private CancellationToken _cancel;
        private IProgress<int>? _progress;
        private readonly Func<IEnumerable<string>> _steamInstallations;
        private readonly Func<string?> _epicManifests;
        private readonly string[] _excluded;
        private readonly Dictionary<string, Candidate> _games = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Candidate> _launcherFolders = new(Paths);
        private readonly List<CatalogIssue> _issues = [];
        private readonly LinkedList<Action> _pending = new();
        private readonly HashSet<string> _steamRoots = new(Paths);
        private readonly HashSet<string> _collections = new(Paths);
        private bool _initialized;
        private string? _epicManifestDirectory;
        private int _scheduled;
        private int _visited, _manifestBytes, _segmentEntries, _reportedGames = -1;
        private long _segmentStarted;
        private bool _limitReached;

        public Scan(CatalogRequest request, Func<IEnumerable<string>> steamInstallations, Func<string?> epicManifests)
        {
            if (request.MaxEntries is < 1 or > 100_000 || request.MaxDepth is < 0 or > 16 ||
                request.GameRoots.Length + request.SteamLibraryRoots.Length > 128)
                throw new ArgumentException("Invalid catalog scan limits.", nameof(request));
            _request = request;
            _steamInstallations = steamInstallations;
            _epicManifests = epicManifests;
            _excluded = request.ExcludedDirectories.Select(path => CatalogPaths.LocalPath(path)
                ?? throw new ArgumentException("Excluded directories must be absolute local drive paths.", nameof(request))).ToArray();
        }

        public bool HasMore => _pending.Count != 0;

        public Task<CatalogResult> ContinueAsync(int maxSegments, TimeSpan admissionWindow, CancellationToken token,
            IProgress<int>? progress = null) =>
            Task.Run(() =>
            {
                _cancel = token;
                _progress = progress;
                try { return Run(maxSegments, admissionWindow); }
                finally { _progress = null; }
            }, token);

        private CatalogResult Run(int maxSegments, TimeSpan admissionWindow)
        {
            if (!_initialized)
            {
                PlanSources();
                _initialized = true;
            }
            long started = Stopwatch.GetTimestamp();
            for (int count = 0; _pending.First is { } next && count < maxSegments &&
                 (count == 0 || Stopwatch.GetElapsedTime(started) < admissionWindow); count++)
            {
                _cancel.ThrowIfCancellationRequested();
                _pending.RemoveFirst();
                Segment(next.Value);
            }

            var rules = new Dictionary<string, CatalogRules>(StringComparer.Ordinal);
            foreach (var entry in _request.Overrides)
                if (!string.IsNullOrWhiteSpace(entry.Id)) rules[entry.Id] = entry.Rules;

            var entries = _games.Values.Select(game =>
                {
                    _cancel.ThrowIfCancellationRequested();
                    return game.ToEntry(rules.GetValueOrDefault(game.Id) ?? new());
                })
                .OrderBy(game => game.DisplayName, Paths).ThenBy(game => game.Id, StringComparer.Ordinal)
                .ToImmutableArray();
            _cancel.ThrowIfCancellationRequested();
            return new(entries, [.. _issues], _visited, _limitReached || HasMore, DateTimeOffset.UtcNow)
            {
                SteamLibraries = [.. _steamRoots.Order(Paths)],
                EpicManifestDirectory = _epicManifestDirectory,
            };
        }

        private void PlanSources()
        {
            // Launcher identities take precedence when both sources name the same install folder.
            var planned = new List<Action>();
            foreach (var raw in _request.SteamLibraryRoots.Concat(DiscoverSteamLibraries()).Take(128))
            {
                if (ApprovedRoot(raw) is { } library && _steamRoots.Add(library))
                    planned.Add(() => ScanSteam(library));
            }
            if (_request.DiscoverEpic && CatalogPaths.LocalPath(_epicManifests()) is { Length: > 3 } epic &&
                !IsExcluded(epic) && Directory.Exists(epic))
            {
                _epicManifestDirectory = epic;
                planned.Add(() => ScanEpic(epic));
            }

            var gameRoots = new HashSet<string>(Paths);
            foreach (var root in _request.GameRoots)
            {
                if (ApprovedRoot(root.Path) is not { } path || !gameRoots.Add(path)) continue;
                planned.Add(() =>
                {
                    if (_launcherFolders.ContainsKey(path)) return;
                    if (root.IsCollection) ScanCollection(path);
                    else ScanFolder(path);
                });
            }

            foreach (var manual in _request.ManualEntries)
            {
                _cancel.ThrowIfCancellationRequested();
                AddManual(manual);
            }

            Prepend(planned, "Game libraries");
        }

        private void Prepend(List<Action> work, string source)
        {
            const int maxSegmentsPerScan = 4096;
            int count = Math.Min(work.Count, maxSegmentsPerScan - _scheduled);
            _scheduled += count;
            if (count < work.Count)
            {
                _limitReached = true;
                AddIssue(source, CatalogIssueKind.Incomplete);
            }
            for (int i = count - 1; i >= 0; i--) _pending.AddFirst(work[i]);
        }

        private IEnumerable<string> DiscoverSteamLibraries()
        {
            if (!_request.DiscoverSteam) return [];
            var libraries = new HashSet<string>(Paths);
            try
            {
                foreach (var installation in _steamInstallations().Distinct(Paths).Take(8))
                {
                    if (ApprovedRoot(installation) is not { } root) continue;
                    libraries.Add(root);
                    foreach (var parent in new[] { "config", "steamapps" })
                    {
                        string directory = Path.Combine(root, parent);
                        string file = Path.Combine(directory, "libraryfolders.vdf");
                        if (IsExcluded(file) || !File.Exists(file) || !CheckDirectory(directory) || !TryAttributes(file, out _)) continue;
                        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (stream.Length > SteamManifestParser.MaxDocumentSize)
                        {
                            AddIssue(file, CatalogIssueKind.InvalidManifest);
                            continue;
                        }
                        var bytes = new byte[(int)stream.Length];
                        stream.ReadExactly(bytes);
                        if (stream.ReadByte() == -1)
                            libraries.UnionWith(SteamManifestParser.ParseLibraryFolders(Encoding.UTF8.GetString(bytes)));
                    }
                }
            }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue("Steam library configuration", CatalogIssueKind.Unavailable); }
            return libraries.Order(Paths);
        }

        private void ScanSteam(string library)
        {
            string steamApps = Path.Combine(library, "steamapps");
            if (!CheckDirectory(steamApps)) return;
            string common = Path.Combine(steamApps, "common");
            var installs = new List<Action>();
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
                    _launcherFolders.TryAdd(install, game);
                    installs.Add(() => ScanExecutables(install, 0, game));
                }
            }, name => name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase));
            Prepend(installs, library);
        }

        private void ScanEpic(string directory)
        {
            VisitEntries(directory, (path, _) =>
            {
                try
                {
                    using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    long length = file.Length;
                    if (length > EpicManifestParser.MaxDocumentSize || length > ManifestBudget - _manifestBytes)
                    {
                        AddIssue(path, length > EpicManifestParser.MaxDocumentSize ? CatalogIssueKind.InvalidManifest : CatalogIssueKind.Incomplete);
                        _limitReached |= length <= EpicManifestParser.MaxDocumentSize;
                        return;
                    }
                    _manifestBytes += (int)length;
                    var data = new byte[(int)length];
                    file.ReadExactly(data);
                    _cancel.ThrowIfCancellationRequested();
                    if (file.ReadByte() != -1) { AddIssue(path, CatalogIssueKind.Unavailable); return; }
                    if (EpicManifestParser.Parse(data, out bool malformed) is not { } manifest)
                    {
                        if (malformed) AddIssue(path, CatalogIssueKind.InvalidManifest);
                        return;
                    }
                    if (!IsRendererCandidate(manifest.Executable) || IsExcluded(manifest.Executable) ||
                        ApprovedRoot(manifest.InstallDirectory) == null || !CheckDirectory(Path.GetDirectoryName(manifest.Executable)!) ||
                        !TryAttributes(manifest.Executable, out var attributes) || attributes.HasFlag(FileAttributes.Directory))
                        return;
                    if (_launcherFolders.TryGetValue(manifest.InstallDirectory, out var existing) && existing.Id != manifest.Id) return;
                    var game = GetCandidate(manifest.Id, manifest.Name, CatalogSource.Epic);
                    game.InstallDirectories.Add(manifest.InstallDirectory);
                    game.Executables.Add(manifest.Executable);
                    _launcherFolders.TryAdd(manifest.InstallDirectory, game);
                }
                catch (Exception ex) when (IsAccessError(ex)) { AddIssue(path, CatalogIssueKind.Unavailable); }
            }, name => name.EndsWith(".item", StringComparison.OrdinalIgnoreCase));
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

        private void ScanCollection(string path, int depth = 0)
        {
            if (!_collections.Add(path) || _launcherFolders.ContainsKey(path)) return;
            var directories = new List<string>();
            var executables = new List<string>();
            VisitEntries(path, (entry, attributes) =>
            {
                if (attributes.HasFlag(FileAttributes.Directory)) directories.Add(entry);
                else if (IsRendererCandidate(entry)) executables.Add(entry);
            });
            if (depth > 0 && (executables.Count > 0 || directories.Any(IsBinaryDirectory)))
            {
                Prepend([() => ScanFolder(path, depth)], path);
                return;
            }
            if (executables.Count > 0)
            {
                var game = FolderCandidate(path);
                game.Executables.UnionWith(executables);
            }
            var subdirectories = directories.Where(dir => !_launcherFolders.ContainsKey(dir)).ToArray();
            if (subdirectories.Length > 0 && depth >= Math.Min(_request.MaxDepth, 4))
            {
                _limitReached = true;
                AddIssue(path, CatalogIssueKind.Incomplete);
                return;
            }
            Prepend(subdirectories.Select<string, Action>(directory => () => ScanCollection(directory, depth + 1)).ToList(), path);
        }

        private static bool IsBinaryDirectory(string path) => Path.GetFileName(path).ToUpperInvariant()
            is "BIN" or "BINARIES" or "WIN64" or "WIN32" or "X64" or "X86";

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
                    else if (!_launcherFolders.ContainsKey(entry)) ScanExecutables(entry, depth + 1, game);
                }
                else AddExecutable(game, entry);
            });
        }

        private static void AddExecutable(Candidate game, string path)
        {
            if (!IsRendererCandidate(path)) return;
            game.Executables.Add(path);
        }

        private static bool IsRendererCandidate(string path)
        {
            if (!CatalogPaths.IsExecutable(path) || CatalogPaths.IsDiscordProcess(path)) return false;
            string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            return name is not ("SETUP" or "INSTALL" or "INSTALLER" or "LAUNCHER" or "GAMELAUNCHER" or "UPDATE" or "UPDATER"
                or "CRASHPAD_HANDLER" or "UNITYCRASHHANDLER32" or "UNITYCRASHHANDLER64" or "CRASHREPORTCLIENT")
                && !name.StartsWith("UNINS", StringComparison.Ordinal)
                && !name.StartsWith("VC_REDIST", StringComparison.Ordinal)
                && !name.EndsWith("CRASHREPORTER", StringComparison.Ordinal);
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

        private void VisitEntries(string directory, Action<string, FileAttributes> visit, Func<string, bool>? manifestName = null)
        {
            if (!CheckDirectory(directory)) return;
            try
            {
                var source = new FileSystemEnumerable<(string Path, FileAttributes Attributes)>(directory,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes),
                    new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System });
                using var entries = source.GetEnumerator();
                while (CanVisitMore(directory) && entries.MoveNext())
                {
                    _cancel.ThrowIfCancellationRequested();
                    var (path, attributes) = entries.Current;
                    string leaf = Path.GetFileName(path);
                    if (manifestName != null ? attributes.HasFlag(FileAttributes.Directory) || !manifestName(leaf)
                        : !attributes.HasFlag(FileAttributes.Directory) && !CatalogPaths.IsExecutable(path)) continue;
                    _visited++;
                    _segmentEntries++;
                    if (!CatalogPaths.SafeLeaf(leaf) || IgnoredNames.Contains(leaf) || IsExcluded(path)) continue;
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) AddIssue(path, CatalogIssueKind.ReparsePoint);
                    else visit(path, attributes);
                    if (_progress != null && _reportedGames != _games.Count)
                    {
                        _progress.Report(_reportedGames = _games.Count);
                        _cancel.ThrowIfCancellationRequested();
                    }
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

        private bool CanVisitMore(string directory)
        {
            _cancel.ThrowIfCancellationRequested();
            if (_segmentEntries < _request.MaxEntries && Stopwatch.GetElapsedTime(_segmentStarted) < TimeSpan.FromSeconds(8)) return true;
            _limitReached = true;
            AddIssue(directory, CatalogIssueKind.Incomplete);
            return false;
        }

        private void Segment(Action scan)
        {
            _segmentEntries = 0;
            _manifestBytes = 0;
            _segmentStarted = Stopwatch.GetTimestamp();
            scan();
        }

        private void AddIssue(string path, CatalogIssueKind kind)
        {
            if (_issues.Count < 128) _issues.Add(new(path, kind));
        }

        private static bool IsAccessError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

        private sealed class Candidate(string id, string name, CatalogSource source)
        {
            private CatalogEntry? _cached;
            public string Id { get; } = id;
            public HashSet<string> InstallDirectories { get; } = new(Paths);
            public HashSet<string> Executables { get; } = new(Paths);

            public CatalogEntry ToEntry(CatalogRules rules)
            {
                if (_cached is { } cached && cached.Executables.Length == Executables.Count &&
                    cached.InstallDirectories.Length == InstallDirectories.Count)
                    return cached with { Rules = rules };
                var ranked = Executables.OrderByDescending(Score).ThenBy(path => path, Paths).ToImmutableArray();
                string displayName = source == CatalogSource.Folder
                    ? ranked.Take(6).Select(ReadProductName).FirstOrDefault(value => value != null) ?? name
                    : name;
                return _cached = new(Id, displayName, source, [.. InstallDirectories.Order(Paths)], ranked, rules);
            }

            private int Score(string path)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (stem.Equals(name, StringComparison.OrdinalIgnoreCase)) return 3;
                if (stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) return 2;
                return stem.EndsWith("Helper", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }

            private static string? ReadProductName(string path)
            {
                try
                {
                    var version = FileVersionInfo.GetVersionInfo(path);
                    return new[] { version.ProductName, version.FileDescription }.Select(value => value?.Trim())
                        .FirstOrDefault(value => value is { Length: >= 2 and <= 100 } && value.Any(char.IsLetter) &&
                            !value.Any(char.IsControl) && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            !new[] { "Unreal Engine", "Unity", "BootstrapPackagedGame", "Microsoft® Windows® Operating System" }
                                .Contains(value, Paths));
                }
                catch (Exception ex) when (IsAccessError(ex)) { return null; }
            }
        }
    }
}

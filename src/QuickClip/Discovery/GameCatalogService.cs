using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using Microsoft.Win32;

namespace QuickClip.Discovery;

/// <summary>One bounded catalog scan. Discovered executables are never launched.</summary>
internal static partial class GameCatalogService
{
    public static async Task<CatalogResult> ScanAsync(CatalogRequest request, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null, Func<IEnumerable<string>>? steamInstallations = null)
    {
        using var scan = CreateScan(request, steamInstallations);
        CatalogResult result;
        do
        {
            result = await scan.ContinueAsync(int.MaxValue, TimeSpan.FromSeconds(1), cancellationToken, progress)
                .ConfigureAwait(false);
        } while (scan.HasMore);
        return result;
    }

    internal static Scan CreateScan(CatalogRequest request, Func<IEnumerable<string>>? steamInstallations = null,
        Func<string?>? epicManifests = null, Func<CancellationToken, IEnumerable<GogRegistryEntry>>? gogProducts = null,
        Func<CatalogSource, IEnumerable<LauncherInstall?>>? localInstalls = null) =>
        new(request, steamInstallations ?? FindSteamInstallations, epicManifests ?? FindEpicManifests,
            gogProducts ?? GogRegistry.Read, localInstalls);

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

    internal sealed partial class Scan : IDisposable
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
        private readonly Func<CancellationToken, IEnumerable<GogRegistryEntry>> _gogProducts;
        private readonly Func<CatalogSource, IEnumerable<LauncherInstall?>>? _localInstalls;
        private readonly List<CatalogEntry> _verifiedInstalls = [];
        private ImmutableArray<CatalogEntry> _verifiedSnapshot = [];
        private int _installFailures;
        private readonly string[] _excluded;
        private readonly Dictionary<string, Candidate> _games = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Candidate> _launcherFolders = new(Paths);
        private readonly List<CatalogIssue> _issues = [];
        private readonly Queue<Func<IEnumerable<ScanStep>>> _sources = new();
        private readonly Queue<Func<IEnumerable<ScanStep>>> _launcherInstalls = new();
        private readonly Stack<IEnumerator<ScanStep>> _walk = new();
        private readonly object _gate = new();
        private readonly record struct ScanStep(Func<IEnumerable<ScanStep>>? Child);
        private bool _disposed;
        private readonly HashSet<string> _steamRoots = new(Paths);
        private bool _initialized;
        private string? _epicManifestDirectory;
        private int _visited, _inspectedEntries, _candidateExecutablesVisited, _manifestBytes, _batch, _reportedGames = -1, _relevantSteps;
        private bool _limitReached, _hasMoreIssues, _hasUnreportedFailures;
        private bool _sourceUnavailable;
        private CatalogResult? _lastResult;
        private long _lastSnapshotAt;
        public bool HasCatalogChanges { get; private set; }

        public Scan(CatalogRequest request, Func<IEnumerable<string>> steamInstallations, Func<string?> epicManifests,
            Func<CancellationToken, IEnumerable<GogRegistryEntry>> gogProducts,
            Func<CatalogSource, IEnumerable<LauncherInstall?>>? localInstalls)
        {
            if (request.MaxEntries is < 1 or > 100_000 || request.MaxDepth is < 0 ||
                request.GameRoots.Length + request.SteamLibraryRoots.Length > 128)
                throw new ArgumentException("Invalid catalog scan limits.", nameof(request));
            _request = request;
            _steamInstallations = steamInstallations;
            _epicManifests = epicManifests;
            _gogProducts = gogProducts;
            _localInstalls = localInstalls;
            _excluded = request.ExcludedDirectories.Select(path => CatalogPaths.LocalPath(path)
                ?? throw new ArgumentException("Excluded directories must be absolute local drive paths.", nameof(request))).ToArray();
        }

        public bool HasMore => _walk.Count != 0 || _launcherInstalls.Count != 0 || _sources.Count != 0;

        public Task<CatalogResult> ContinueAsync(int maxSegments, TimeSpan admissionWindow, CancellationToken token,
            IProgress<int>? progress = null, bool throttleSnapshots = false) =>
            Task.Run(() =>
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _cancel = token;
                    _progress = progress;
                    try { return Run(maxSegments, admissionWindow, throttleSnapshots); }
                    finally { _progress = null; }
                }
            }, token);

        private CatalogResult Run(int maxSegments, TimeSpan admissionWindow, bool throttleSnapshots)
        {
            if (maxSegments < 1) throw new ArgumentOutOfRangeException(nameof(maxSegments));
            _batch++;
            _manifestBytes = 0;
            _relevantSteps = 0;
            long started = Stopwatch.GetTimestamp();
            if (!_initialized)
            {
                PlanSources();
                _initialized = true;
            }
            int steps = 0, completed = 0;
            while (HasMore && completed < maxSegments && _relevantSteps < _request.MaxEntries && steps < 20_000 &&
                   _manifestBytes < ManifestBudget &&
                   (steps == 0 || Stopwatch.GetElapsedTime(started) < admissionWindow))
            {
                _cancel.ThrowIfCancellationRequested();
                steps++;
                if (_walk.Count == 0)
                {
                    var work = _launcherInstalls.Count == 0 ? _sources.Dequeue() : _launcherInstalls.Dequeue();
                    _walk.Push(work().GetEnumerator());
                }
                var current = _walk.Peek();
                if (current.MoveNext())
                {
                    if (current.Current.Child is { } child) _walk.Push(child().GetEnumerator());
                    if (_progress != null && _reportedGames != _games.Count)
                    {
                        _progress.Report(_reportedGames = _games.Count);
                        _cancel.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    _walk.Pop().Dispose();
                    if (_walk.Count == 0) completed++;
                }
            }

            bool pending = HasMore;
            if (pending && throttleSnapshots && _lastResult is { } prior &&
                prior.Issues.Length == _issues.Count && prior.HasUnreportedFailures == _hasUnreportedFailures &&
                Stopwatch.GetElapsedTime(_lastSnapshotAt) < TimeSpan.FromSeconds(1))
            {
                HasCatalogChanges = false;
                return prior with { VisitedEntries = _visited, InspectedEntries = _inspectedEntries,
                    CandidateExecutablesVisited = _candidateExecutablesVisited };
            }
            return Snapshot(pending);
        }

        internal Task<CatalogResult> SnapshotAsync(CancellationToken token) => Task.Run(() =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _cancel = token;
                return Snapshot(HasMore);
            }
        }, token);

        private CatalogResult Snapshot(bool pending)
        {
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
            if (_verifiedSnapshot.Length != _verifiedInstalls.Count)
                _verifiedSnapshot = [.. _verifiedInstalls];
            var result = new CatalogResult(entries, [.. _issues], _visited, _limitReached, pending ? default : DateTimeOffset.UtcNow)
            {
                SteamLibraries = [.. _steamRoots.Order(Paths)],
                EpicManifestDirectory = _epicManifestDirectory,
                InProgress = pending,
                HasMoreIssues = _hasMoreIssues,
                HasUnreportedFailures = _hasUnreportedFailures,
                SourceUnavailable = _sourceUnavailable,
                CandidateExecutablesVisited = _candidateExecutablesVisited,
                InspectedEntries = _inspectedEntries,
                VerifiedInstalls = _verifiedSnapshot,
            };
            HasCatalogChanges = _lastResult == null || !_lastResult.Entries.SequenceEqual(entries) ||
                _lastResult.VerifiedInstalls != result.VerifiedInstalls ||
                !_lastResult.Issues.SequenceEqual(result.Issues) || _lastResult.HasMoreIssues != _hasMoreIssues ||
                _lastResult.HasUnreportedFailures != _hasUnreportedFailures;
            _lastResult = result;
            _lastSnapshotAt = Stopwatch.GetTimestamp();
            return result;
        }

        private void PlanSources()
        {
            // Launcher identities take precedence when both sources name the same install folder.
            foreach (var raw in _request.SteamLibraryRoots.Concat(DiscoverSteamLibraries()))
            {
                if (ApprovedRoot(raw) is { } library && _steamRoots.Add(library))
                    _sources.Enqueue(() => ScanSteam(library));
            }
            if (_request.DiscoverEpic && CatalogPaths.LocalPath(_epicManifests()) is { Length: > 3 } epic && !IsExcluded(epic))
            {
                try
                {
                    if (!(File.GetAttributes(epic).HasFlag(FileAttributes.Directory))) AddIssue(epic, CatalogIssueKind.Unavailable);
                    else if (CheckDirectory(epic))
                    {
                        _epicManifestDirectory = epic;
                        _sources.Enqueue(() => ScanEpic(epic));
                    }
                    else _sourceUnavailable = true;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    AddIssue(epic, CatalogIssueKind.SourceMissing);
                }
                catch (Exception ex) when (IsAccessError(ex)) { AddIssue(epic, CatalogIssueKind.Unavailable); }
            }
            if (_request.DiscoverGog) _sources.Enqueue(ScanGog);
            foreach (var source in _request.Launchers.Where(LocalLaunchers.IsSupported).Distinct())
                _sources.Enqueue(() => ScanLocalLauncher(source));

            var gameRoots = new HashSet<string>(Paths);
            foreach (var root in _request.GameRoots)
            {
                if (ApprovedRoot(root.Path) is not { } path || !gameRoots.Add(path)) continue;
                _sources.Enqueue(() =>
                {
                    if (_launcherFolders.ContainsKey(path)) return [];
                    return root.IsCollection ? ScanCollection(path) : ScanFolder(path);
                });
            }

            foreach (var manual in _request.ManualEntries)
            {
                _cancel.ThrowIfCancellationRequested();
                AddManual(manual);
            }
        }

        private IEnumerable<ScanStep> ScanGog()
        {
            // The cursor spans batches; check the current batch token before every registry item.
            foreach (var product in _gogProducts(CancellationToken.None))
            {
                _cancel.ThrowIfCancellationRequested();
                _relevantSteps++;
                _visited++;
                if (product.Issue is { } issue)
                    AddIssue(GogRegistry.SourceName, issue);
                else if (!GogRegistry.TryProductId(product.Key, out long id) ||
                    (!string.IsNullOrWhiteSpace(product.Parent) && product.Parent != "0" &&
                        !GogRegistry.TryProductId(product.Parent, out _)))
                    AddIssue(GogRegistry.SourceName, CatalogIssueKind.InvalidManifest);
                else if (!GogRegistry.TryProductId(product.Parent, out long parent) || parent == id)
                {
                    if (string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 160 ||
                        product.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(product.Path))
                        AddIssue(GogRegistry.SourceName, CatalogIssueKind.InvalidManifest);
                    else if (ApprovedRoot(product.Path) is { } install)
                    {
                        // Some tools introduce additional records for the same product. Keep distinct
                        // validated directories under the same stable product ID.
                        var game = GetCandidate("gog:" + id.ToString(CultureInfo.InvariantCulture), product.Name.Trim(), CatalogSource.Gog);
                        if (game.InstallDirectories.Add(install))
                            _launcherInstalls.Enqueue(() => ScanInstall(install, 0, game, null, allowFolderFallback: true));
                    }
                }
                yield return default;
            }
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
                        if (stream.ReadByte() != -1)
                        {
                            AddIssue(file, CatalogIssueKind.Incomplete);
                            continue;
                        }
                        libraries.UnionWith(SteamManifestParser.ParseLibraryFolders(Encoding.UTF8.GetString(bytes), out bool complete));
                        if (!complete) AddIssue(file, CatalogIssueKind.InvalidManifest);
                    }
                }
            }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue("Steam library configuration", CatalogIssueKind.Unavailable); }
            return libraries.Order(Paths);
        }

        private IEnumerable<ScanStep> ScanSteam(string library)
        {
            string steamApps = Path.Combine(library, "steamapps");
            string common = Path.Combine(steamApps, "common");

            foreach (var entry in VisitEntries(steamApps, name => name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase)))
            {
                yield return default;
                if (entry is not { } item) continue;
                string path = item.Path;
                if (!CheckDirectory(steamApps) || !TryAttributes(path, out var attributes) ||
                    attributes.HasFlag(FileAttributes.Directory)) continue;
                string name = Path.GetFileName(path);
                const string prefix = "appmanifest_";
                const string suffix = ".acf";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    !SteamManifestParser.TryId(name[prefix.Length..^suffix.Length], out uint id))
                    continue;

                var manifest = ReadManifest(path);
                if (manifest == null) continue;
                if (manifest.AppId != id)
                {
                    AddIssue(path, CatalogIssueKind.InvalidManifest);
                    continue;
                }

                if (id == 228980) continue;
                string install = Path.Combine(common, manifest.InstallDirectory);
                if (!CheckDirectory(install)) continue;
                string key = "steam:" + id.ToString(CultureInfo.InvariantCulture);
                var game = GetCandidate(key, manifest.Name, CatalogSource.Steam);
                if (game.InstallDirectories.Add(install))
                {
                    _launcherFolders.TryAdd(install, game);
                    _launcherInstalls.Enqueue(() => ScanInstall(install, 0, game, null));
                }
            }
        }

        private IEnumerable<ScanStep> ScanEpic(string directory)
        {
            foreach (var entry in VisitEntries(directory, name => name.EndsWith(".item", StringComparison.OrdinalIgnoreCase)))
            {
                yield return default;
                if (entry is not { } item) continue;
                string path = item.Path;
                if (!CheckDirectory(directory) || !TryAttributes(path, out var attributes) ||
                    attributes.HasFlag(FileAttributes.Directory)) continue;
                try
                {
                    using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    long length = file.Length;
                    if (length > EpicManifestParser.MaxDocumentSize)
                    {
                        AddIssue(path, CatalogIssueKind.InvalidManifest);
                        continue;
                    }
                    _manifestBytes += (int)length;
                    var data = new byte[(int)length];
                    file.ReadExactly(data);
                    _cancel.ThrowIfCancellationRequested();
                    if (file.ReadByte() != -1) { AddIssue(path, CatalogIssueKind.Unavailable); continue; }
                    if (EpicManifestParser.Parse(data, out bool malformed) is not { } manifest)
                    {
                        if (malformed) AddIssue(path, CatalogIssueKind.InvalidManifest);
                        continue;
                    }
                    if (CatalogPaths.DefaultRole(manifest.Executable) is not (CatalogMatchKind.Unknown or CatalogMatchKind.Launcher) || IsExcluded(manifest.Executable) ||
                        ApprovedRoot(manifest.InstallDirectory) == null || !CheckDirectory(Path.GetDirectoryName(manifest.Executable)!) ||
                        !TryAttributes(manifest.Executable, out var executableAttributes) ||
                        executableAttributes.HasFlag(FileAttributes.Directory))
                        continue;
                    if (_launcherFolders.TryGetValue(manifest.InstallDirectory, out var existing) && existing.Id != manifest.Id) continue;
                    var game = GetCandidate(manifest.Id, manifest.Name, CatalogSource.Epic);
                    if (game.InstallDirectories.Add(manifest.InstallDirectory))
                    {
                        _launcherFolders.TryAdd(manifest.InstallDirectory, game);
                        _launcherInstalls.Enqueue(() => ScanInstall(manifest.InstallDirectory, 0, game, manifest.Executable));
                    }
                }
                catch (Exception ex) when (IsAccessError(ex)) { AddIssue(path, CatalogIssueKind.Unavailable); }
            }
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

        private IEnumerable<ScanStep> ScanCollection(string path, int depth = 0)
        {
            if (_launcherFolders.ContainsKey(path) || CatalogPaths.IsRedistributablePath(path)) yield break;
            // Classify a possible install before descending. This shallow probe
            // avoids holding every child of a wide collection in memory.
            if (depth > 0)
            {
                bool install = false;
                foreach (var entry in VisitEntries(path, count: false))
                {
                    if (entry is { } item && (item.IsDirectory ? IsBinaryDirectory(item.Path) : CatalogPaths.DefaultRole(item.Path) is CatalogMatchKind.Unknown or CatalogMatchKind.Launcher))
                    {
                        install = true;
                        break;
                    }
                    yield return default;
                }
                if (install)
                {
                    yield return new(() => ScanFolder(path, depth));
                    yield break;
                }
            }

            var looseExecutables = new List<string>();
            foreach (var entry in VisitEntries(path))
            {
                if (entry is { IsDirectory: true } folder && CanDescend(folder.Path, depth))
                    yield return new(() => ScanCollection(folder.Path, depth + 1));
                else
                {
                    if (entry is { IsDirectory: false } file && IsRendererCandidate(file.Path))
                    {
                        AddExecutable(FolderCandidate(path), file.Path);
                        looseExecutables.Add(file.Path);
                    }
                    yield return default;
                }
            }
            if (looseExecutables.Count > 0)
                yield return new(() => IdentifyRenderer(FolderCandidate(path), path, looseExecutables, null));
        }

        private static bool IsBinaryDirectory(string path)
        {
            string name = Path.GetFileName(path);
            if (name.ToUpperInvariant() is "BIN" or "BINARIES" or "WIN64" or "WIN32" or "X64" or "X86" or "CURRENT")
                return true;
            var version = name.AsSpan();
            if (version.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) version = version[4..];
            else if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase)) version = version[1..];
            return Version.TryParse(version, out _);
        }

        private IEnumerable<ScanStep> ScanFolder(string path, int depth = 0)
        {
            var game = FolderCandidate(path);
            yield return new(() => ScanInstall(path, depth, game, null, allowFolderFallback: true));
            RemoveEmptyFolder(game);
        }

        // Capture the complete executable set across resumable directory cursors BEFORE
        // applying exact Unity, Unreal, or Godot renderer eligibility evidence.
        private IEnumerable<ScanStep> ScanInstall(string install, int depth, Candidate game, string? launchExecutable,
            bool allowFolderFallback = false, bool requireInstallNamedFallback = false)
        {
            var executables = new List<string>();
            int failuresBefore = _installFailures;
            bool incomplete = false;
            if (launchExecutable != null) game.Executables.Add(launchExecutable);
            yield return new(() => ScanExecutables(install, depth, game, executables,
                catalogAll: launchExecutable == null, onIncomplete: () => incomplete = true));
            if (!incomplete && failuresBefore == _installFailures)
                yield return new(() => IdentifyRenderer(game, install, executables, launchExecutable, allowFolderFallback,
                    requireInstallNamedFallback));
            if (!incomplete && failuresBefore == _installFailures && game.Executables.Count > 0)
            {
                bool InInstall(string path) => CatalogPaths.IsWithin(path, install);
                var entry = game.ToEntry(new());
                _verifiedInstalls.Add(entry with
                {
                    InstallDirectories = [install],
                    Executables = [.. entry.Executables.Where(InInstall)],
                    RendererExecutables = entry.RendererExecutables is { } renderers ? [.. renderers.Where(InInstall)] : null,
                    DetectedLaunchers = [.. entry.DetectedLaunchers.Where(InInstall)],
                });
            }
        }

        private IEnumerable<ScanStep> ScanExecutables(string path, int depth, Candidate game,
            List<string>? executables = null, bool catalogAll = true, Action? onIncomplete = null)
        {
            using var entries = VisitEntries(path).GetEnumerator();
            while (entries.MoveNext())
            {
                var entry = entries.Current;
                if (entry is { IsDirectory: true } folder)
                {
                    if (CanDescend(folder.Path, depth))
                        yield return new(() => ScanExecutables(folder.Path, depth + 1, game,
                            executables, catalogAll, onIncomplete));
                    else onIncomplete?.Invoke();
                }
                else
                {
                    if (entry is { IsDirectory: false } file && IsRendererCandidate(file.Path))
                    {
                        if (catalogAll) AddExecutable(game, file.Path);
                        executables?.Add(file.Path);
                    }
                    yield return default;
                }
            }
        }

        private bool CanDescend(string path, int depth)
        {
            if (_launcherFolders.ContainsKey(path) || CatalogPaths.IsRedistributablePath(path)) return false;
            if (_request.MaxDepth is not { } limit || depth < limit) return true;
            _limitReached = true;
            AddIssue(path, CatalogIssueKind.DepthLimit);
            return false;
        }

        private static void AddExecutable(Candidate game, string path)
        {
            if (!IsRendererCandidate(path)) return;
            game.Executables.Add(path);
        }

        private IEnumerable<ScanStep> IdentifyRenderer(Candidate game, string install, IEnumerable<string> executables,
            string? launchExecutable, bool allowFolderFallback = false, bool requireInstallNamedFallback = false)
        {
            string? preferred = null;
            var exactInstallVariants = new List<string>();
            var scopedExecutables = new List<string>();
            var engineExecutables = new List<string>();
            int best = 0;
            bool unrealBootstrap = false;
            bool fortnite = game.Id.Equals("epic:fortnite", StringComparison.OrdinalIgnoreCase);
            string fortniteRenderer = Path.Combine(install, @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe");
            foreach (string executable in executables)
            {
                _cancel.ThrowIfCancellationRequested();
                if (game.DetectedLaunchers.Contains(executable)) { yield return default; continue; }
                bool inBinaryLocation = allowFolderFallback && IsInInstallBinaryLocation(install, executable);
                bool support = ManualGameInput.IsSupportFile(executable, install) || CatalogPaths.IsJavaRuntime(executable) ||
                    QuickClip.Recording.GameDetector.ApplyProcessRole(executable, new(CatalogMatchKind.Unknown)).Kind != CatalogMatchKind.Unknown;
                bool folderScope = game.Source == CatalogSource.Folder && inBinaryLocation;
                bool shipping = !Paths.Equals(RendererStem(executable), Path.GetFileNameWithoutExtension(executable));
                int score = support ? 0 : folderScope && !shipping ? ScopedFolderExecutable : RendererScore(install, executable);
                if (inBinaryLocation && !support)
                {
                    bool named = Paths.Equals(VariantStem(executable), Path.GetFileName(install));
                    if (named)
                        exactInstallVariants.Add(executable);
                    if (game.Source == CatalogSource.Folder) scopedExecutables.Add(executable);
                    if (score == 0 && (!requireInstallNamedFallback || named)) score = ScopedFolderExecutable;
                }
                if (launchExecutable != null && score == EngineRenderer && !Paths.Equals(launchExecutable, executable))
                {
                    bool bootstrap = SharesRendererName(launchExecutable, executable) ||
                        (fortnite && Paths.Equals(executable, fortniteRenderer));
                    if (!bootstrap && !(CatalogPaths.DefaultRole(launchExecutable) == CatalogMatchKind.Launcher &&
                        Paths.Equals(RendererStem(executable), Path.GetFileName(install)))) continue;
                    unrealBootstrap |= bootstrap;
                }
                if (score == EngineRenderer && game.Source == CatalogSource.Folder) engineExecutables.Add(executable);
                if (score > best) (best, preferred) = (score, executable);
                else if (score == best) preferred = null;
                yield return default; // Respect bounded scan slices during renderer verification.
            }

            if (launchExecutable != null)
            {
                if (CatalogPaths.DefaultRole(launchExecutable) == CatalogMatchKind.Launcher ||
                    unrealBootstrap)
                    game.DetectedLaunchers.Add(launchExecutable);
                else
                {
                    game.Renderers.Add(launchExecutable);
                    yield break;
                }
                if (best < EngineRenderer) yield break;
            }

            // A user-selected game install is positive scope evidence, even
            // without a unique name match or engine signature. Keep known
            // helpers, shared Java runtimes and incomplete sources excluded.
            if (game.Source == CatalogSource.Folder && launchExecutable == null)
            {
                game.Renderers.UnionWith(scopedExecutables);
                game.Renderers.UnionWith(engineExecutables);
            }
            if (exactInstallVariants.Count > 0)
                game.Renderers.UnionWith(exactInstallVariants);

            if (preferred != null)
            {
                game.Renderers.Add(preferred);
                game.Executables.Add(preferred);
            }

            if (game.Source == CatalogSource.Folder && engineExecutables.Count > 0)
            {
                var shippingNames = engineExecutables.Select(path => Path.GetFileNameWithoutExtension(path)!)
                    .Where(name => name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("-Win32-Shipping", StringComparison.OrdinalIgnoreCase))
                    .Select(RendererStem).ToHashSet(Paths);
                foreach (var path in scopedExecutables)
                    if (Paths.Equals(Path.GetDirectoryName(path), install) &&
                        shippingNames.Contains(Path.GetFileNameWithoutExtension(path)))
                    {
                        game.DetectedLaunchers.Add(path);
                        game.Renderers.Remove(path);
                    }
            }
        }

        private static string VariantStem(string executable)
        {
            string stem = RendererStem(executable);
            int suffix = stem.LastIndexOfAny(['-', '_']);
            return suffix > 0 && stem[(suffix + 1)..].ToUpperInvariant() is
                "X86" or "X64" or "WIN32" or "WIN64" or "DX11" or "DX12" or "VULKAN"
                ? stem[..suffix] : stem;
        }

        private static bool IsInInstallBinaryLocation(string install, string executable)
        {
            string directory = Path.GetDirectoryName(executable)!;
            while (CatalogPaths.IsWithin(directory, install))
            {
                if (Paths.Equals(directory, install)) return true;
                if (!IsBinaryDirectory(directory)) break;
                string parent = Path.GetDirectoryName(directory)!;
                if (Paths.Equals(Path.GetFileName(directory), "Binaries") &&
                    Paths.Equals(Path.GetDirectoryName(parent), install)) return true;
                directory = parent;
            }
            return false;
        }

        private static bool SharesRendererName(string path, string renderer)
        {
            string stem = RendererStem(renderer);
            return !Paths.Equals(path, renderer) &&
                Paths.Equals(Path.GetFileNameWithoutExtension(path), stem);
        }

        private static string RendererStem(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            return stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                stem.EndsWith("-Win32-Shipping", StringComparison.OrdinalIgnoreCase)
                    ? stem[..^"-Win64-Shipping".Length] : stem;
        }

        private const int ScopedFolderExecutable = 1;
        private const int EngineRenderer = 4;

        private int RendererScore(string install, string executable)
        {
            if (IsEngineRenderer(install, executable)) return EngineRenderer;
            string stem = Path.GetFileNameWithoutExtension(executable);
            return _request.GameRoots.Any(root => !root.IsCollection && Paths.Equals(CatalogPaths.LocalPath(root.Path), install)) &&
                stem.Equals(Path.GetFileName(install), StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        }

        private bool IsEngineRenderer(string install, string executable)
        {
            string directory = Path.GetDirectoryName(executable)!;
            string stem = Path.GetFileNameWithoutExtension(executable);
            string parent = Path.GetFileName(directory);
            if (Paths.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Binaries") &&
                CatalogPaths.IsWithin(directory, install) &&
                ((Paths.Equals(parent, "Win64") && stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) ||
                 (Paths.Equals(parent, "Win32") && stem.EndsWith("-Win32-Shipping", StringComparison.OrdinalIgnoreCase))))
                return true;

            string data = Path.Combine(directory, stem + "_Data");
            if (EvidenceFile(Path.Combine(directory, "UnityPlayer.dll")) && Directory.Exists(data) && CheckDirectory(data) &&
                (EvidenceFile(Path.Combine(data, "globalgamemanagers")) || EvidenceFile(Path.Combine(data, "data.unity3d"))))
                return true;

            string pack = Path.ChangeExtension(executable, ".pck");
            try
            {
                if (EvidenceFile(pack))
                {
                    using var external = File.Open(pack, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (HasGodotMagic(external)) return true;
                }
                using var embedded = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int footerSize = sizeof(ulong) + sizeof(uint);
                if (embedded.Length < footerSize + sizeof(uint)) return false;
                long footerPosition = embedded.Seek(-footerSize, SeekOrigin.End);
                Span<byte> footer = stackalloc byte[footerSize];
                embedded.ReadExactly(footer);
                if (!footer[sizeof(ulong)..].SequenceEqual("GDPC"u8)) return false;
                ulong size = BinaryPrimitives.ReadUInt64LittleEndian(footer);
                if (size < sizeof(uint) || size > (ulong)footerPosition) return false;
                embedded.Position = footerPosition - (long)size;
                return HasGodotMagic(embedded);
            }
            catch (Exception ex) when (IsAccessError(ex)) { return false; }
        }

        private static bool HasGodotMagic(Stream stream)
        {
            Span<byte> header = stackalloc byte[4];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length &&
                header.SequenceEqual("GDPC"u8);
        }

        private bool EvidenceFile(string path) =>
            !IsExcluded(path) && File.Exists(path) && TryAttributes(path, out var attributes) &&
            !attributes.HasFlag(FileAttributes.Directory);

        private static bool IsRendererCandidate(string path)
        {
            return CatalogPaths.IsExecutable(path) && CatalogPaths.DefaultRole(path) == CatalogMatchKind.Unknown;
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
            string id = CatalogPaths.FolderId(path);
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
            if (CheckDirectory(path)) return path;
            _sourceUnavailable |= !IsExcluded(path);
            return null;
        }

        private bool CheckDirectory(string path)
        {
            if (IsExcluded(path) || CatalogPaths.IsRedistributablePath(path)) return false;
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

        private readonly record struct DirectoryEntry(string Path, bool IsDirectory);

        private IEnumerable<DirectoryEntry?> VisitEntries(string directory, Func<string, bool>? manifestName = null,
            bool count = true)
        {
            using var entries = OpenDirectory(directory);
            if (entries == null) yield break;
            int validatedBatch = _batch;
            while (true)
            {
                _cancel.ThrowIfCancellationRequested();
                if (validatedBatch != _batch)
                {
                    if (!CheckDirectory(directory)) yield break;
                    validatedBatch = _batch;
                }
                if (!MoveNext(entries, directory)) yield break;
                _inspectedEntries++;
                var (path, attributes) = entries.Current;
                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                string leaf = Path.GetFileName(path);
                if (manifestName != null ? isDirectory || !manifestName(leaf) : !isDirectory && !CatalogPaths.IsExecutable(path))
                {
                    yield return null;
                    continue;
                }
                _relevantSteps++;
                if (count) _visited++;
                if (!CatalogPaths.SafeLeaf(leaf) || IgnoredNames.Contains(leaf) || IsExcluded(path))
                {
                    yield return null;
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (count) AddIssue(path, CatalogIssueKind.ReparsePoint);
                    yield return null;
                    continue;
                }
                // Count real candidate visits, excluding the collection's shallow classification pass.
                if (count && manifestName == null && !isDirectory && IsRendererCandidate(path))
                    _candidateExecutablesVisited++;
                yield return new DirectoryEntry(path, isDirectory);
            }
        }

        private IEnumerator<(string Path, FileAttributes Attributes)>? OpenDirectory(string directory)
        {
            if (!CheckDirectory(directory)) return null;
            try
            {
                return new FileSystemEnumerable<(string, FileAttributes)>(directory,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes),
                    new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                    .GetEnumerator();
            }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue(directory, CatalogIssueKind.Unavailable); return null; }
        }

        private bool MoveNext(IEnumerator<(string, FileAttributes)> entries, string directory)
        {
            try { return entries.MoveNext(); }
            catch (Exception ex) when (IsAccessError(ex)) { AddIssue(directory, CatalogIssueKind.Unavailable); return false; }
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

        private void AddIssue(string path, CatalogIssueKind kind)
        {
            if (kind != CatalogIssueKind.ReparsePoint) _installFailures++;
            var issue = new CatalogIssue(path, kind);
            if (_issues.Contains(issue)) return;
            if (_issues.Count < 128) _issues.Add(issue);
            else
            {
                _hasMoreIssues = true;
                if (kind != CatalogIssueKind.ReparsePoint) _hasUnreportedFailures = true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                while (_walk.TryPop(out var cursor)) cursor.Dispose();
                _sources.Clear();
                _launcherInstalls.Clear();
            }
        }

        private static bool IsAccessError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

        private sealed class Candidate(string id, string name, CatalogSource source)
        {
            private CatalogEntry? _cached;
            public string Id { get; } = id;
            public CatalogSource Source => source;
            public HashSet<string> InstallDirectories { get; } = new(Paths);
            public HashSet<string> Executables { get; } = new(Paths);
            public HashSet<string> Renderers { get; } = new(Paths);
            public HashSet<string> DetectedLaunchers { get; } = new(Paths);

            public CatalogEntry ToEntry(CatalogRules rules)
            {
                if (_cached is { } cached && cached.Executables.Length == Executables.Count &&
                    cached.InstallDirectories.Length == InstallDirectories.Count &&
                    (cached.RendererExecutables?.Length ?? 0) == (source is CatalogSource.Steam or CatalogSource.Manual ? 0 : Renderers.Count) &&
                    cached.DetectedLaunchers.Length == DetectedLaunchers.Count)
                    return cached.Rules == rules ? cached : cached with { Rules = rules };
                var ranked = Executables.OrderByDescending(Score).ThenBy(path => path, Paths).ToImmutableArray();
                return _cached = new(Id, name, source, [.. InstallDirectories.Order(Paths)], ranked, rules)
                {
                    RendererExecutables = source is CatalogSource.Steam or CatalogSource.Manual ? null : [.. Renderers.Order(Paths)],
                    DetectedLaunchers = [.. DetectedLaunchers.Order(Paths)],
                };
            }

            private int Score(string path)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (Renderers.Contains(path)) return 4;
                if (DetectedLaunchers.Contains(path)) return 0;
                if (stem.Equals(name, StringComparison.OrdinalIgnoreCase)) return 3;
                if (stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) return 2;
                return stem.EndsWith("Helper", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }
        }
    }
}

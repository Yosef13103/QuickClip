using System.Collections.Immutable;

namespace QuickClip.Discovery;

internal enum CatalogSource { Folder, Steam, Manual, Epic }
internal enum CatalogConfidence { Low, Medium, High }
internal enum CatalogInclusion { Default, Include, Exclude }
internal enum AppClassification { Automatic, Game, Application, Companion, Launcher }
internal enum CatalogActivation { Default, ForegroundOnly }
internal enum CatalogCapturePreference { Default, Window, Monitor }

internal sealed record CatalogRules(
    string? DisplayName = null,
    CatalogInclusion Inclusion = CatalogInclusion.Default,
    AppClassification Classification = AppClassification.Automatic,
    CatalogActivation Activation = CatalogActivation.Default,
    CatalogCapturePreference CapturePreference = CatalogCapturePreference.Default,
    string? ProfileId = null,
    int Priority = 0)
{
    public ImmutableArray<string> LauncherExecutables { get; init; } = [];
}

internal sealed record ManualCatalogEntry(Guid Id, string DisplayName, ImmutableArray<string> Executables);

// Persist these separately from the last scan so offline installations keep their rules.
internal sealed record CatalogOverride(string Id, CatalogRules Rules);

internal sealed record GameScanRoot(string Path, bool IsCollection = false);

internal sealed record CatalogRequest
{
    public bool DiscoverSteam { get; init; }
    public bool DiscoverEpic { get; init; }
    public ImmutableArray<GameScanRoot> GameRoots { get; init; } = [];
    public ImmutableArray<string> SteamLibraryRoots { get; init; } = [];
    public ImmutableArray<string> ExcludedDirectories { get; init; } = [];
    public ImmutableArray<ManualCatalogEntry> ManualEntries { get; init; } = [];
    public ImmutableArray<CatalogOverride> Overrides { get; init; } = [];
    public int MaxEntries { get; init; } = 20_000;
    public int? MaxDepth { get; init; }
}

internal sealed record CatalogEntry(
    string Id, string Name, CatalogSource Source,
    ImmutableArray<string> InstallDirectories, ImmutableArray<string> Executables, CatalogRules Rules)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Rules.DisplayName) ? Name : Rules.DisplayName.Trim();

    // Confidence in the catalog association, rather than in a particular EXE being the game's renderer.
    public CatalogConfidence Confidence => Source switch
    {
        CatalogSource.Manual => CatalogConfidence.High,
        CatalogSource.Steam or CatalogSource.Epic => CatalogConfidence.Medium,
        _ => CatalogConfidence.Low,
    };
}

internal enum CatalogIssueKind { InvalidPath, Unavailable, ReparsePoint, InvalidManifest, InvalidManualEntry, Incomplete, DepthLimit }

internal sealed record CatalogIssue(string Location, CatalogIssueKind Kind);

internal sealed record CatalogResult(
    ImmutableArray<CatalogEntry> Entries, ImmutableArray<CatalogIssue> Issues,
    int VisitedEntries, bool LimitReached, DateTimeOffset CompletedAt)
{
    public ImmutableArray<string> SteamLibraries { get; init; } = [];
    public string? EpicManifestDirectory { get; init; }
    public bool InProgress { get; init; }
    public bool HasMoreIssues { get; init; }
    public bool HasUnreportedFailures { get; init; }
}

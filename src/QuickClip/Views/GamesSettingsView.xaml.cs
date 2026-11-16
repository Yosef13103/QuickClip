using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;
using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Views;

public partial class GamesSettingsView : UserControl
{
    private sealed record LauncherRow(CatalogSource Source, string Name, bool Enabled, bool Supported, string Status)
    {
        public bool CanRefresh => Enabled && Supported;
        public string RefreshName => $"Refresh {Name}";
    }

    private sealed record RootRow(string Path, bool Steam, bool Automatic = false, bool Excluded = false, bool Epic = false,
        string? Key = null, string? Status = null, bool Enabled = true)
    {
        public bool CanToggle => Key != null && !Excluded;
        public bool CanRefresh => CanToggle && Enabled;
        public string RemoveLabel => Excluded ? "Restore" : Automatic ? "Exclude" : "Remove";
        public string Label => ToString();
        public override string ToString() => (Excluded ? "Excluded · " : Epic ? "Detected Epic manifests · " :
            Automatic ? "Detected Steam · " : Steam ? "Steam · " : "Game folder · ") + Path +
            (Status == null ? "" : " · " + Status);
    }
    private static GameDiscoveryCoordinator Discovery => GameCatalogRuntime.Discovery;
    private bool _basic;
    private int _eligibleCount;
    private GameCatalogSettings? _renderedState;
    private List<GameRow> _rows = [];
    private LauncherRow[] _launchers = [];
    private ListCollectionView? _gamesView;
    private int _uniqueCount, _manualCount, _retainedCount, _offlineCount;
    private GameRow? _expanded;
    private string _query = "";
    private static GameCatalogSettings State => AppSettings.Current.Games ?? new();

    public GamesSettingsView()
    {
        InitializeComponent();
        Render();
        IsVisibleChanged += async (_, _) =>
        {
            Discovery.Changed -= ShowDiscoveryStatus;
            if (!IsVisible) return;
            Discovery.Changed += ShowDiscoveryStatus;
            ShowDiscoveryStatus();
            await Discovery.RequestAsync();
        };
    }

    // Even a ListBox with its scrollbar disabled owns an internal ScrollViewer
    // that consumes wheel events. Forward them to the page's virtualized scroller.
    private void SourceList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        e.Handled = true;
        var scroller = FindPageScrollViewer(Games);
        scroller?.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent
        });
    }

    private static ScrollViewer? FindPageScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindPageScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(element, i)) is { } child)
                return child;
        return null;
    }

    private void ShowDiscoveryStatus()
    {
        if (!ReferenceEquals(_renderedState, AppSettings.Current.Games)) Render();
        ScanButton.IsEnabled = !Discovery.IsRunning;
        ScanButton.Content = Discovery.IsPaused ? "Resume scan" : "Scan Now";
        CancelButton.IsEnabled = Discovery.IsRunning;
        if (_basic) Status.Text = Discovery.IsPaused ? "Game search paused." : Discovery.IsRunning
            ? $"Looking for games · {Discovery.ActiveSourceVisited:N0} items checked" : BasicReadiness;
        else if (Discovery.Status.Length != 0) Status.Text = Discovery.Status;
        else if (State.Snapshot?.InProgress == true)
            Status.Text = "Incomplete · a previous session ended during the scan.";
    }

    internal void UseBasicPresentation(bool basic)
    {
        if (_basic == basic) return;
        _basic = basic;
        LibrarySources.Visibility = ScanDetails.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        ScanHelp.Visibility = TargetingHelp.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        ScanHelp.Text = basic ? "Use Add Game if a game is missing. Manage game stores and folders in Advanced settings."
            : "Automatic scans refresh enabled sources. You can pause and resume a scan here.";
        TargetingHelp.Text = basic ? "Games marked ready can start recording when recording is on and the display is allowed."
            : "Targeting rules apply to game detection. Manual entire-display capture includes anything visible on the approved display, including games disabled here.";
        foreach (var row in _rows) row.UseBasicPresentation(basic);
        ShowDiscoveryStatus();
        UpdateCount();
    }

    internal string BasicReadiness
    {
        get
        {
            if (!ReferenceEquals(_renderedState, AppSettings.Current.Games)) Render();
            return _eligibleCount == 0 ? "No games ready yet. Search or add a game."
                : $"{_eligibleCount:N0} {(_eligibleCount == 1 ? "game" : "games")} ready for automatic detection.";
        }
    }

    private void SetState(GameCatalogSettings state)
    {
        AppSettings.Current.Games = CatalogClaims.Reconfigure(state);
        AppSettings.Current.Save();
        Render();
        _ = Discovery.SettingsChangedAsync();
    }

    private async void AutoScan_Click(object sender, RoutedEventArgs e)
    {
        SetState(State with { AutoScan = AutoScan.IsChecked == true });
        if (State.AutoScan) await Discovery.RequestAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Discovery.RequestAsync(manual: true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Discovery.Pause();

    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: LauncherRow { Supported: true } row } box) return;
        bool enabled = box.IsChecked == true;
        var state = State;
        SetState(row.Source switch
        {
            CatalogSource.Steam => state with { DiscoverSteam = enabled },
            CatalogSource.Epic => state with { DiscoverEpic = enabled },
            CatalogSource.Gog => state with { DiscoverGog = enabled },
            _ => state with { EnabledLaunchers = enabled
                ? state.EnabledLaunchers.Add(row.Source) : state.EnabledLaunchers.Remove(row.Source) },
        });
    }

    private async void RefreshLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LauncherRow { CanRefresh: true } row })
            await Discovery.RequestAsync(manual: true, sourceKey: new CatalogScope(row.Source).Key);
    }

    private void RootEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: RootRow { Key: { } key } } box) return;
        var state = State;
        var disabled = state.DisabledSourceKeys;
        SetState(state with { DisabledSourceKeys = box.IsChecked == true ? disabled.Remove(key) : disabled.Add(key) });
    }

    private void EnableStores_Click(object sender, RoutedEventArgs e)
    {
        SetState(State with { DiscoverSteam = true, DiscoverEpic = true, DiscoverGog = true,
            EnabledLaunchers = [.. Enum.GetValues<CatalogSource>().Where(LocalLaunchers.IsSupported)] });
    }

    private async void RefreshSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RootRow { CanRefresh: true, Key: { } key } } &&
            CatalogScope.Enabled(State).Any(scope => scope.Key == key))
            await Discovery.RequestAsync(manual: true, sourceKey: key);
    }

    private void Steam_Click(object sender, RoutedEventArgs e) => Approve(true);
    private void Collection_Click(object sender, RoutedEventArgs e) => Approve(false);

    private void Approve(bool steam)
    {
        var dialog = new OpenFolderDialog
        {
            Title = steam ? "Approve a Steam library containing steamapps" :
                "Choose a folder containing multiple games",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (CatalogPaths.LocalPath(dialog.FolderName) is not { Length: > 3 } path)
        {
            SourceError.Text = "Choose a folder on a local drive.";
            return;
        }
        var request = State.Request;
        if (request.GameRoots.Length + request.SteamLibraryRoots.Length >= 128)
        {
            SourceError.Text = "Remove a game folder before adding another.";
            return;
        }
        if (steam ? request.SteamLibraryRoots.Contains(path, StringComparer.OrdinalIgnoreCase)
            : request.GameRoots.Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            SourceError.Text = "This folder is already in your game sources.";
            return;
        }
        SetState(State with { Request = steam ? request with { SteamLibraryRoots = request.SteamLibraryRoots.Add(path) }
            : request with { GameRoots = request.GameRoots.Add(new(path, IsCollection: true)) } });
        SourceError.Text = "";
        _ = Discovery.RequestAsync(manual: true, sourceKey: new CatalogScope(
            steam ? CatalogSource.Steam : CatalogSource.Folder, path, IsCollection: !steam).Key);
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RootRow root }) return;
        var request = State.Request;
        SetState(State with { Request = root.Excluded
            ? request with { ExcludedDirectories = request.ExcludedDirectories.Remove(root.Path) }
            : root.Automatic
            ? request with { ExcludedDirectories = request.ExcludedDirectories.Add(root.Path) }
            : root.Steam ? request with { SteamLibraryRoots = request.SteamLibraryRoots.RemoveAll(path =>
                string.Equals(CatalogPaths.LocalPath(path), root.Path, StringComparison.OrdinalIgnoreCase)) }
            : request with { GameRoots = request.GameRoots.RemoveAll(row =>
                string.Equals(CatalogPaths.LocalPath(row.Path), root.Path, StringComparison.OrdinalIgnoreCase)) },
            DisabledSourceKeys = root.Key is { } key ? State.DisabledSourceKeys.Remove(key) : State.DisabledSourceKeys });
        Status.Text = root.Excluded ? "Exclusion removed. Scan again to find this source."
            : root.Automatic ? "Launcher source excluded." : "Game folder removed.";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        ShowAddError(null);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var file = new MenuItem { Header = "Executable or shortcut (more accurate)…" };
        file.Click += (_, _) => PickExecutables(null);
        var folder = new MenuItem { Header = "Add game folder…" };
        folder.Click += (_, _) => PickGameFolder();
        menu.Items.Add(file);
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameRow { IsManual: true } row })
        {
            row.ActionError = null;
            PickExecutables(row.Entry);
        }
    }

    internal static string? ValidateManualGameExecutables(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Any(path => CatalogPaths.LocalPath(path) == null || !CatalogPaths.IsExecutable(path)))
            return "Choose one or more local Windows executable files (.exe).";
        return null;
    }

    private void PickExecutables(CatalogEntry? entry, string? directory = null)
    {
        var dialog = new OpenFileDialog { Filter = "Executable or shortcut|*.exe;*.lnk", Multiselect = true,
            Title = "Select game executables or shortcuts", CheckFileExists = true, DereferenceLinks = false,
            InitialDirectory = directory ?? "" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddSelection(dialog.FileNames, entry);
    }

    private void ShowAddError(string? message)
    {
        AddError.Text = message;
        AddError.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PickGameFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Add a game's installation folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (CatalogPaths.LocalPath(dialog.FolderName) is not { Length: > 3 } path)
        {
            ShowAddError("Choose a game installation folder on a local drive.");
            return;
        }
        var request = State.Request;
        if (request.GameRoots.Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowAddError("This folder is already in your game sources.");
            return;
        }
        if (request.GameRoots.Length + request.SteamLibraryRoots.Length >= 128)
        {
            ShowAddError("Remove a game folder before adding another.");
            return;
        }
        SetState(State with { Request = request with { GameRoots = request.GameRoots.Add(new(path)) } });
        Status.Text = "Game folder added. Scan queued…";
        _ = Discovery.RequestAsync(manual: true, sourceKey: new CatalogScope(CatalogSource.Folder, path).Key);
    }

    private void AddSelection(IReadOnlyList<string> inputs, CatalogEntry? entry)
    {
        void Error(string message)
        {
            if (entry != null && _rows.FirstOrDefault(row => row.Entry.Id == entry.Id) is { } row)
                row.ActionError = message;
            else ShowAddError(message);
        }

        if (inputs.Count == 0)
        {
            Error("Select at least one game executable.");
            return;
        }
        ManualGameInput.Selection[] selections;
        try { selections = inputs.Select(ManualGameInput.Read).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error(ex.Message);
            return;
        }

        var owner = Window.GetWindow(this);
        foreach (var item in selections.Where(item => item.Shortcut != null).DistinctBy(item => item.Executable, StringComparer.OrdinalIgnoreCase))
        {
            if (!ModalDialog.ConfirmAction(owner,
                $"{Path.GetFileName(item.Shortcut)} points to this executable:\n{item.Executable}",
                "Confirm shortcut target", "Use executable", "Cancel", destructive: false)) return;
        }
        var paths = selections.Select(item => item.Executable).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (paths.FirstOrDefault(path => GameDetector.ApplyProcessRole(path, new(CatalogPaths.DefaultRole(path))).Kind
            != CatalogMatchKind.Unknown) is { } nonGame)
        {
            Error($"{Path.GetFileName(nonGame)} is blocked from automatic recording. Choose a game executable.");
            return;
        }
        if (paths.Any(CatalogPaths.IsJavaRuntime) && !ModalDialog.ConfirmAction(owner,
            "This Java executable can run more than one app. Only add it if this exact file is dedicated to your game. For a shared Java installation, use manual recording on an allowed display.",
            "Confirm dedicated Java executable", "Add dedicated executable", "Cancel", destructive: false)) return;
        var state = State;
        var request = state.Request;
        var existing = _rows.FirstOrDefault(row => row.IsManual && row.Entry.Id != entry?.Id &&
            row.Entry.Executables.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any());
        if (existing != null)
        {
            Error($"A selected file is already in {existing.Entry.DisplayName}.");
            return;
        }
        string name = entry?.DisplayName ?? ManualGameInput.FriendlyName(paths[0]);
        if (entry == null)
        {
            if (TextPrompt.Ask(owner, "Game display name", name, "Add Game") is not { Length: > 0 } confirmed) return;
            name = confirmed.Trim();
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        Guid id = entry == null ? Guid.NewGuid() : Guid.ParseExact(entry.Id[7..], "N");
        SetState(state with { Request = request with
        {
            ManualEntries = request.ManualEntries.RemoveAll(item => item.Id == id).Add(new(id, name, paths)),
        } });
        Search.Text = "";
        Games.SelectedItem = _rows.FirstOrDefault(row => row.Entry.Id == "manual:" + id.ToString("N"));
        Status.Text = paths.Any(path => state.BlockedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase))
            ? "Entry saved. Blocked paths stay ignored until restored under Blocked executables."
            : "Entry saved.";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameRow { Entry: { Source: CatalogSource.Manual } entry } }) return;
        var request = State.Request;
        Guid id = Guid.ParseExact(entry.Id[7..], "N");
        SetState(State with { Request = request with
        {
            ManualEntries = request.ManualEntries.RemoveAll(item => item.Id == id),
            Overrides = request.Overrides.RemoveAll(item => item.Id == entry.Id),
        } });
        Status.Text = "Manual entry removed. Game files are unchanged.";
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameRow row }) return;
        var entry = row.Entry;
        if (TextPrompt.Ask(Window.GetWindow(this), "Game display name", entry.DisplayName, "Save") is { Length: > 0 } name)
            SaveRules(entry, entry.Rules with { DisplayName = name.Trim() });
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameRow row }) return;
        var rules = row.Entry.Rules;
        SaveRules(row.Entry, rules with { Inclusion = rules.Inclusion == CatalogInclusion.Exclude
            ? CatalogInclusion.Include : CatalogInclusion.Exclude });
    }

    private void Launcher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameRow row, CommandParameter: string path }) return;
        var entry = row.Entry;
        var launchers = entry.Rules.LauncherExecutables;
        SaveRules(entry, entry.Rules with { LauncherExecutables = launchers.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? launchers.RemoveAll(value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase)) : launchers.Add(path) });
    }

    private void BlockFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Windows executables|*.exe", Multiselect = true,
            Title = "Select executables to block for automatic game detection", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BlockExecutables(dialog.FileNames);
    }

    private void BlockSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string path, DataContext: GameRow row }) BlockExecutables([path], row);
    }

    private void BlockExecutables(IReadOnlyList<string> paths, GameRow? row = null)
    {
        string? error = ValidateManualGameExecutables(paths);
        if (row == null) BlockedError.Text = error;
        else row.ActionError = error;
        if (error != null) return;
        var blocked = State.BlockedExecutables.Concat(paths.Select(path => CatalogPaths.LocalPath(path)!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        SetState(State with { BlockedExecutables = blocked });
        Status.Text = "Selected executables are ignored for automatic game detection.";
    }

    private void RestoreFile_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedFiles.SelectedItem is not string path) return;
        SetState(State with { BlockedExecutables = State.BlockedExecutables.Remove(path) });
        Status.Text = "Executable restored to its catalog rules.";
    }

    private void SaveRules(CatalogEntry entry, CatalogRules rules)
    {
        var request = State.Request;
        var prior = request.Overrides.LastOrDefault(item => item.Id == entry.Id);
        var overrides = request.Overrides.RemoveAll(item => item.Id == entry.Id).Add(new(entry.Id, rules)
        {
            DeniedExecutables = rules.Inclusion == CatalogInclusion.Exclude
                ? [.. (prior?.DeniedExecutables ?? []).Concat(entry.Executables).Distinct(StringComparer.OrdinalIgnoreCase)] : [],
        });
        if (entry.Rules.Inclusion != rules.Inclusion)
            overrides = [.. overrides.Select(item => entry.AliasIds.Contains(item.Id) ?
                item with { Rules = item.Rules with { Inclusion = rules.Inclusion } } : item)];
        SetState(State with { Request = request with
        {
            Overrides = overrides,
        } });
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _query = Search.Text.Trim();
        _gamesView?.Refresh();
        UpdateCount();
    }

    private void Render()
    {
        _renderedState = AppSettings.Current.Games;
        var state = _renderedState ?? new();
        AutoScan.IsChecked = state.AutoScan;
        var claims = (state.SourceClaims ?? []).ToDictionary(claim => claim.Scope.Key, StringComparer.Ordinal);
        string DescribeSource(string key)
        {
            if (state.DisabledSourceKeys.Contains(key)) return "Disabled";
            if (key == Discovery.ActiveSourceKey && (Discovery.IsRunning || Discovery.IsPaused))
                return Discovery.IsPaused ? "Scan paused" : "Scanning";
            if (!claims.TryGetValue(key, out var claim)) return "Awaiting scan";
            var issues = claim.Result.Issues;
            string count = $"{claim.DiscoveredCount ?? claim.Result.Entries.Length:N0} found";
            if (claim.Verified && claim.Scope.Kind == CatalogSource.Steam && claim.Scope.Root == null &&
                claim.Result.SteamLibraries.IsEmpty) return "Not installed";
            if (claim.Verified) return $"Available · {count}";
            if (issues.Any(issue => issue.Kind == CatalogIssueKind.Unavailable) || claim.Result.SourceUnavailable)
                return $"Inaccessible or offline · {count}";
            if (issues.Any(issue => issue.Kind == CatalogIssueKind.SourceMissing) &&
                issues.All(issue => issue.Kind is CatalogIssueKind.SourceMissing or CatalogIssueKind.ReparsePoint))
                return claim.Scope.Root != null ? "Source missing" :
                    claim.Result.Entries.IsEmpty ? "Not installed" : "Not currently found";
            return $"Incomplete · {count}";
        }
        var enabled = CatalogScope.Enabled(state).Select(scope => scope.Key).ToHashSet(StringComparer.Ordinal);
        LauncherRow[] launchers = new[]
        {
            CatalogSource.Steam, CatalogSource.Epic, CatalogSource.Gog, CatalogSource.Xbox, CatalogSource.Ea, CatalogSource.Riot,
            CatalogSource.BattleNet, CatalogSource.Ubisoft, CatalogSource.Rockstar, CatalogSource.Heroic, CatalogSource.Origin,
            CatalogSource.Amazon, CatalogSource.Itch,
        }.Select(source =>
        {
            string key = new CatalogScope(source).Key;
            bool supported = source is CatalogSource.Steam or CatalogSource.Epic or CatalogSource.Gog || LocalLaunchers.IsSupported(source);
            bool active = enabled.Contains(key);
            return new LauncherRow(source, LocalLaunchers.Name(source), active, supported,
                !supported ? "Unsupported" : active ? DescribeSource(key) : "Disabled");
        }).Concat([
            new LauncherRow(CatalogSource.Manual, "Other Microsoft Store installs", false, false, "Unsupported · Add Game for accessible executables"),
        ]).ToArray();
        if (!_launchers.SequenceEqual(launchers)) Launchers.ItemsSource = _launchers = launchers;
        RootRow[] epic = state.Snapshot?.EpicManifestDirectory is { } manifestDirectory &&
            !state.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(manifestDirectory, root))
            ? [new(manifestDirectory, Steam: false, Automatic: true, Epic: true)] : [];
        Roots.ItemsSource = CatalogScope.Configured(state).Where(scope => scope.Root != null)
            .Select(scope => new RootRow(scope.Root!, scope.Kind == CatalogSource.Steam, Key: scope.Key, Status: DescribeSource(scope.Key), Enabled: enabled.Contains(scope.Key)))
            .Concat((state.Snapshot?.SteamLibraries ?? []).Where(path =>
                !state.Request.SteamLibraryRoots.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                !state.Request.ExcludedDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Select(path => new RootRow(path, true, Automatic: true)))
            .Concat(epic)
            .Concat(state.Request.ExcludedDirectories.Select(path => new RootRow(path, false, Excluded: true))).ToArray();
        LastScan.Text = state.Snapshot is { CompletedAt: var completed } && completed != default
            ? $"Last finished scan · {completed.ToLocalTime():g}" : "No finished scan.";
        BlockedFiles.ItemsSource = state.BlockedExecutables;
        // Old saved snapshots may predate the coordinator's presentation filter.
        // Show SourceMissing only for an explicitly configured folder; never
        // list each absent optional launcher as a scan warning.
        var explicitMissing = claims.Values.Where(claim => claim.Scope.Root != null)
            .SelectMany(claim => claim.Result.Issues)
            .Where(issue => issue.Kind == CatalogIssueKind.SourceMissing).ToHashSet();
        var issues = state.Snapshot?.Issues.Where(issue =>
            issue.Kind != CatalogIssueKind.SourceMissing || explicitMissing.Contains(issue))
            .Select(ScanIssueText) ?? [];
        ScanIssues.ItemsSource = state.Snapshot?.HasMoreIssues == true ? issues.Append("Additional scan issues are omitted.").ToArray()
            : issues.ToArray();
        RenderRows(CatalogClaims.Start(state));
    }

    internal void RenderRows(GameCatalogSettings state)
    {
        string? selected = (Games.SelectedItem as GameRow)?.Entry.Id;
        var entries = GameCatalogRuntime.Rows(state);
        var offline = entries.Where(entry => entry.IsUnverified).Select(entry => entry.Id)
            .Concat(state.OfflineIds).ToHashSet(StringComparer.Ordinal);
        var catalog = new GameCatalogIndex(new(entries, [], 0, false, default), CatalogClaims.BlockedExecutables(state));
        var retained = entries.Where(entry => entry.IsUnverified || !entry.UnverifiedExecutables.IsEmpty)
            .Select(entry => entry.Id).Concat(state.RetainedIds).ToHashSet(StringComparer.Ordinal);
        _manualCount = entries.Count(entry => entry.Source == CatalogSource.Manual);
        _uniqueCount = entries.Length - _manualCount;
        var knownPaths = entries.Where(entry => entry.Source != CatalogSource.Manual).SelectMany(entry => entry.Executables)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(entry => entry.Source == CatalogSource.Manual))
        {
            if (entry.Executables.IsEmpty || entry.Executables.Any(path => !knownPaths.Contains(path))) _uniqueCount++;
            knownPaths.UnionWith(entry.Executables);
        }
        _retainedCount = entries.Count(entry => entry.Source != CatalogSource.Manual && retained.Contains(entry.Id));
        _offlineCount = entries.Count(entry => entry.Source != CatalogSource.Manual && offline.Contains(entry.Id));
        var existing = _rows.ToDictionary(row => row.Entry.Id, StringComparer.Ordinal);
        _eligibleCount = 0;
        var rows = new List<GameRow>(entries.Length);
        bool refilter = false;
        foreach (var entry in entries)
        {
            if (!existing.TryGetValue(entry.Id, out var row)) row = new(entry);
            else if (_query.Length > 0) refilter |= row.Matches(_query) != GameRow.Matches(entry, _query);
            row.Update(entry, offline.Contains(entry.Id), state.BlockedExecutables, catalog);
            row.UseBasicPresentation(_basic);
            if (row.DetectionReady) _eligibleCount++;
            rows.Add(row);
        }
        if (_expanded != null && !entries.Any(entry => entry.Id == _expanded.Entry.Id))
            _expanded = null;
        if (_gamesView == null || !_rows.SequenceEqual(rows))
        {
            _rows = rows;
            _gamesView = new ListCollectionView(rows) { Filter = item => ((GameRow)item).Matches(_query) };
            GameItems.Collection = _gamesView;
            Games.SelectedItem = rows.FirstOrDefault(row => row.Entry.Id == selected && row.Matches(_query));
        }
        else if (refilter) _gamesView.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (Count != null && _gamesView != null)
        {
            Count.Text = (_basic ? $"{_uniqueCount:N0} games and apps found" :
                $"Catalog · {_uniqueCount:N0} unique games and apps · {_manualCount:N0} manual entries")
                + (_query.Length == 0 ? "" : $" · {_gamesView.Count:N0} shown");
            DiscoveryCountDetail.Text = $"{_retainedCount:N0} entries with cached paths · {_offlineCount:N0} awaiting verification";
            DiscoveryCountDetail.Visibility = !_basic && _retainedCount + _offlineCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: GameRow row }) return;
        if (!ToggleDetails(row) || !row.IsManual || row.CheckingExecutables || row.Entry.Executables.IsEmpty) return;
        var paths = row.Entry.Executables;
        row.CheckingExecutables = true;
        try
        {
            var unavailable = await Task.Run(() => paths.Where(path => !File.Exists(path)).ToArray());
            if (row.Entry.Executables.SequenceEqual(paths)) row.SetUnavailableExecutables(unavailable);
        }
        finally { row.CheckingExecutables = false; }
    }

    internal bool ToggleDetails(GameRow row)
    {
        bool open = !row.IsExpanded;
        if (_expanded != null) _expanded.IsExpanded = false;
        _expanded = open ? row : null;
        if (open)
        {
            row.IsExpanded = true;
            Games.SelectedItem = row;
        }
        return open;
    }

    internal static string ScanIssueText(CatalogIssue issue) => (issue.Kind switch
    {
        CatalogIssueKind.Unavailable => "Source unavailable · reconnect the drive or check folder access",
        CatalogIssueKind.SourceMissing => "Source missing",
        CatalogIssueKind.InvalidManifest => "Unreadable launcher manifest · check the installation and rescan",
        CatalogIssueKind.InvalidManualEntry => "Invalid manual entry · edit its executable selection",
        CatalogIssueKind.ReparsePoint => "Linked folder skipped · approve its actual local location",
        CatalogIssueKind.InvalidPath => "Invalid source path · choose a local folder",
        CatalogIssueKind.DepthLimit => "Configured folder depth reached · deeper entries were not scanned",
        _ => "Previous scan was incomplete · rescan this source",
    }) + " · " + issue.Location;
}

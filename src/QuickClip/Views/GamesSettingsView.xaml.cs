using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;
using QuickClip.Discovery;

namespace QuickClip.Views;

public partial class GamesSettingsView : UserControl
{
    private sealed record RootRow(string Path, bool Steam, bool Automatic = false, bool Excluded = false, bool Epic = false)
    {
        public override string ToString() => (Excluded ? "Excluded · " : Epic ? "Detected Epic manifests · " :
            Automatic ? "Detected Steam · " : Steam ? "Steam · " : "Game folder · ") + Path;
    }
    private static GameDiscoveryCoordinator Discovery => GameCatalogRuntime.Discovery;
    private bool _rendering;
    private GameCatalogSettings? _renderedState;
    private List<GameRow> _rows = [];
    private ListCollectionView? _gamesView;
    private int _manualCount, _retainedCount, _offlineCount;
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

    private void ShowDiscoveryStatus()
    {
        if (!ReferenceEquals(_renderedState, AppSettings.Current.Games)) Render();
        ScanButton.IsEnabled = !Discovery.IsRunning;
        ScanButton.Content = Discovery.IsPaused ? "Resume scan" : "Scan Now";
        CancelButton.IsEnabled = Discovery.IsRunning;
        if (Discovery.Status.Length != 0) Status.Text = Discovery.Status;
        else if (State.Snapshot?.InProgress == true)
            Status.Text = "Incomplete · a previous session ended during the scan.";
    }

    private void SetState(GameCatalogSettings state)
    {
        AppSettings.Current.Games = state;
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
            Status.Text = "Choose a folder on a local drive.";
            return;
        }
        var request = State.Request;
        if (request.GameRoots.Length + request.SteamLibraryRoots.Length >= 128)
        {
            Status.Text = "Remove an approved folder before adding another.";
            return;
        }
        if (steam ? request.SteamLibraryRoots.Contains(path, StringComparer.OrdinalIgnoreCase)
            : request.GameRoots.Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        SetState(State with { Request = steam ? request with { SteamLibraryRoots = request.SteamLibraryRoots.Add(path) }
            : request with { GameRoots = request.GameRoots.Add(new(path, IsCollection: true)) } });
        Status.Text = "Folder approved. Select Scan Now to scan it.";
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (Roots.SelectedItem is not RootRow root) return;
        var request = State.Request;
        SetState(State with { Request = root.Excluded
            ? request with { ExcludedDirectories = request.ExcludedDirectories.Remove(root.Path) }
            : root.Automatic
            ? request with { ExcludedDirectories = request.ExcludedDirectories.Add(root.Path) }
            : root.Steam ? request with { SteamLibraryRoots = request.SteamLibraryRoots.Remove(root.Path) }
            : request with { GameRoots = request.GameRoots.RemoveAll(row => row.Path == root.Path) } });
        Status.Text = root.Excluded ? "Exclusion removed. Scan again to find this source."
            : root.Automatic ? "Launcher source excluded." : "Approval removed.";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var file = new MenuItem { Header = "Executable or shortcut (more accurate)…" };
        file.Click += (_, _) => PickExecutables(null);
        var folder = new MenuItem { Header = "Game installation folder…" };
        folder.Click += async (_, _) => await PickGameFolderAsync();
        menu.Items.Add(file);
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameRow { Entry: { Source: CatalogSource.Manual } entry } })
            PickExecutables(entry);
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

    private async Task PickGameFolderAsync()
    {
        var owner = Window.GetWindow(this);
        var dialog = new OpenFolderDialog { Title = "Choose one game's installation folder" };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            var (paths, limited) = await Task.Run(() => ManualGameInput.FindCandidates(dialog.FolderName));
            if (paths.Count == 0)
            {
                Status.Text = limited ? "The folder search was incomplete. Choose the executable directly."
                    : "No likely game executable was found. Choose the executable directly.";
                PickExecutables(null, dialog.FolderName);
                return;
            }
            if (owner is not { IsVisible: true, Content: Panel }) return;
            var list = new ListBox { SelectionMode = SelectionMode.Extended, MaxHeight = 260, MinHeight = 70,
                ItemsSource = paths.Select(path => Path.GetRelativePath(dialog.FolderName, path)).ToArray() };
            if (paths.Count == 1) list.SelectedIndex = 0;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = limited ? "Search incomplete. Select the game's executable or browse for it."
                : "Select the executable or executables that belong to this game.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(list);
            var browse = new Button { Content = "Browse for another executable…", HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(browse);
            string[]? other = null;
            browse.Click += (_, _) =>
            {
                var picker = new OpenFileDialog { Filter = "Executable or shortcut|*.exe;*.lnk", Multiselect = true,
                    CheckFileExists = true, DereferenceLinks = false, InitialDirectory = dialog.FolderName };
                if (picker.ShowDialog(owner) != true) return;
                other = picker.FileNames;
                list.UnselectAll();
                browse.Content = other.Length == 1 ? Path.GetFileName(other[0]) : $"{other.Length} executables selected";
            };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItems.Count > 0) other = null;
            };
            if (!ModalDialog.Run(owner, "Choose game executables", panel, "Continue", "Cancel", safeDefault: true)) return;
            AddSelection(other ?? list.SelectedItems.Cast<string>()
                .Select(path => Path.Combine(dialog.FolderName, path)).ToArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
    }

    private void AddSelection(IReadOnlyList<string> inputs, CatalogEntry? entry)
    {
        if (inputs.Count == 0)
        {
            Status.Text = "Select at least one game executable.";
            return;
        }
        ManualGameInput.Selection[] selections;
        try { selections = inputs.Select(ManualGameInput.Read).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = ex.Message;
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
        var state = State;
        var request = state.Request;
        var entries = GameCatalogRuntime.Rows(state);
        var existing = entries.Where(row => row.Id != entry?.Id &&
            row.Executables.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any()).ToArray();
        if (existing.Length > 0)
        {
            if (existing is [var match] && paths.All(path => match.Executables.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                var catalogRow = _rows.FirstOrDefault(row => row.Entry.Id == match.Id);
                bool approved = catalogRow?.RulesAfterApproval(paths) != null &&
                    ModalDialog.ConfirmAction(owner,
                        $"Allow the selected executables in {match.DisplayName} to trigger replay when they own the game window?",
                        "Approve game executables", "Approve executables", "Keep current settings", destructive: false);
                if (approved)
                {
                    // A discovery update can refresh the catalog while the confirmation is open.
                    catalogRow = _rows.FirstOrDefault(row => row.Entry.Id == match.Id);
                    if (catalogRow?.RulesAfterApproval(paths) is { } confirmedRules)
                        SaveRules(catalogRow.Entry, confirmedRules);
                    else approved = false;
                }
                Search.Text = "";
                Games.SelectedItem = _rows.FirstOrDefault(row => row.Entry.Id == match.Id);
                if (Games.SelectedItem is { } selected) Games.ScrollIntoView(selected);
                Status.Text = approved ? ExecutablesApproved :
                    $"Already in the catalog as {match.DisplayName}. Open its details to check executable eligibility.";
            }
            else Status.Text = "Some executables already belong to another catalog entry. Select only new executables.";
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
            ? "Entry saved. Blocked paths stay ignored until restored under Ignored executables."
            : paths.Any(path => CatalogPaths.DefaultRole(path) == CatalogMatchKind.Companion)
                ? "Companion apps do not trigger game recording." : "Entry saved.";
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

    private void Classification_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || sender is not ComboBox { IsKeyboardFocusWithin: true, DataContext: GameRow row,
                SelectedValue: AppClassification classification } || classification == row.Entry.Rules.Classification) return;
        SaveRules(row.Entry, row.Entry.Rules with { Classification = classification });
    }

    private void Launcher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameRow row, CommandParameter: string path }) return;
        var entry = row.Entry;
        var launchers = entry.Rules.LauncherExecutables;
        SaveRules(entry, entry.Rules with { LauncherExecutables = launchers.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? launchers.RemoveAll(value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase)) : launchers.Add(path) });
    }

    private void ApproveExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameRow row, CommandParameter: string path }) return;
        if (row.RulesAfterApproval([path]) == null)
        {
            Status.Text = "This executable cannot be approved. Check its detection status.";
            return;
        }
        if (!ModalDialog.ConfirmAction(Window.GetWindow(this),
            $"Allow {Path.GetFileName(path)} to trigger replay when it owns the game window?",
            "Approve game executable", "Approve executable", "Cancel", destructive: false)) return;
        if (!_rows.Contains(row) || row.RulesAfterApproval([path]) is not { } rules)
        {
            Status.Text = "This executable cannot be approved. Check its detection status.";
            return;
        }
        SaveRules(row.Entry, rules);
        Status.Text = ExecutablesApproved;
    }

    private const string ExecutablesApproved = "Approval saved. Replay requires a verified game window and authorized display.";

    private void BlockFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Windows executables|*.exe", Multiselect = true,
            Title = "Select executables to ignore for automatic game detection", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BlockExecutables(dialog.FileNames);
    }

    private void BlockSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string path }) BlockExecutables([path]);
    }

    private void BlockExecutables(IReadOnlyList<string> paths)
    {
        if (ValidateManualGameExecutables(paths) is { } error)
        {
            Status.Text = error;
            return;
        }
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
        SetState(State with { Request = request with
        {
            Overrides = request.Overrides.RemoveAll(item => item.Id == entry.Id).Add(new(entry.Id, rules)),
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
        RootRow[] epic = state.Snapshot?.EpicManifestDirectory is { } manifestDirectory &&
            !state.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(manifestDirectory, root))
            ? [new(manifestDirectory, Steam: false, Automatic: true, Epic: true)] : [];
        Roots.ItemsSource = state.Request.SteamLibraryRoots.Select(path => new RootRow(path, true))
            .Concat((state.Snapshot?.SteamLibraries ?? []).Where(path =>
                !state.Request.SteamLibraryRoots.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                !state.Request.ExcludedDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Select(path => new RootRow(path, true, Automatic: true)))
            .Concat(epic)
            .Concat(state.Request.GameRoots.Select(root => new RootRow(root.Path, false)))
            .Concat(state.Request.ExcludedDirectories.Select(path => new RootRow(path, false, Excluded: true))).ToArray();
        LastScan.Text = state.Snapshot is { CompletedAt: var completed } && completed != default
            ? $"Last finished scan · {completed.ToLocalTime():g}" : "No finished scan.";
        BlockedFiles.ItemsSource = state.BlockedExecutables;
        var issues = state.Snapshot?.Issues.Select(ScanIssueText) ?? [];
        ScanIssues.ItemsSource = state.Snapshot?.HasMoreIssues == true ? issues.Append("Additional scan issues are omitted.").ToArray()
            : issues.ToArray();
        RenderRows(state);
    }

    internal void RenderRows(GameCatalogSettings state)
    {
        string? selected = (Games.SelectedItem as GameRow)?.Entry.Id;
        var entries = GameCatalogRuntime.Rows(state);
        var offline = state.OfflineIds.ToHashSet(StringComparer.Ordinal);
        var catalog = new GameCatalogIndex(new(entries, [], 0, false, default), state.BlockedExecutables);
        var retained = state.RetainedIds.ToHashSet(StringComparer.Ordinal);
        _manualCount = entries.Count(entry => entry.Source == CatalogSource.Manual);
        _retainedCount = entries.Count(entry => entry.Source != CatalogSource.Manual && retained.Contains(entry.Id));
        _offlineCount = entries.Count(entry => entry.Source != CatalogSource.Manual && offline.Contains(entry.Id));
        var existing = _rows.ToDictionary(row => row.Entry.Id, StringComparer.Ordinal);
        var rows = new List<GameRow>(entries.Length);
        bool refilter = false;
        _rendering = true;
        try
        {
            foreach (var entry in entries)
            {
                if (!existing.TryGetValue(entry.Id, out var row)) row = new(entry);
                else if (_query.Length > 0) refilter |= row.Matches(_query) != GameRow.Matches(entry, _query);
                row.Update(entry, offline.Contains(entry.Id), state.BlockedExecutables, catalog);
                rows.Add(row);
            }
            if (_expanded != null && !entries.Any(entry => entry.Id == _expanded.Entry.Id))
                _expanded = null;
            if (_gamesView == null || !_rows.SequenceEqual(rows))
            {
                _rows = rows;
                _gamesView = new ListCollectionView(rows) { Filter = item => ((GameRow)item).Matches(_query) };
                Games.ItemsSource = _gamesView;
                Games.SelectedItem = rows.FirstOrDefault(row => row.Entry.Id == selected && row.Matches(_query));
            }
            else if (refilter) _gamesView.Refresh();
        }
        finally { _rendering = false; }
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (Count != null && _gamesView != null)
        {
            Count.Text = $"Catalog · {_rows.Count:N0} entries · {_rows.Count - _manualCount:N0} discovered · {_manualCount:N0} manual"
                + (_query.Length == 0 ? "" : $" · {_gamesView.Count:N0} shown");
            DiscoveryCountDetail.Text = $"Of discovered entries · {_retainedCount:N0} retained · {_offlineCount:N0} awaiting verification";
            DiscoveryCountDetail.Visibility = _retainedCount + _offlineCount > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        CatalogIssueKind.InvalidManifest => "Unreadable launcher manifest · check the installation and rescan",
        CatalogIssueKind.InvalidManualEntry => "Invalid manual entry · edit its executable selection",
        CatalogIssueKind.ReparsePoint => "Linked folder skipped · approve its actual local location",
        CatalogIssueKind.InvalidPath => "Invalid source path · choose a local folder",
        CatalogIssueKind.DepthLimit => "Configured folder depth reached · deeper entries were not scanned",
        _ => "Previous scan was incomplete · rescan this source",
    }) + " · " + issue.Location;
}

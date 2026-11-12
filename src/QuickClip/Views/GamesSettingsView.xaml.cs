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
        CancelButton.IsEnabled = Discovery.IsRunning;
        if (Discovery.Status.Length != 0) Status.Text = Discovery.Status;
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
    private void Folder_Click(object sender, RoutedEventArgs e) => Approve(false);
    private void Collection_Click(object sender, RoutedEventArgs e) => Approve(false, collection: true);

    private void Approve(bool steam, bool collection = false)
    {
        var dialog = new OpenFolderDialog
        {
            Title = steam ? "Approve a Steam library containing steamapps" :
                collection ? "Choose a folder containing multiple games" : "Choose one game's install folder",
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
            : request with { GameRoots = request.GameRoots.Add(new(path, collection)) } });
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

    private void Add_Click(object sender, RoutedEventArgs e) => PickExecutables(null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameRow { Entry: { Source: CatalogSource.Manual } entry } })
            PickExecutables(entry);
    }

    internal static string? ValidateManualGameExecutables(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Any(path => CatalogPaths.LocalPath(path) == null || !CatalogPaths.IsExecutable(path)))
            return "Choose one or more local Windows game executable files (.exe).";
        if (paths.Any(CatalogPaths.IsDiscordProcess))
            return "Discord is a companion app, not a game. It cannot trigger automatic game recording.";
        return null; // No arbitrary count limit: large collections of manually associated executables are valid.
    }

    private void PickExecutables(CatalogEntry? entry)
    {
        var dialog = new OpenFileDialog { Filter = "Windows executables|*.exe", Multiselect = true,
            Title = "Select the game executable(s)", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Guid id = entry == null ? Guid.NewGuid() : Guid.ParseExact(entry.Id[7..], "N");
        var paths = dialog.FileNames.Select(CatalogPaths.LocalPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        string? selectionError = ValidateManualGameExecutables(paths);
        if (selectionError != null)
        {
            Status.Text = selectionError;
            return;
        }
        var request = State.Request;
        if (request.ManualEntries.Any(item => item.Id != id && item.Executables.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any()))
        {
            Status.Text = "One of those executables already belongs to another manual entry.";
            return;
        }
        string name = entry?.Name ?? Path.GetFileNameWithoutExtension(paths[0]);
        var manual = new ManualCatalogEntry(id, name, paths);
        SetState(State with { Request = request with
        {
            ManualEntries = request.ManualEntries.RemoveAll(item => item.Id == id).Add(manual),
        } });
        Games.SelectedItem = Games.Items.OfType<GameRow>().FirstOrDefault(row => row.Entry.Id == "manual:" + id.ToString("N"));
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
        LastScan.Text = state.Snapshot is { } scan ? $"Last scan {scan.CompletedAt.ToLocalTime():g} · {scan.Entries.Length:N0} games found"
            : "No completed scan.";
        ScanIssues.ItemsSource = state.Snapshot?.Issues.Select(ScanIssueText).ToArray();
        RenderRows(state);
    }

    internal void RenderRows(GameCatalogSettings state)
    {
        string? selected = (Games.SelectedItem as GameRow)?.Entry.Id;
        var entries = GameCatalogRuntime.Rows(state);
        var offline = state.OfflineIds.ToHashSet(StringComparer.Ordinal);
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
                row.Update(entry, offline.Contains(entry.Id));
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
            Count.Text = $"Detected games · {_gamesView.Count:N0} / {_rows.Count:N0}";
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
        _ => "Scan limit reached · try a smaller source",
    }) + " · " + issue.Location;
}

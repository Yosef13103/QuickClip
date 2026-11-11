using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickClip.Discovery;

namespace QuickClip.Views;

public partial class GamesSettingsView : UserControl
{
    private sealed record RootRow(string Path, bool Steam, bool Automatic = false, bool Excluded = false)
    {
        public override string ToString() => (Excluded ? "Excluded · " : Automatic ? "Detected Steam · " : Steam ? "Steam · " : "Game folder · ") + Path;
    }
    private sealed record GameRow(CatalogEntry Entry, string Summary, string Action);
    private static GameDiscoveryCoordinator Discovery => GameCatalogRuntime.Discovery;
    private bool _rendering;
    private static GameCatalogSettings State => AppSettings.Current.Games ?? new();
    private CatalogEntry? Selected => (Games.SelectedItem as GameRow)?.Entry;

    public GamesSettingsView()
    {
        InitializeComponent();
        Classification.ItemsSource = Enum.GetValues<AppClassification>();
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
        Render();
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
            : root.Automatic ? "Steam library excluded." : "Approval removed.";
    }

    private void Add_Click(object sender, RoutedEventArgs e) => PickExecutables(null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { Source: CatalogSource.Manual } entry) PickExecutables(entry);
    }

    private void PickExecutables(CatalogEntry? entry)
    {
        var dialog = new OpenFileDialog { Filter = "Windows executables|*.exe", Multiselect = true,
            Title = "Select the game executable(s)", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Guid id = entry == null ? Guid.NewGuid() : Guid.ParseExact(entry.Id[7..], "N");
        var paths = dialog.FileNames.Select(CatalogPaths.LocalPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (paths.Length == 0 || paths.Length > 32 || paths.Any(path => !CatalogPaths.IsExecutable(path) || CatalogPaths.IsDiscordProcess(path)))
        {
            Status.Text = "Choose up to 32 local game executables. Discord is a companion app.";
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
        if (Selected is not { Source: CatalogSource.Manual } entry) return;
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
        if (Selected is not { } entry) return;
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
        if (!_rendering && Selected is { } entry && Classification.SelectedItem is AppClassification classification)
            SaveRules(entry, entry.Rules with { Classification = classification });
    }

    private void Launcher_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry || Executables.SelectedValue is not string path) return;
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
        if (Games != null) RenderRows();
    }

    private void Render()
    {
        var state = State;
        AutoScan.IsChecked = state.AutoScan;
        Roots.ItemsSource = state.Request.SteamLibraryRoots.Select(path => new RootRow(path, true))
            .Concat((state.Snapshot?.SteamLibraries ?? []).Where(path =>
                !state.Request.SteamLibraryRoots.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                !state.Request.ExcludedDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Select(path => new RootRow(path, true, Automatic: true)))
            .Concat(state.Request.GameRoots.Select(root => new RootRow(root.Path, false)))
            .Concat(state.Request.ExcludedDirectories.Select(path => new RootRow(path, false, Excluded: true))).ToArray();
        LastScan.Text = state.Snapshot is { } scan ? $"Last scan {scan.CompletedAt.ToLocalTime():g} · {scan.Entries.Length:N0} games found"
            : "No completed scan.";
        ScanIssues.ItemsSource = state.Snapshot?.Issues.Select(issue => $"{issue.Kind} · {issue.Location}").ToArray();
        RenderRows();
    }

    private void RenderRows()
    {
        string? selected = Selected?.Id;
        string query = Search.Text.Trim();
        var state = State;
        var entries = GameCatalogRuntime.Rows(state);
        var rows = entries.Where(entry => entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                query.Length > 0 && entry.Executables.Any(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(entry =>
            {
                bool disabled = entry.Rules.Inclusion == CatalogInclusion.Exclude;
                string source = entry.Source == CatalogSource.Folder ? "Approved folder" : entry.Source.ToString();
                string status = disabled ? "Disabled for automatic targeting" :
                    state.OfflineIds.Contains(entry.Id) || entry.Executables.IsEmpty ? "Needs review" : "Eligible for game detection";
                return new GameRow(entry, $"{source} · {status}", disabled ? "Allow game targeting" : "Exclude from game targeting");
            }).ToArray();
        Games.ItemsSource = rows;
        Games.SelectedItem = rows.FirstOrDefault(row => row.Entry.Id == selected);
        Count.Text = $"Detected games · {rows.Length:N0} / {entries.Length:N0}";
        ShowDetails();
    }

    private void Game_Changed(object sender, SelectionChangedEventArgs e) => ShowDetails();

    private void ShowDetails()
    {
        var entry = Selected;
        var settings = AppSettings.Current;
        _rendering = true;
        Identity.Text = entry == null ? "Select a game to see its identity and executable associations."
            : $"{entry.DisplayName} · {entry.Id}";
        Policy.Text = entry == null ? "" :
            $"Inherits global recording · {settings.RecordFps} fps · {(settings.RecordResolution == 0 ? "Native" : $"{settings.RecordResolution}px short side")} · {settings.BufferSeconds}s replay. " +
            (settings.UseAudioEndpoints ? "Uses global selected audio endpoints. " : "Uses global legacy audio settings. ") +
            (entry.Rules.CapturePreference != CatalogCapturePreference.Default ? $"Saved {entry.Rules.CapturePreference} source preference awaits integration. " : "") +
            (entry.Rules.ProfileId is { } profile ? $" Saved profile {profile} is awaiting integration." : "");
        Executables.ItemsSource = entry?.Executables.Select(path => new KeyValuePair<string, string>(path,
            path + (entry.Rules.LauncherExecutables.Contains(path, StringComparer.OrdinalIgnoreCase) ? " · launcher" : "")
            + (entry.Source == CatalogSource.Manual && !File.Exists(path) ? " · unavailable" : ""))).ToArray();
        Executables.DisplayMemberPath = "Value";
        Executables.SelectedValuePath = "Key";
        Classification.SelectedItem = entry?.Rules.Classification;
        Classification.IsEnabled = LauncherButton.IsEnabled = RenameButton.IsEnabled = entry != null;
        EditButton.IsEnabled = RemoveButton.IsEnabled = entry?.Source == CatalogSource.Manual;
        _rendering = false;
    }
}

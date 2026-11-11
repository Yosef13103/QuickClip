using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Discovery;

namespace QuickClip.Views;

public partial class GamesSettingsView : UserControl
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(15);
    private sealed record RootRow(string Path, bool Steam)
    {
        public override string ToString() => (Steam ? "Steam · " : "Game folder · ") + Path;
    }
    private sealed record GameRow(CatalogEntry Entry, string Summary, string Action);
    private readonly DispatcherTimer _timer = new() { Interval = ScanInterval };
    private CancellationTokenSource? _scan;
    private long? _lastStarted;
    private bool _rendering;
    private static GameCatalogSettings State => AppSettings.Current.Games ?? new();
    private CatalogEntry? Selected => (Games.SelectedItem as GameRow)?.Entry;

    public GamesSettingsView()
    {
        InitializeComponent();
        Classification.ItemsSource = Enum.GetValues<AppClassification>();
        _timer.Tick += async (_, _) => await AutoScanIfDueAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible)
            {
                _timer.Stop();
                _scan?.Cancel();
                return;
            }
            Render();
            UpdateTimer();
            await AutoScanIfDueAsync();
        };
    }

    private void UpdateTimer() => _timer.IsEnabled = IsVisible && State.AutoScan;

    private void SetState(GameCatalogSettings state)
    {
        AppSettings.Current.Games = state;
        AppSettings.Current.Save();
        Render();
    }

    private async void AutoScan_Click(object sender, RoutedEventArgs e)
    {
        SetState(State with { AutoScan = AutoScan.IsChecked == true });
        UpdateTimer();
        if (!State.AutoScan) _scan?.Cancel();
        else await AutoScanIfDueAsync();
    }

    private Task AutoScanIfDueAsync() =>
        State.AutoScan && (_lastStarted is not { } last || TimeProvider.System.GetElapsedTime(last) >= ScanInterval)
            ? RunScanAsync() : Task.CompletedTask;

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scan?.Cancel();

    private async Task RunScanAsync()
    {
        if (_scan != null) return;
        var request = State.Request;
        if (request.GameRoots.IsEmpty && request.SteamLibraryRoots.IsEmpty)
        {
            Status.Text = "Approve a Steam library or game folder to scan.";
            return;
        }
        using var scan = new CancellationTokenSource();
        _scan = scan;
        _lastStarted = TimeProvider.System.GetTimestamp();
        ScanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Status.Text = "Scanning approved folders…";
        try
        {
            var progress = new Progress<int>(count =>
            {
                if (_scan == scan && !scan.IsCancellationRequested) Status.Text = $"Scanning… {count:N0} entries checked.";
            });
            var result = await GameCatalogService.ScanAsync(request, scan.Token, progress);
            scan.Token.ThrowIfCancellationRequested();
            SetState(GameCatalogRuntime.WithScan(State, result));
            Status.Text = result.LimitReached ? "Scan limit reached. Some earlier entries need review."
                : result.Issues.IsEmpty ? "Scan complete."
                : $"Scan completed with {result.Issues.Length:N0} reported issue(s). Expand Approved folders to review the scan report.";
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
            Status.Text = "Scan canceled. Previous results kept.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status.Text = "Scan could not finish. Previous results kept.";
            Log.Write("Game scan failed: " + ex.GetType().Name);
        }
        finally
        {
            _scan = null;
            ScanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void Steam_Click(object sender, RoutedEventArgs e) => Approve(true);
    private void Folder_Click(object sender, RoutedEventArgs e) => Approve(false);

    private void Approve(bool steam)
    {
        var dialog = new OpenFolderDialog { Title = steam ? "Approve a Steam library containing steamapps" : "Approve a single game folder" };
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
        _scan?.Cancel();
        SetState(State with { Request = steam ? request with { SteamLibraryRoots = request.SteamLibraryRoots.Add(path) }
            : request with { GameRoots = request.GameRoots.Add(new(path)) } });
        Status.Text = "Folder approved. Select Scan Now to scan it.";
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (Roots.SelectedItem is not RootRow root) return;
        _scan?.Cancel();
        var request = State.Request;
        SetState(State with { Request = root.Steam ? request with { SteamLibraryRoots = request.SteamLibraryRoots.Remove(root.Path) }
            : request with { GameRoots = request.GameRoots.RemoveAll(row => row.Path == root.Path) } });
        Status.Text = "Approval removed.";
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
            .Concat(state.Request.GameRoots.Select(root => new RootRow(root.Path, false))).ToArray();
        LastScan.Text = state.Snapshot is { } scan ? $"Last scan {scan.CompletedAt.ToLocalTime():g} · {scan.VisitedEntries:N0} entries checked"
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

using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using QuickClip.Discovery;
using QuickClip.Recording;
using QuickClip.Recording.Companion;

namespace QuickClip.Views;

public partial class GamesSettingsView : UserControl
{
    private sealed record ApplicationTile(string Id, string Name, string Description, Geometry Icon, Brush IconBrush, bool Enabled)
    {
        public string Status => Enabled ? "Enabled" : "Disabled";
        public string ActionLabel => "Open " + Name + " recording settings";
        public string Tooltip => Name + " capture " + Status.ToLowerInvariant() + ". Open settings.";
    }

    private sealed record LauncherRow(CatalogSource Source, string Name, bool Enabled, bool Supported, string Status)
    {
        public bool CanRefresh => Enabled && Supported;
        public string LogoKey => Source.ToString();
        public string TileName => Source switch
        {
            CatalogSource.Xbox => "Xbox / Game Pass",
            CatalogSource.Ubisoft => "Ubisoft",
            CatalogSource.Rockstar => "Rockstar",
            CatalogSource.Origin => "Origin",
            _ => Name,
        };
        public string TileTooltip => $"{Name} · {(Enabled ? "Enabled" : "Disabled")}\n{Status}\nClick to {(Enabled ? "disable" : "enable")} discovery";
        public string RefreshName => $"Refresh {Name} game library";
    }

    private sealed record RootRow(string Path, bool Steam, bool Automatic = false, bool Excluded = false, bool Epic = false,
        string? Key = null, string? Status = null, bool Enabled = true, bool IsCollection = false,
        bool HasPreviousExclusion = false)
    {
        public bool CanToggle => Key != null && !Excluded;
        public bool CanRefresh => CanToggle && Enabled;
        public string RemoveLabel => Excluded ? "Restore" : Automatic ? "Exclude" : "Remove";
        public string Label => ToString();
        public override string ToString() => (Excluded ? "Excluded · " : Epic ? "Detected Epic manifests · " :
            Automatic ? "Detected Steam · " : Steam ? "Steam · " : IsCollection ? "Game collection · " : "Game folder · ") + Path +
            (Status == null ? "" : " · " + Status) +
            (HasPreviousExclusion ? " · Previous game exclusion retained" : "");
    }
    private static GameDiscoveryCoordinator Discovery => GameCatalogRuntime.Discovery;
    private bool _basic;
    private int _eligibleCount;
    private GameCatalogSettings? _renderedState;
    private List<GameRow> _rows = [];
    private LauncherRow[] _launchers = [];
    private ListCollectionView? _gamesView, _attentionView, _ignoredView;
    private readonly ListBoxItem[] _attentionHeading, _ignoredHeading, _otherHeading;
    private readonly Expander _ignoredExpander;
    internal event Action<int>? AttentionCountChanged;
    internal event Action? DiscordSettingsRequested;
    internal int AttentionCount { get; private set; }
    private int _uniqueCount, _manualCount, _retainedCount, _offlineCount;
    private GameRow? _expanded;
    private string _query = "";
    private ScrollViewer? _catalogScroller;
    private ListBoxItem[] _searchPaddingItems = [];
    private bool _changingSearchLayout;
    private double? _searchAnchorDocumentY;
    private long _searchRevision;
    private static GameCatalogSettings State => AppSettings.Current.Games ?? new();

    public GamesSettingsView()
    {
        InitializeComponent();
        // Search is a permanent sibling of the virtualized list. Only its rendered Y
        // changes as the placeholder scrolls; filtering never recycles its TextBox.
        Loaded += (_, _) => AttachStickySearch();
        Unloaded += (_, _) => DetachStickySearch();
        Games.SizeChanged += (_, _) => UpdateStickySearch();
        SearchAnchorItem.Loaded += (_, _) => UpdateStickySearch();
        SearchAnchorItem.LayoutUpdated += (_, _) => UpdateStickySearch();
        _ignoredExpander = new() { Header = "Ignored issues", Margin = new Thickness(4, 4, 0, 4) };
        _ignoredExpander.Expanded += IgnoredExpansion_Changed;
        _ignoredExpander.Collapsed += IgnoredExpansion_Changed;
        var attention = new TextBlock { Text = "Needs attention", FontWeight = FontWeights.SemiBold, Margin = new(4, 12, 0, 2) };
        attention.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        _attentionHeading = [Heading(attention)];
        _ignoredHeading = [Heading(_ignoredExpander)];
        _otherHeading = [Heading(new TextBlock { Text = "Games and apps", FontWeight = FontWeights.SemiBold, Margin = new(4, 12, 0, 2) })];
        Render();
        RefreshApplicationCards();
        IsVisibleChanged += async (_, _) =>
        {
            Discovery.Changed -= ShowDiscoveryStatus;
            if (!IsVisible) return;
            RefreshApplicationCards();
            Discovery.Changed += ShowDiscoveryStatus;
            ShowDiscoveryStatus();
            await Discovery.RequestAsync();
        };
    }

    private static readonly Geometry DiscordIcon = Geometry.Parse(
        "M20.317 4.3698A19.7913 19.7913 0 0 0 15.885 3.1l-.55 1.13a18.27 18.27 0 0 1 3.94 1.53 15.38 15.38 0 0 0-4.75-2.1 17.63 17.63 0 0 0-5.05 0 15.38 15.38 0 0 0-4.75 2.1 18.27 18.27 0 0 1 3.94-1.53L8.115 3.1A19.7913 19.7913 0 0 0 3.683 4.37C.877 8.55.117 12.62.497 16.63a19.9 19.9 0 0 0 6.11 3.08l1.31-2.13a12.86 12.86 0 0 1-2.07-1.03l.5-.39c3.98 1.85 8.29 1.85 12.22 0l.5.39a12.86 12.86 0 0 1-2.07 1.03l1.31 2.13a19.9 19.9 0 0 0 6.11-3.08c.45-4.65-.77-8.68-3.2-12.26ZM8.67 14.22c-1.22 0-2.21-1.11-2.21-2.47 0-1.36.97-2.47 2.21-2.47s2.23 1.11 2.21 2.47c0 1.36-.97 2.47-2.21 2.47Zm6.66 0c-1.22 0-2.21-1.11-2.21-2.47 0-1.36.97-2.47 2.21-2.47 1.24 0 2.23 1.11 2.21 2.47 0 1.36-.97 2.47-2.21 2.47Z");

    internal static string DiscordCaptureDescription(DiscordCaptureMode mode) => mode switch
    {
        DiscordCaptureMode.DuringCalls => "During voice and video calls",
        DiscordCaptureMode.WhileOpen => "While Discord is open",
        DiscordCaptureMode.Manual => "Only when I start it",
        _ => "Capture turned off",
    };

    internal void RefreshApplicationCards()
    {
        var settings = AppSettings.Current;
        Applications.ItemsSource = new[]
        {
            new ApplicationTile("discord", "Discord", DiscordCaptureDescription(settings.DiscordCaptureMode),
                DiscordIcon, new SolidColorBrush(Color.FromRgb(88, 101, 242)), settings.DiscordCaptureEnabled),
        };
    }

    private void ApplicationSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } &&
            id.Equals("discord", StringComparison.OrdinalIgnoreCase))
            DiscordSettingsRequested?.Invoke();
    }

    private static ListBoxItem Heading(object content) => new()
    {
        Content = content, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    // The search input must never become a recycled ListBox item. The static
    // placeholder marks its natural document position while the real input stays
    // in a sibling overlay. ScrollUnit=Pixel gives offsets in WPF device units.
    private void AttachStickySearch()
    {
        var scroller = FindPageScrollViewer(Games);
        if (ReferenceEquals(scroller, _catalogScroller))
        {
            UpdateStickySearch();
            return;
        }
        DetachStickySearch();
        _catalogScroller = scroller;
        if (scroller != null) scroller.ScrollChanged += CatalogScroll_Changed;
        UpdateStickySearch();
    }

    private void DetachStickySearch()
    {
        if (_catalogScroller != null)
            _catalogScroller.ScrollChanged -= CatalogScroll_Changed;
        _catalogScroller = null;
    }

    private void CatalogScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _catalogScroller))
            UpdateStickySearch();
    }

    internal static double StickySearchTop(double documentY, double scrollY) =>
        Math.Max(0, documentY - scrollY);

    // Keep Apps/Discord and discovery controls in the normal scroll range even
    // while filtering games. A short result list otherwise leaves too little
    // scrollable extent to bring the search anchor to the viewport's top.
    // Virtualized, nonfocusable trailing rows reserve only the needed viewport;
    // they do not participate in game filtering or represent recording sources.
    internal static int SearchPaddingRows(double viewport) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(0, viewport) / 64) + 6, 16, 48);

    private void UpdateSearchPadding(bool searching)
    {
        if (!searching)
        {
            SearchScrollPadding.Collection = null;
            return;
        }

        double viewport = _catalogScroller?.ViewportHeight > 0
            ? _catalogScroller.ViewportHeight : Games.ActualHeight;
        int count = SearchPaddingRows(viewport);
        if (_searchPaddingItems.Length != count)
            _searchPaddingItems = Enumerable.Range(0, count).Select(_ => new ListBoxItem
            {
                Height = 64, Focusable = false, IsTabStop = false,
                IsHitTestVisible = false, Background = Brushes.Transparent,
                Content = null
            }).ToArray();
        if (!ReferenceEquals(SearchScrollPadding.Collection, _searchPaddingItems))
            SearchScrollPadding.Collection = _searchPaddingItems;
    }

    private void UpdateStickySearch()
    {
        if (_changingSearchLayout) return;
        // Keep the search beside the catalog until it reaches the top.
        // Scrolling UP past it hides the overlay and reveals Apps/Discord and
        // discovery normally, regardless of whether the search has text.
        var scroller = _catalogScroller;
        if (scroller == null || Games.ActualHeight <= 0) return;
        if (Games.IsAncestorOf(SearchAnchorItem))
        {
            var top = SearchAnchorItem.TranslatePoint(new Point(0, 0), Games).Y;
            if (double.IsFinite(top))
                _searchAnchorDocumentY = top + scroller.VerticalOffset;
        }
        if (_searchAnchorDocumentY is not { } documentY)
        {
            SearchDock.Visibility = Visibility.Collapsed;
            return;
        }

        double naturalY = documentY - scroller.VerticalOffset;
        if (naturalY >= Games.ActualHeight)
        {
            SearchDock.Visibility = Visibility.Collapsed;
            return;
        }
        SearchDockTransform.Y = StickySearchTop(documentY, scroller.VerticalOffset);
        SearchDock.Visibility = Visibility.Visible;
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

    internal void RefreshPresentation() => ShowDiscoveryStatus();

    private void ShowDiscoveryStatus()
    {
        // Discovery may publish its update from a worker thread. WPF controls must
        // only be accessed on the view's owning dispatcher, including when a
        // scan completes while a settings page is being opened or closed.
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.BeginInvoke(new Action(ShowDiscoveryStatus),
                    System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        if (!IsVisible) return;
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
        ScanDetails.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        LibrarySources.Visibility = Visibility.Visible;
        IgnoredExecutables.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        ScanHelp.Visibility = TargetingHelp.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        ScanHelp.Text = basic ? "Use Add Game for one game or a collection. Manage your added folders here; choose game stores here."
            : "Automatic scans refresh enabled sources. You can pause and resume a scan here.";
        TargetingHelp.Text = basic ? "Games marked ready can start recording when recording is on and the display is allowed."
            : "Targeting rules apply to game detection. Manual entire-display capture includes anything visible on the approved display, including games disabled here.";
        foreach (var row in _rows) row.UseBasicPresentation(basic);
        Render();
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
        if (sender is not ToggleButton { DataContext: LauncherRow { Supported: true } row } box) return;
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
        AddedFolders.IsExpanded = true;
        SourceError.Text = "";
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
            SourceError.Text = "This folder is already in your game sources. Remove it first to change its type.";
            AddedFolders.IsExpanded = true;
            return;
        }
        SetState(State with { Request = steam ? request with { SteamLibraryRoots = request.SteamLibraryRoots.Add(path) }
            : request with { GameRoots = request.GameRoots.Add(new(path, IsCollection: true)) } });
        SourceError.Text = "";
        AddedFolders.IsExpanded = true;
        _ = Discovery.RequestAsync(manual: true, sourceKey: new CatalogScope(
            steam ? CatalogSource.Steam : CatalogSource.Folder, path, IsCollection: !steam).Key);
    }

    private void RestoreCollectionExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RootRow { HasPreviousExclusion: true } root } ||
            CatalogClaims.CollectionRootExclusion(State, root.Path) is not { } prior) return;
        if (!ModalDialog.ConfirmAction(Window.GetWindow(this),
            $"A previous game-level exclusion for this folder can still block its executables.\n\n{root.Path}\n\n" +
            $"Restore this one rule ({prior.DeniedExecutables.Length:N0} remembered executable paths)? Other explicit blocks, game exclusions and normal detection rules still apply.",
            "Restore previous game exclusion", "Restore", "Cancel", destructive: false)) return;
        SetState(CatalogClaims.RestoreCollectionRootExclusion(State, root.Path));
        Status.Text = "Previous game exclusion restored to normal detection rules.";
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
            : root.Automatic ? "Launcher source excluded." : "Folder removed from QuickClip. Game files are unchanged.";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        ShowAddError(null);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var file = new MenuItem { Header = "Executable or shortcut (more accurate)…" };
        file.Click += (_, _) => PickExecutables(null);
        var folder = new MenuItem { Header = "One game — installation folder…" };
        folder.Click += (_, _) => PickGameFolder();
        menu.Items.Add(file);
        menu.Items.Add(folder);
        var collection = new MenuItem { Header = "Multiple games — collection folder…" };
        collection.Click += (_, _) => Approve(false);
        menu.Items.Add(collection);
        menu.IsOpen = true;
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameRow row })
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

    private void PickExecutables(CatalogEntry? entry, string? directory = null, string? suggestedName = null)
    {
        var dialog = new OpenFileDialog { Filter = "Executable or shortcut|*.exe;*.lnk", Multiselect = true,
            Title = "Select game executables or shortcuts", CheckFileExists = true, DereferenceLinks = false,
            InitialDirectory = directory ?? "" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddSelection(dialog.FileNames, entry, suggestedName);
    }

    private void ShowAddError(string? message)
    {
        AddError.Text = message;
        AddError.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PickGameFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose ONE game's installation folder (not a collection)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (CatalogPaths.LocalPath(dialog.FolderName) is not { Length: > 3 } path)
        {
            ShowAddError("Choose a game installation folder on a local drive.");
            return;
        }
        var request = State.Request;
        if (request.GameRoots.Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowAddError("This folder is already in your game sources. Remove it from Game folders and collections before adding it with a different type.");
            AddedFolders.IsExpanded = true;
            return;
        }
        if (request.GameRoots.Length + request.SteamLibraryRoots.Length >= 128)
        {
            ShowAddError("Remove a game folder before adding another.");
            return;
        }
        SetState(State with { Request = request with { GameRoots = request.GameRoots.Add(new(path)) } });
        AddedFolders.IsExpanded = true;
        Status.Text = "Game folder added. Scan queued…";
        _ = Discovery.RequestAsync(manual: true, sourceKey: new CatalogScope(CatalogSource.Folder, path).Key);
    }

    private void AddSelection(IReadOnlyList<string> inputs, CatalogEntry? entry, string? suggestedName = null)
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
        if (paths.FirstOrDefault(CatalogPaths.IsProtectedExecutable) is { } protectedPath)
        {
            Error($"{Path.GetFileName(protectedPath)} is a protected or unsupported executable.");
            return;
        }
        if (paths.Any(path => GameDetector.ApplyProcessRole(path, new(CatalogPaths.DefaultRole(path))).Kind != CatalogMatchKind.Unknown) &&
            !ModalDialog.ConfirmAction(owner,
                "One or more selected files look like a launcher or non-game app. Add anyway only if you want recording to target a window actually owned by that exact executable. Selecting an updater does not select the app it launches.",
                "Non-game executable warning", "Add anyway", "Cancel", destructive: false, warning: true)) return;
        if (paths.Any(CatalogPaths.IsJavaRuntime) && !ModalDialog.ConfirmAction(owner,
            "This Java executable can run more than one app. Only add it if this exact file is dedicated to your game. For a shared Java installation, use manual recording on an allowed display.",
            "Confirm dedicated Java executable", "Add dedicated executable", "Cancel", destructive: false)) return;
        var state = State;
        var request = state.Request;
        var existing = _rows.FirstOrDefault(row => (row.IsManual || row.Entry.Rules.AttachedExecutables.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any()) && row.Entry.Id != entry?.Id &&
            row.Entry.Executables.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any());
        if (existing != null)
        {
            Error($"A selected file is already in {existing.Entry.DisplayName}.");
            return;
        }
        if (entry == null)
        {
            var owners = _rows.Where(row => !row.IsManual && row.Entry.Executables.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any()).ToArray();
            if (owners.Length > 0)
            {
                if (owners.Length != 1 || !paths.All(path => owners[0].Entry.Executables.Contains(path, StringComparer.OrdinalIgnoreCase)))
                {
                    Error("Choose the existing game entry and attach the executable there to resolve ownership.");
                    return;
                }
                if (!ModalDialog.ConfirmAction(owner, $"Attach these files to {owners[0].Entry.DisplayName} instead of creating a duplicate?",
                    "Existing game", "Attach", "Cancel", destructive: false)) return;
                entry = owners[0].Entry;
            }
        }
        if (entry is { Source: not CatalogSource.Manual })
        {
            SaveRules(entry, entry.Rules with
            {
                AttachedExecutables = [.. entry.Rules.AttachedExecutables.Union(paths, StringComparer.OrdinalIgnoreCase)],
            });
            Status.Text = "Exact executable attachment saved. Explicit blocks still apply.";
            return;
        }
        string name = entry?.DisplayName ?? suggestedName ?? ManualGameInput.FriendlyName(paths[0]);
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
            Overrides = request.Overrides.RemoveAll(item => item.Id == "manual:" + id.ToString("N"))
                .Add(new("manual:" + id.ToString("N"), (entry?.Rules ?? new CatalogRules()) with { AttachedExecutables = paths })
                { DeniedExecutables = request.Overrides.LastOrDefault(item => item.Id == "manual:" + id.ToString("N"))?.DeniedExecutables ?? [] }),
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

    private void DetachExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameRow row, CommandParameter: string path } ||
            row.IsManual || !row.Entry.Rules.AttachedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        var request = State.Request;
        var ids = row.Entry.AliasIds.Prepend(row.Entry.Id).ToHashSet(StringComparer.Ordinal);
        SetState(State with { Request = request with
        {
            Overrides = [.. request.Overrides.Select(item => ids.Contains(item.Id) ? item with
            {
                Rules = item.Rules with
                {
                    AttachedExecutables = item.Rules.AttachedExecutables.RemoveAll(value =>
                        string.Equals(value, path, StringComparison.OrdinalIgnoreCase)),
                },
            } : item)],
        } });
        Status.Text = "Executable attachment removed; discovery and explicit blocks still apply.";
    }

    private void BlockFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Windows executables|*.exe", Multiselect = true,
            Title = "Select executables to block for automatic game detection", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) BlockExecutables(dialog.FileNames);
    }

    private void BlockSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string path, DataContext: GameRow row }) return;
        if (State.BlockedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            SetState(State with { BlockedExecutables = State.BlockedExecutables.RemoveAll(value =>
                string.Equals(value, path, StringComparison.OrdinalIgnoreCase)) });
            Status.Text = "Executable restored to its catalog rules.";
        }
        else BlockExecutables([path], row);
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

    private void PositionGameSearch()
    {
        var scroller = _catalogScroller;
        if (scroller == null) return;
        _changingSearchLayout = true;
        try
        {
            // Realize the search marker without replacing or refocusing the
            // TextBox. The Apps and discovery controls are always still above it.
            Games.ScrollIntoView(SearchAnchorItem);
            Games.UpdateLayout();
            if (!Games.IsAncestorOf(SearchAnchorItem)) return;
            double anchor = SearchAnchorItem.TranslatePoint(new Point(0, 0), Games).Y
                + scroller.VerticalOffset;
            _searchAnchorDocumentY = anchor;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                scroller.ScrollToVerticalOffset(Math.Min(Math.Max(0, anchor), scroller.ScrollableHeight));
                Games.UpdateLayout();
            }
        }
        finally
        {
            _changingSearchLayout = false;
            UpdateStickySearch();
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        // Apps/Discord and discovery are never removed or filtered. Keep the
        // genuine search input alive outside the virtualized ListBox and scroll
        // to the first matching game rather than preserving a stale scroll offset.
        long revision = ++_searchRevision;
        _query = Search.Text.Trim();
        _changingSearchLayout = true;
        try
        {
            UpdateSearchPadding(_query.Length > 0);
            RefreshIssueViews();
            UpdateCount();
        }
        finally { _changingSearchLayout = false; }

        // Reposition synchronously on edits, then once after WPF recalculates
        // the virtualized extent. Scrolling up afterward remains entirely free.
        PositionGameSearch();
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (revision == _searchRevision && IsLoaded)
                PositionGameSearch();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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
        }).ToArray();
        if (!_launchers.SequenceEqual(launchers)) Launchers.ItemsSource = _launchers = launchers;
        RootRow[] epic = state.Snapshot?.EpicManifestDirectory is { } manifestDirectory &&
            !state.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(manifestDirectory, root))
            ? [new(manifestDirectory, Steam: false, Automatic: true, Epic: true)] : [];
        var roots = CatalogScope.Configured(state).Where(scope => scope.Root != null)
            .Select(scope => new RootRow(scope.Root!, scope.Kind == CatalogSource.Steam, Key: scope.Key, Status: DescribeSource(scope.Key), Enabled: enabled.Contains(scope.Key), IsCollection: scope.IsCollection,
                HasPreviousExclusion: scope.Kind == CatalogSource.Folder && scope.IsCollection &&
                    CatalogClaims.CollectionRootExclusion(state, scope.Root!) != null))
            .Concat((state.Snapshot?.SteamLibraries ?? []).Where(path =>
                !state.Request.SteamLibraryRoots.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                !state.Request.ExcludedDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Select(path => new RootRow(path, true, Automatic: true)))
            .Concat(epic)
            .Concat(state.Request.ExcludedDirectories.Select(path => new RootRow(path, false, Excluded: true)));
        Roots.ItemsSource = roots.Where(root => !_basic || !root.Automatic && !root.Excluded).ToArray();
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
        var ignored = (AppSettings.Current.IgnoredGameIssues ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!existing.TryGetValue(entry.Id, out var row)) row = new(entry);
            else if (_query.Length > 0) refilter |= row.Matches(_query) != GameRow.Matches(entry, _query);
            row.Update(entry, offline.Contains(entry.Id), state.BlockedExecutables, catalog);
            row.UseBasicPresentation(_basic);
            var previousIssue = (row.Issue, row.IssueIgnored);
            row.EvaluateIssue(ignored, Discovery.IsRunning);
            refilter |= previousIssue != (row.Issue, row.IssueIgnored);
            if (row.DetectionReady) _eligibleCount++;
            rows.Add(row);
        }
        if (_expanded != null && !entries.Any(entry => entry.Id == _expanded.Entry.Id))
            _expanded = null;
        if (_gamesView == null || !_rows.SequenceEqual(rows))
        {
            _rows = rows;
            _gamesView = new ListCollectionView(rows) { Filter = item => item is GameRow row && row.Matches(_query) && !row.HasIssue };
            _attentionView = new ListCollectionView(rows) { Filter = item => item is GameRow row && row.Matches(_query) && row.HasIssue && !row.IssueIgnored };
            _ignoredView = new ListCollectionView(rows) { Filter = item => item is GameRow row && row.Matches(_query) && row.HasIssue && row.IssueIgnored };
            GameItems.Collection = _gamesView;
            AttentionItems.Collection = _attentionView;
            IgnoredItems.Collection = _ignoredExpander.IsExpanded ? _ignoredView : null;
            Games.SelectedItem = rows.FirstOrDefault(row => row.Entry.Id == selected && row.Matches(_query));
        }
        else if (refilter) RefreshIssueViews();
        UpdateIssueHeaders();
        UpdateCount();
    }

    private void RefreshIssueViews()
    {
        _gamesView?.Refresh();
        _attentionView?.Refresh();
        _ignoredView?.Refresh();
        UpdateIssueHeaders();
    }

    private void UpdateIssueHeaders()
    {
        if (_attentionView == null || _ignoredView == null) return;
        ShowHeading(AttentionHeading, _attentionHeading, _attentionView.Count > 0);
        ShowHeading(IgnoredHeading, _ignoredHeading, _ignoredView.Count > 0);
        ShowHeading(OtherGamesHeading, _otherHeading, _attentionView.Count + _ignoredView.Count > 0);
        int count = _rows.Count(row => row.HasIssue && !row.IssueIgnored);
        if (AttentionCount == count) return;
        AttentionCount = count;
        AttentionCountChanged?.Invoke(count);
    }

    private static void ShowHeading(CollectionContainer container, ListBoxItem[] heading, bool visible)
    {
        var collection = visible ? heading : null;
        if (!ReferenceEquals(container.Collection, collection)) container.Collection = collection;
    }

    private void IgnoredExpansion_Changed(object sender, RoutedEventArgs e)
    {
        if (IgnoredItems != null) IgnoredItems.Collection = _ignoredExpander.IsExpanded ? _ignoredView : null;
        UpdateCount();
    }

    private void DismissIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameRow { HasIssue: true } row }) return;
        var settings = AppSettings.Current;
        var previous = settings.IgnoredGameIssues ?? [];
        settings.IgnoredGameIssues = row.IssueIgnored
            ? previous.Except(row.IssueKeys(), StringComparer.Ordinal).ToArray()
            : previous.Append(row.IssueKeys().First()).Distinct(StringComparer.Ordinal).ToArray();
        if (!settings.TrySave())
        {
            settings.IgnoredGameIssues = previous;
            row.ActionError = "Could not save your issue preference.";
            return;
        }
        row.ActionError = null;
        row.EvaluateIssue(settings.IgnoredGameIssues, Discovery.IsRunning);
        RefreshIssueViews();
    }

    private async void RepairIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GameRow { HasIssue: true } row }) return;
        if (row.Issue == DetectionIssue.SourceUnavailable)
        {
            string? key = CatalogScope.Enabled(State).FirstOrDefault(scope => scope.Kind == row.Entry.Source &&
                (scope.Root == null || row.Entry.InstallDirectories.Any(path => CatalogPaths.IsWithin(path, scope.Root))))?.Key;
            if (key == null) row.ActionError = "Restore or re-enable the game source before rescanning.";
            else if (Discovery.IsRunning) row.ActionError = "A scan is already running. Try again when it finishes.";
            else await Discovery.RequestAsync(manual: true, sourceKey: key);
            return;
        }

        if (row.Issue == DetectionIssue.OwnershipConflict)
        {
            ChooseSharedOwner(row);
            return;
        }

        string? initial = row.Entry.InstallDirectories.FirstOrDefault();
        if (row.Issue == DetectionIssue.MinecraftJava)
        {
            if (!ModalDialog.ConfirmAction(Window.GetWindow(this),
                "QuickClip could not find Minecraft's bundled Java runtime. Look inside your Minecraft Launcher installation (often under Program Files (x86), possibly on another drive) for:\n\n" +
                CatalogPaths.MinecraftLauncherFolder + "\\" + CatalogPaths.MinecraftRuntimeRelative +
                "\n\nIf your runtime version differs, start Minecraft and use Task Manager > Details > javaw.exe > Open file location. Select the dedicated javaw.exe, not MinecraftLauncher.exe. Bedrock is separate.",
                "Fix Minecraft Java detection", "Locate dedicated executable", "Close", destructive: false)) return;
            initial = null;
        }
        string? name = row.Issue == DetectionIssue.MinecraftJava ? "Minecraft Java Edition" : row.Entry.DisplayName;
        PickExecutables(row.Issue == DetectionIssue.MinecraftJava && !row.IsManual ? null : row.Entry, initial, name);
    }

    private void ChooseSharedOwner(GameRow row)
    {
        if (Window.GetWindow(this) is not { } owner) return;
        var state = State;
        var entries = GameCatalogRuntime.Rows(state);
        var index = new GameCatalogIndex(new(entries, [], 0, false, default), CatalogClaims.BlockedExecutables(state));
        var conflicts = SharedGameOwnership.Conflicts(row.Entry, index);
        if (conflicts.Length == 0) { row.ActionError = "No unresolved shared executable remains."; return; }
        var files = new ComboBox { ItemsSource = conflicts, SelectedIndex = 0 };
        var games = new ComboBox { DisplayMemberPath = nameof(CatalogEntry.DisplayName) };
        System.Windows.Automation.AutomationProperties.SetName(files, "Shared executable");
        System.Windows.Automation.AutomationProperties.SetName(games, "Recording game name");
        void RefreshOwners()
        {
            if (files.SelectedItem is not string path) return;
            games.ItemsSource = SharedGameOwnership.Claimants(entries, path);
            games.SelectedItem = ((CatalogEntry[])games.ItemsSource).FirstOrDefault(entry => entry.Id == row.Entry.Id);
        }
        files.SelectionChanged += (_, _) => RefreshOwners();
        RefreshOwners();
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Choose which game owns this exact executable when recording. Other game files keep their existing names and rules. For servers or tools, use Block selected executable instead.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Shared executable" });
        panel.Children.Add(files);
        panel.Children.Add(new TextBlock { Text = "Record as", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(games);
        if (!ModalDialog.Run(owner, "Choose recording owner", panel, "Use selected game", "Cancel", safeDefault: true) ||
            files.SelectedItem is not string selected || games.SelectedItem is not CatalogEntry game) return;
        if (!ReferenceEquals(state, State) || SharedGameOwnership.Choose(state, entries, selected, game.Id) is not { } changed)
        {
            row.ActionError = "The catalog changed or the selection is blocked or unavailable. Refresh and try again.";
            return;
        }
        // Persist first; a failed save must not leave a changed active owner.
        var settings = AppSettings.Current;
        settings.Games = CatalogClaims.Reconfigure(changed);
        if (!settings.TrySave())
        {
            settings.Games = state;
            row.ActionError = "Could not save the recording owner.";
            return;
        }
        Render();
        _ = Discovery.SettingsChangedAsync();
        Status.Text = $"Record {Path.GetFileName(selected)} as {game.DisplayName}.";
    }

    private void UpdateCount()
    {
        if (Count != null && _gamesView != null)
        {
            Count.Text = (_basic ? $"{_uniqueCount:N0} games and apps found" :
                $"Catalog · {_uniqueCount:N0} unique games and apps · {_manualCount:N0} manual entries")
                + (_query.Length == 0 ? "" : $" · {_gamesView.Count + (_attentionView?.Count ?? 0) + (_ignoredExpander.IsExpanded ? _ignoredView?.Count ?? 0 : 0):N0} shown");
            DiscoveryCountDetail.Text = $"{_retainedCount:N0} entries with cached paths · {_offlineCount:N0} awaiting verification";
            DiscoveryCountDetail.Visibility = !_basic && _retainedCount + _offlineCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: GameRow row }) return;
        if (!ToggleDetails(row) || row.CheckingExecutables || row.Entry.Executables.IsEmpty) return;
        var paths = row.Entry.Executables;
        row.CheckingExecutables = true;
        try
        {
            var unavailable = await Task.Run(() => paths.Where(path => !File.Exists(path)).ToArray());
            if (row.Entry.Executables.SequenceEqual(paths))
            {
                row.SetUnavailableExecutables(unavailable);
                row.EvaluateIssue(AppSettings.Current.IgnoredGameIssues ?? [], Discovery.IsRunning);
                RefreshIssueViews();
            }
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

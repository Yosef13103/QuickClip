using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Views;

/// <summary>
/// Clips in the clips folder, grouped by game, newest first; clips can be ticked to add them to a montage project
/// or copy them out. Projects are listed under the folders and open in place of the clips.
/// </summary>
public partial class GalleryView : UserControl
{
    private readonly GalleryHoverPreview _hoverPreview = new();
    private const int DecodedThumbnailLimit = 96;
    private readonly LinkedList<ClipItem> _decodedThumbnails = new();
    private readonly Dictionary<ClipItem, LinkedListNode<ClipItem>> _thumbnailNodes = [];

    private sealed record Folder(GalleryFolderSelection Selection, string Name, int Count, DateTime Latest,
        bool Expandable = false, bool Expanded = false, int Depth = 0,
        string? Hint = null, string? DisplayPath = null)
    {
        public Visibility ExpandVisibility => Expandable ? Visibility.Visible : Visibility.Collapsed;
        public string ExpandGlyph => Expanded ? "\uE70D" : "\uE76C";
        public Thickness Indent => new(Depth * 16, 0, 0, 0);
    }
    private GalleryFolderSelection _folderSelection = new(GalleryFolderKey.All);
    private readonly HashSet<GalleryFolderKey> _expandedGames = [];
    private readonly Dictionary<string, GalleryFolderKey> _displayFolderKeys = new(StringComparer.OrdinalIgnoreCase);
    private LibraryCollection[] _collectionDefinitions = [];
    private LibraryTagDefinition[] _tagDefinitions = [];

    private const double CardGap = 14; // must match the card's right margin
    private const double PreferredCardWidth = 440;
    private int _preferredThumbnailWidth = 440;

    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(GalleryView), new PropertyMetadata(304d));
    public static readonly DependencyProperty PosterHeightProperty =
        DependencyProperty.Register(nameof(PosterHeight), typeof(double), typeof(GalleryView), new PropertyMetadata(170d));

    public double CardWidth => (double)GetValue(CardWidthProperty);
    public double PosterHeight => (double)GetValue(PosterHeightProperty);

    public static readonly DependencyProperty HasSelectionProperty =
        DependencyProperty.Register(nameof(HasSelection), typeof(bool), typeof(GalleryView));

    private readonly DispatcherTimer _reloadTimer;
    private readonly DispatcherTimer _agoTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _filterTimer;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private Point? _dragStart;      // where a press on a clip began, until it becomes a drag or a click
    private ClipItem? _dragItem;
    private bool _dragged;          // the press turned into a drag, so releasing it isn't a click
    private List<ClipItem> _clips = [];
    private List<ProjectSummary> _projects = [];
    private string _folder = ClipLibrary.AllClips;
    private string? _openProject;
    private string? _loadedRoot;
    private string[] _loadedExclusions = [];
    private bool _loadedIndexed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _changedScopes = new(StringComparer.OrdinalIgnoreCase);
    private bool _scanRoot = true;
    private readonly List<FileSystemWatcher> _watchers = [];
    private string[] _loadedSources = [];
    private string[] _availableSources = [];
    private int _columns = 3;
    private bool _loading, _reloadQueued, _rereadQueued, _released, _settingLists;
    private long _loadedOrganizationRevision = -1;
    private QuickClip.Discovery.GameCatalogSettings? _loadedFolderCatalog;
    private bool _updatingSelection, _editingAnnotations;
    private Task? _annotationJob;
    private GalleryClipFilter _filter = new("", "", false, false);
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<ClipItem> _visibleClips = [];
    private int _renderedColumns;
    public bool PersonalAnnotations => AppProfile.IsPersonal;
    internal IReadOnlyList<string> KnownTags { get; private set; } = [];
    internal IClipAnnotationActions AnnotationActions
    {
        get => GalleryDetailsEditor.AnnotationActions;
        set => GalleryDetailsEditor.AnnotationActions = value;
    }
    internal ILibraryInventorySource? InventorySource
    {
        set => StoragePane.InventorySource = value;
    }

    /// <summary>The user wants to edit a file.</summary>
    public event Action<string>? OpenRequested;
    internal event Action<string, FrameworkElement>? CardOpenRequested;

    /// <summary>The user wants to watch a clip; the folder's other clips come along for previous/next.</summary>
    public event Action<IReadOnlyList<ClipItem>, int>? PlayRequested;
    internal event Action? PlaybackLibraryChanged;
    internal ClipItem[] PlaybackLibrarySnapshot() => _clips.ToArray();

    /// <summary>Some clips are ticked (each card then shows its checkbox, and clicking a card ticks it).</summary>
    public bool HasSelection
    {
        get => (bool)GetValue(HasSelectionProperty);
        private set => SetValue(HasSelectionProperty, value);
    }

    public GalleryView()
    {
        InitializeComponent();
        _preferredThumbnailWidth = Math.Clamp(AppSettings.Current.GalleryThumbnailWidth, 180, 800);
        ClipArea.PreviewMouseWheel += ClipArea_PreviewMouseWheel;
        PreviewKeyDown += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || e.Key is not (Key.D0 or Key.NumPad0) ||
                Keyboard.FocusedElement is TextBox) return;
            _preferredThumbnailWidth = (int)PreferredCardWidth;
            LayoutCards(ClipArea.ActualWidth, refreshCards: true);
            AppSettings.Current.GalleryThumbnailWidth = _preferredThumbnailWidth;
            if (!AppSettings.Current.TrySave()) ShowStatus("Could not save the Gallery thumbnail size.");
            e.Handled = true;
        };
        GallerySidebarColumn.Width = new GridLength(Math.Clamp(
            AppSettings.Current.GallerySidebarWidth, 190, 520));
        GalleryDetailsEditor.KnownTags = () => KnownTags;
        GalleryDetailsEditor.AvailableTagsForGame = AvailableTagsForGame;
        GalleryDetailsEditor.AutoSaveEnabled = AppProfile.IsPersonal;
        GalleryDetailsEditor.AutoSaved += ApplyClipDetails;
        AnnotationActions = PersonalLibrary.Current;
        StorageButton.Visibility = PersonalAnnotations ? Visibility.Visible : Visibility.Collapsed;
        StoragePane.BackRequested += () =>
        {
            CloseStorage();
            StorageButton.Focus();
        };
        GalleryDetailsEditor.Cancelled += () => InlineDetails.Visibility = Visibility.Collapsed;
        GalleryDetailsEditor.Saved += (path, details) =>
        {
            InlineDetails.Visibility = Visibility.Collapsed;
            ApplyClipDetails(path, details);
        };
        OrganizationFilters.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        _filterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, async (_, _) =>
        {
            _filterTimer!.Stop();
            await SearchAsync();
        }, Dispatcher) { IsEnabled = false };
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer!.Stop();
            _ = ReloadAsync();
        }, Dispatcher) { IsEnabled = false };
        _agoTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var clip in _clips) clip.RefreshAgo();
        }, Dispatcher);
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) =>
        {
            _statusTimer!.Stop();
            StatusText.Visibility = Visibility.Collapsed;
        }, Dispatcher) { IsEnabled = false };

        ProjectPane.EditRequested += path => OpenRequested?.Invoke(path);
        ProjectPane.ProjectChanged += QueueReload;
        Recorder.Instance.ClipSaved += OnClipSaved;
        ClipDeletion.Current.Changed += OnDeletionChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _hoverPreview.Stop();
                return;
            }
            if (PersonalLibrary.Enabled && (_loadedOrganizationRevision != PersonalLibrary.Current.OrganizationRevision || !ReferenceEquals(_loadedFolderCatalog, AppSettings.Current.Games)))
                _ = ReloadAsync(rereadIndex: true);
            var settings = AppSettings.Current;
            if (_renderedZone != null && !Equals(_renderedZone, TimeZoneInfo.Local)) ShowFolder(preserveScroll: true);
            var sources = SavedRecordingFolders.Additional(settings);
            if (_loadedRoot != settings.ClipsFolder ||
                !_loadedSources.SequenceEqual(sources, SavedRecordingFolders.Paths) ||
                !_availableSources.SequenceEqual(AvailableFolders(settings.ClipsFolder, sources), SavedRecordingFolders.Paths) ||
                _loadedIndexed != PersonalLibrary.Enabled ||
                !_loadedExclusions.SequenceEqual(settings.LibraryExclusions ?? [])) _ = ReloadAsync();
        };
        Loaded += (_, _) => _ = ReloadAsync();
    }

    private void GallerySidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settings = AppSettings.Current;
        int width = Math.Clamp((int)Math.Round(GallerySidebarColumn.ActualWidth), 190, 520);
        if (settings.GallerySidebarWidth == width) return;
        int previous = settings.GallerySidebarWidth;
        settings.GallerySidebarWidth = width;
        if (settings.TrySave()) return;
        settings.GallerySidebarWidth = previous;
        GallerySidebarColumn.Width = new GridLength(Math.Clamp(previous, 190, 520));
        ShowStatus("Could not save the Gallery sidebar width.");
    }

    /// <summary>Stops watching the folder, closes the project preview and drops the thumbnails (the window is closing).</summary>

    public async Task CloseAsync()
    {
        _released = true;
        _lifetime.Cancel();
        _hoverPreview.Stop();
        Recorder.Instance.ClipSaved -= OnClipSaved;
        ClipDeletion.Current.Changed -= OnDeletionChanged;
        StopWatchingFolders();
        _reloadTimer.Stop();
        _agoTimer.Stop();
        _statusTimer.Stop();
        _filterTimer.Stop();
        _searchCancellation?.Cancel();
        if (_annotationJob is { } job)
        {
            try { await job; }
            catch (Exception) { /* The initiating command handles the failure. */ }
        }
        Rows.ItemsSource = null;
        _clips = [];
        await ProjectPane.CloseAsync();
    }

    private void OnClipSaved(SavedClip clip) => Dispatcher.BeginInvoke(async () =>
    {
        if (_released) return;
        try { await PersonalLibrary.Current.RepairRecordedCollectionAsync(clip.Path, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Log.Failure(DiagnosticCategory.Library, "Could not recover saved collection metadata", ex); }
        if (!_released) QueueScopedReload(clip.Path);
    });

    private void QueueScopedReload(string path)
    {
        if (_released || _loadedRoot == null) return;
        string directory = Path.GetDirectoryName(path) ?? _loadedRoot;
        string relative = Path.GetRelativePath(_loadedRoot, directory);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || _changedScopes.Count >= 16)
            _scanRoot = true;
        else
            _changedScopes.Add(relative);
        QueueReload();
    }

    private void QueueReload()
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    internal void RefreshConfiguredSources()
    {
        if (_released) return;
        _scanRoot = true;
        _ = ReloadAsync();
    }

    internal async Task RefreshVisibleAsync()
    {
        if (_released || _loading || _editingAnnotations || !await FlushAnnotationsAsync() || !IsVisible) return;
        await ReloadAsync(rereadIndex: true);
    }

    private async Task ReloadAsync(bool rereadIndex = false)
    {
        if (_released) return;
        if (_loading)
        {
            _reloadQueued = true;
            _rereadQueued |= rereadIndex;
            return;
        }
        _loading = true;
        long organizationRevision = PersonalLibrary.Current.OrganizationRevision;
        rereadIndex |= organizationRevision != _loadedOrganizationRevision;
        using var activity = QuickClip.Shell.BackgroundActivity.Begin("Refreshing Gallery");
        try
        {
            string root = AppSettings.Current.ClipsFolder;
            string[] sources = SavedRecordingFolders.Additional(AppSettings.Current);
            string[] available = await Task.Run(() => AvailableFolders(root, sources), _lifetime.Token);
            string[] exclusions = AppSettings.Current.LibraryExclusions ?? [];
            bool indexed = PersonalLibrary.Enabled && !ClipLibrary.IsMomentsPath(root);
            bool foldersChanged = root != _loadedRoot || !_loadedSources.SequenceEqual(sources, SavedRecordingFolders.Paths) ||
                !_availableSources.SequenceEqual(available, SavedRecordingFolders.Paths);
            if (foldersChanged) WatchFolders(root, available);
            bool fullScan = _scanRoot || foldersChanged || indexed != _loadedIndexed ||
                !_loadedExclusions.SequenceEqual(exclusions);
            var scopes = fullScan ? null : _changedScopes.ToArray();
            _changedScopes.Clear();
            _scanRoot = false;
            _loadedRoot = root;
            _loadedSources = sources;
            _availableSources = available;
            _loadedExclusions = exclusions;
            _loadedIndexed = indexed;
            InventorySource = indexed ? PersonalLibrary.Current : null;
            var previous = _clips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
            // Show the previously indexed, still-valid files before reconciling
            // the filesystem. Watchers are already attached, so changes during
            // this phase schedule another refresh.
            if (indexed && fullScan && previous.Count == 0 && available.Length > 0)
            {
                try
                {
                    activity.Report("Loading saved Gallery index");
                    var cached = await PersonalLibrary.Current.ReadIndexedClipsAsync(_lifetime.Token,
                        folders: available, exclusions: exclusions);
                    if (_released) return;
                    if (cached.Count > 0)
                    {
                        _clips = cached;
                        await LoadDraftRangesAsync();
                        if (_released) return;
                        var catalog = AppSettings.Current.Games;
                        _collectionDefinitions = await PersonalLibrary.Current.ReadCollectionsAsync(_lifetime.Token);
                        _loadedFolderCatalog = catalog;
                        _tagDefinitions = await PersonalLibrary.Current.ReadTagDefinitionsAsync(_lifetime.Token);
                        if (_released) return;
                        PublishClips(previous);
                        previous = _clips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
                        PlaybackLibraryChanged?.Invoke();
                        activity.Report("Checking Gallery for changes");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Failure(DiagnosticCategory.Library, "Could not load the saved Gallery index", ex);
                }
            }
            _projects = await Task.Run(() => available.SelectMany(LoadProjects).ToList(), _lifetime.Token);
            if (indexed)
            {
                if (scopes == null || scopes.Length > 0 || _clips.Count == 0)
                {
                    var loaded = new List<ClipItem>();
                    bool incomplete = false;
                    var folders = (scopes == null ? sources : []).Prepend(root).ToArray();
                    int folderNumber = 0;
                    foreach (string folder in folders)
                    {
                        try
                        {
                            int number = ++folderNumber;
                            activity.Report($"Indexing Gallery · folder {number}/{folders.Length}");
                            var result = await PersonalLibrary.Current.LoadAsync(folder, exclusions,
                                SavedRecordingFolders.Paths.Equals(folder, root) ? scopes : null, _lifetime.Token,
                                (message, fraction) => activity.Report($"{message} · folder {number}/{folders.Length}",
                                    fraction is double f && folders.Length == 1 ? f : null));
                            loaded.AddRange(result.Clips);
                            incomplete |= !result.Scan.Complete;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            incomplete = true;
                            // An incomplete scan must not resurrect stale or excluded paths.
                            try
                            {
                                loaded.AddRange(await PersonalLibrary.Current.ReadIndexedClipsAsync(_lifetime.Token,
                                    folders: [folder], exclusions: exclusions));
                            }
                            catch (Exception cacheError) when (cacheError is not OperationCanceledException)
                            {
                                Log.Failure(DiagnosticCategory.Library, "Could not recover the cached source", cacheError);
                            }
                            Log.Failure(DiagnosticCategory.Library, "Could not refresh an indexed clips folder", ex);
                        }
                    }
                    if (scopes != null)
                        loaded.AddRange(_clips.Where(clip => sources.Any(folder => LibraryFiles.Within(clip.Path, folder))));
                    _clips = loaded.OrderByDescending(clip => clip.Details.CapturedAt).ToList();
                    if (incomplete) ShowStatus("One or more Gallery sources are unavailable or could not be fully scanned.");
                }
                else if (rereadIndex)
                {
                    _clips = await PersonalLibrary.Current.ReadIndexedClipsAsync(_lifetime.Token, folders: available, exclusions: exclusions);
                }
                else
                {
                    RebuildProjects();
                    return;
                }
            }
            else
                _clips = await Task.Run(() => available.SelectMany(folder => ClipLibrary.Scan(folder, exclusions, previous))
                    .OrderByDescending(clip => clip.Details.CapturedAt).ToList(), _lifetime.Token);
            if (sources.Any(folder => !available.Contains(folder, SavedRecordingFolders.Paths)))
                ShowStatus("A saved Gallery source is offline. Its folder remains in Settings → Library.");
            if (_released) return;
            if (AppProfile.IsPersonal) await LoadDraftRangesAsync();
            if (_released) return;
            activity.Report("Updating Gallery view");
            if (PersonalLibrary.Enabled)
            {
                var catalog = AppSettings.Current.Games;
                _collectionDefinitions = await PersonalLibrary.Current.ReadCollectionsAsync(_lifetime.Token);
                _loadedFolderCatalog = catalog;
            }
            else _collectionDefinitions = [];
            _tagDefinitions = await PersonalLibrary.Current.ReadTagDefinitionsAsync(_lifetime.Token);
            if (_released) return;
            _loadedOrganizationRevision = organizationRevision;
            if (organizationRevision != PersonalLibrary.Current.OrganizationRevision)
            {
                _reloadQueued = true;
                _rereadQueued = true;
            }
            _searchCancellation?.Cancel();
            PublishClips(previous);
        }
        catch (Exception ex)
        {
            if (!_released)
            {
                _scanRoot = true;
                ShowStatus("Couldn’t refresh the clips folder. Check its availability and library settings.");
                Log.Failure(DiagnosticCategory.Library, "Loading clips failed", ex);
            }
        }
        finally
        {
            _loading = false;
            if (!_released && _clips.Count > 0) PlaybackLibraryChanged?.Invoke();
            if (_reloadQueued && !_released)
            {
                _reloadQueued = false;
                bool reread = _rereadQueued;
                _rereadQueued = false;
                // Schedule after this lease exits, including early-return paths.
                _ = Dispatcher.BeginInvoke(new Action(() => _ = ReloadAsync(rereadIndex: reread)));
            }
        }
    }

    private void PublishClips(Dictionary<string, ClipItem> previous)
    {
        foreach (var clip in _clips)
        {
            // Keep thumbnails already on screen so a new clip doesn't make every card flicker.
            if (previous.TryGetValue(clip.Path, out var old) && !ReferenceEquals(old, clip))
            {
                if (old.Details.IndexId is { } clipId && clipId == clip.Details.IndexId &&
                    old.Details.IndexRevision > clip.Details.IndexRevision)
                    clip.ApplyDetails(old.Details); // A save callback arrived while the query was in flight.
                if (old.Size == clip.Size && old.Thumbnail != null &&
                    old.File.LastWriteTimeUtc == clip.File.LastWriteTimeUtc && old.File.CreationTimeUtc == clip.File.CreationTimeUtc)
                {
                    clip.Thumbnail = old.Thumbnail;
                    clip.ThumbnailWidth = old.ThumbnailWidth;
                    if (_thumbnailNodes.Remove(old, out var node)) _decodedThumbnails.Remove(node);
                    RememberThumbnail(clip);
                }
            }
            clip.IsSelected = _selected.Contains(clip.Path);
            // Incremental refresh can retain items from other sources.
            clip.SelectionChanged -= OnClipSelectionChanged;
            clip.SelectionChanged += OnClipSelectionChanged;
        }
        _selected.IntersectWith(_clips.Select(c => c.Path));
        RefreshKnownTags();
        RebuildFolders(preserveScroll: previous.Count > 0);
        RebuildProjects();
        UpdateSelection();
    }

    private static string[] AvailableFolders(string root, IEnumerable<string> sources) =>
        sources.Prepend(root).Where(folder => !ClipLibrary.IsMomentsPath(folder) &&
            LibraryFiles.InspectPath(folder, directory: true) == ClipAvailability.Available).ToArray();

    private static List<ProjectSummary> LoadProjects(string folder)
    {
        if (LibraryFiles.InspectPath(ProjectLibrary.Root(folder), directory: true) != ClipAvailability.Available) return [];
        try { return ProjectLibrary.List(folder); }
        catch (Exception ex) when (LibraryFiles.IsAccessError(ex)) { return []; }
    }

    private void StopWatchingFolders()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }

    private void WatchFolders(string primary, IEnumerable<string> available)
    {
        StopWatchingFolders();
        foreach (string folder in available) Watch(folder, SavedRecordingFolders.Paths.Equals(primary, folder));
    }

    private void Watch(string root, bool primary)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            bool Relevant(string path) => !Path.GetFileName(path).StartsWith(".quickclip-tmp", StringComparison.OrdinalIgnoreCase) &&
                !ClipLibrary.IsExcluded(Path.GetRelativePath(root, path), AppSettings.Current.LibraryExclusions);
            void Changed(string path)
            {
                if (primary) QueueScopedReload(path);
                else { _scanRoot = true; QueueReload(); }
            }
            FileSystemEventHandler changed = (_, e) =>
            {
                if (Relevant(e.FullPath)) Dispatcher.BeginInvoke(() => Changed(e.FullPath));
            };
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Changed += changed;
            watcher.Renamed += (_, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (Relevant(e.OldFullPath)) Changed(e.OldFullPath);
                    if (Relevant(e.FullPath)) Changed(e.FullPath);
                });
            };
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { _scanRoot = true; QueueReload(); });
            _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Can't watch a clips folder", ex);
        }
    }

    // ---- folders ------------------------------------------------------------------------------

    private bool _sortFoldersByName;
    private bool _sortFoldersDescending = true;

    private void FolderNameSort_Click(object sender, RoutedEventArgs e)
    {
        _sortFoldersDescending = _sortFoldersByName && !_sortFoldersDescending;
        _sortFoldersByName = true;
        RebuildFolders(preserveScroll: true);
    }

    private void FolderDateSort_Click(object sender, RoutedEventArgs e)
    {
        _sortFoldersDescending = _sortFoldersByName || !_sortFoldersDescending;
        _sortFoldersByName = false;
        RebuildFolders(preserveScroll: true);
    }

    private IEnumerable<T> SortFolders<T>(IEnumerable<T> source, Func<T,string> name, Func<T,DateTime> date) =>
        GalleryFolderSort.Apply(source, name, date, _sortFoldersByName, _sortFoldersDescending);

    private static void UpdateSortButton(Button button, bool alphabetical, bool descending, bool active, string nextAction)
    {
        var glyph = new System.Windows.Controls.Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        System.Windows.Shapes.Path Stroke(string data)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(data), StrokeThickness = 1.7,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PrimaryTextBrush");
            return path;
        }
        glyph.Children.Add(Stroke(alphabetical
            ? (descending ? "M3,3 H11 L3,10 H11 M3,21 L7,14 L11,21 M4.5,19 H9.5"
                : "M3,10 L7,3 L11,10 M4.5,8 H9.5 M3,14 H11 L3,21 H11")
            : "M13,11 A6,6 0 1 1 1,11 A6,6 0 1 1 13,11 M7,7 V11 L10,13"));
        glyph.Children.Add(Stroke(descending ? "M18,4 V20 M14,16 L18,20 L22,16" : "M18,20 V4 M14,8 L18,4 L22,8"));
        button.Content = new Viewbox { Child = glyph, Width = 22, Height = 22 };
        button.SetResourceReference(Control.BorderBrushProperty, active ? "AccentFillColorDefaultBrush" : "BorderBrush");
        string current = active ? (alphabetical ? (descending ? "Z to A" : "A to Z") : (descending ? "Newest clip first" : "Oldest clip first")) + ". " : "";
        button.ToolTip = current + nextAction;
        System.Windows.Automation.AutomationProperties.SetName(button, current + nextAction);
    }

    private void RebuildFolders(bool preserveScroll = false)
    {
        bool nameDescending = _sortFoldersByName && _sortFoldersDescending;
        bool dateDescending = _sortFoldersByName || _sortFoldersDescending;
        string nameSort = _sortFoldersByName && !_sortFoldersDescending ? "Sort folders Z to A" : "Sort folders A to Z";
        string dateSort = !_sortFoldersByName && _sortFoldersDescending ? "Sort folders by oldest clip" : "Sort folders by newest clip";
        UpdateSortButton(FolderNameSort, true, nameDescending, _sortFoldersByName, nameSort);
        UpdateSortButton(FolderDateSort, false, dateDescending, !_sortFoldersByName, dateSort);
        var visible = _clips.ToArray();
        var folders = new List<Folder> { new(new(GalleryFolderKey.All), ClipLibrary.AllClips, visible.Length,
            visible.FirstOrDefault()?.Created ?? DateTime.MinValue) };
        // Filesystem hierarchy is presentation-only: do not rewrite legacy game metadata
        // or assign a catalog ID merely because folder names look alike.
        var mapped = visible.Select(clip =>
        {
            var disk = GalleryDiskFolders.Locate(clip.Path, _availableSources);
            return (Clip: clip, Disk: disk, Name: GalleryDiskFolders.PresentedGame(clip.Details, disk));
        }).ToArray();
        var unambiguousIds = mapped.Where(item => item.Clip.Details.GameId != null)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Ids: group.Select(item => item.Clip.Details.GameId!)
                .Distinct(StringComparer.Ordinal).Take(2).ToArray()))
            .Where(group => group.Ids.Length == 1)
            .ToDictionary(group => group.Name, group => group.Ids[0], StringComparer.OrdinalIgnoreCase);
        _displayFolderKeys.Clear();
        foreach (var item in mapped)
            _displayFolderKeys[item.Clip.Path] = GalleryDiskFolders.PresentedKey(item.Clip.Details,
                item.Name, unambiguousIds.GetValueOrDefault(item.Name));

        var associatedNames = mapped.Where(item => item.Clip.Details.GameId != null)
            .Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopedDefinitions = _collectionDefinitions.Where(d => d.GameId != null).ToLookup(d => d.GameId);
        foreach (var group in SortFolders(mapped.GroupBy(item => _displayFolderKeys[item.Clip.Path]),
                     g => g.First().Name, g => g.Max(item => item.Clip.Created)))
        {
            var entries = group.ToArray();
            var clips = entries.Select(item => item.Clip).ToArray();
            var key = group.Key;
            string name = entries[0].Name;
            if (key.GameId == null && associatedNames.Contains(name)) name += " (unassociated)";
            var definitions = scopedDefinitions[key.GameId]
                .Concat(clips.Select(c => c.Details.Collection).OfType<LibraryCollection>())
                .DistinctBy(d => d.Id).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();

            // Use full physical paths as child identities. Same-named children under
            // different games or Gallery roots never share an item or selection.
            var physical = new Dictionary<string,
                (string? Parent, string Name, int Depth, int Count, DateTime Latest, string DisplayPath)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in entries)
            {
                if (item.Disk is not { } disk) continue;
                foreach (var child in disk.ChildAncestors())
                {
                    string relative = Path.GetRelativePath(Path.Combine(disk.Root, disk.Game), child.Path)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    string display = entries[0].Name + " / " + relative;
                    if (physical.TryGetValue(child.Path, out var prior))
                        physical[child.Path] = (child.Parent, child.Name, child.Depth,
                            prior.Count + 1, item.Clip.Created > prior.Latest ? item.Clip.Created : prior.Latest, display);
                    else
                        physical.Add(child.Path, (child.Parent, child.Name, child.Depth, 1, item.Clip.Created, display));
                }
            }
            bool hasChildren = definitions.Length > 0 || physical.Count > 0;
            bool expanded = hasChildren && _expandedGames.Contains(key);
            folders.Add(new(new(key), name, clips.Length, clips.Max(c => c.Created), hasChildren, expanded));
            if (!expanded) continue;

            void AddDiskChildren(string? parent)
            {
                var direct = physical.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Value.Parent, parent));
                foreach (var child in SortFolders(direct, item => item.Value.Name, item => item.Value.Latest))
                {
                    var entry = child.Value;
                    folders.Add(new(new(key, true, null, child.Key), entry.Name, entry.Count,
                        entry.Latest, Depth: entry.Depth, Hint: child.Key, DisplayPath: entry.DisplayPath));
                    AddDiskChildren(child.Key);
                }
            }
            AddDiskChildren(null);

            if (definitions.Length == 0) continue;
            folders.Add(new(new(key, true), "Uncategorized", clips.Count(c => c.Details.Collection == null),
                DateTime.MinValue, Depth: 1));
            foreach (var definition in SortFolders(definitions, d => d.Name,
                         d => clips.Where(c => c.Details.Collection?.Id == d.Id)
                            .Select(c => c.Created).DefaultIfEmpty(DateTime.MinValue).Max()))
                folders.Add(new(new(key, true, definition.Id), definition.Name,
                    clips.Count(c => c.Details.Collection?.Id == definition.Id), DateTime.MinValue, Depth: 1));
        }
        var chosen = folders.FirstOrDefault(f => f.Selection == _folderSelection)
            ?? folders.FirstOrDefault(f => f.Selection == new GalleryFolderSelection(_folderSelection.Game))
            ?? folders[0];
        _folderSelection = chosen.Selection;
        _folder = chosen.Name;
        _settingLists = true;
        FolderList.ItemsSource = folders;
        FolderList.SelectedItem = _openProject != null ? null : chosen;
        _settingLists = false;
        ShowFolder(preserveScroll);
    }

    private void ExpandFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Folder { Expandable: true } f }) return;
        e.Handled = true;
        if (!_expandedGames.Add(f.Selection.Game)) _expandedGames.Remove(f.Selection.Game);
        _searchCancellation?.Cancel();
        RebuildFolders(preserveScroll: true);
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLists || FolderList.SelectedItem is not Folder f) return;
        CloseStorage();
        if (_openProject != null) CloseProject();
        if (f.Selection == _folderSelection) return;
        _searchCancellation?.Cancel();
        _folderSelection = f.Selection;
        _folder = f.Name;
        ShowFolder();
    }

    private IReadOnlyList<ClipItem> CurrentClips => _visibleClips;
    internal string[] EditorNavigationPaths() => CurrentClips.Select(clip => clip.Path).ToArray();

    private void OrganizationFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_filterTimer == null || _released) return;
        _searchCancellation?.Cancel();
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async Task SearchAsync()
    {
        var filter = new GalleryClipFilter(ClipSearchBox.Text.Trim(), "",
            FavoritesFilter.IsChecked == true, false)
        { DefinitionFilters = _exactTags.Select(t => new GalleryTagFilter(t.Id, t.Name)).ToArray(), MatchAllTags = _matchAllTags };
        var folder = _folderSelection;
        var clips = _clips;
        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try
        {
            var matches = await Task.Run(() => filter.Apply(clips.Where(c => folder.Matches(c.Details)), ClipLibrary.AllClips, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || _released) return;
            _filter = filter;
            ApplyRows(matches, preserveScroll: true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_searchCancellation == cancellation) _searchCancellation = null;
        }
    }

    private void RefreshKnownTags()
    {
        if (PersonalLibrary.Enabled)
        {
            _tagDefinitions = _tagDefinitions.Concat(_clips.SelectMany(c => c.Details.TagDefinitions))
                .DistinctBy(t => t.Id).ToArray();
            KnownTags = ClipTagChoices.Match(_tagDefinitions.Where(t => t.GameId == null).Select(t => t.Name), "", []).ToArray();
            _exactTags = _exactTags.Select(t => t.Id is long id
                ? _tagDefinitions.FirstOrDefault(d => d.Id == id) is { } definition ? TagChoiceFor(definition) : null
                : t).OfType<TagFilterChoice>().ToArray();
        }
        else KnownTags = ClipTagChoices.Match(_clips.SelectMany(clip => clip.Details.Tags), "", []).ToArray();
        _filter = _filter with { DefinitionFilters = _exactTags.Select(t => new GalleryTagFilter(t.Id, t.Name)).ToArray() };
        RefreshTagFilterButton();
    }

    internal IReadOnlyList<string> AvailableTagsForGame(string? gameId) => PersonalLibrary.Enabled
        ? ClipTagChoices.Match(_tagDefinitions.Where(t => t.AppliesTo(gameId)).Select(t => t.Name), "", []).ToArray()
        : KnownTags;

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ClipSearchBox.Clear();
        _exactTags = [];
        _matchAllTags = true;
        _filter = _filter with { DefinitionFilters = _exactTags.Select(t => new GalleryTagFilter(t.Id, t.Name)).ToArray() };
        RefreshTagFilterButton();
        OrganizationFilter_Changed(this, new RoutedEventArgs());
        FavoritesFilter.IsChecked = false;
    }

    internal event Action<string, ClipDetails>? DetailsCommitted;

    internal void ApplyClipDetails(string path, ClipDetails details) => ApplyClipDetails([new(path, details, null)]);

    private void ApplyClipDetails(IReadOnlyList<ClipAnnotationResult> changes)
    {
        if (_loading) _reloadQueued = true;
        var saved = changes.Where(change => change.Details != null)
            .ToDictionary(change => change.Path, change => change.Details!, StringComparer.OrdinalIgnoreCase);
        bool regroup = false;
        foreach (var clip in _clips)
        {
            if (!saved.TryGetValue(clip.Path, out var details)) continue;
            if (clip.Details.IndexId == details.IndexId && clip.Details.IndexRevision > details.IndexRevision) continue;
            regroup |= clip.Folder != details.Game || clip.Details.GameId != details.GameId || clip.Details.Collection != details.Collection || clip.Details.RecordingSessionId != details.RecordingSessionId;
            clip.ApplyDetails(details);
        }
        if (saved.Count == 0) return;
        foreach (var (path, details) in saved)
        {
            if (SavedRecordingFolders.Paths.Equals(GalleryDetailsEditor.AnnotationTarget?.Path, path))
                GalleryDetailsEditor.ApplyCommittedDetails(details);
            DetailsCommitted?.Invoke(path, details);
        }
        RefreshKnownTags();
        if (regroup || _filter.FavoritesFirst) _renderedColumns = 0;
        if (regroup) { RebuildFolders(preserveScroll: true); PlaybackLibraryChanged?.Invoke(); }
        else ShowFolder(preserveScroll: true);
    }

    private GalleryFolderKey FolderKeyFor(ClipItem clip) =>
        _displayFolderKeys.TryGetValue(clip.Path, out var key) ? key : GalleryFolderKey.For(clip.Details);

    private bool MatchesFolder(ClipItem clip) =>
        _folderSelection.Matches(clip.Details, clip.Path, FolderKeyFor(clip));

    private void ShowFolder(bool preserveScroll = false)
    {
        if (_searchCancellation is { IsCancellationRequested: false })
            OrganizationFilter_Changed(this, new RoutedEventArgs());
        ApplyRows(_filter.Apply(_clips.Where(MatchesFolder), ClipLibrary.AllClips), preserveScroll);
    }

    private IReadOnlyList<ClipItem> _filteredClips = [];
    private TimeZoneInfo? _renderedZone;
    private bool _renderedFavoritesFirst;

    private void ApplyRows(IReadOnlyList<ClipItem> clips, bool preserveScroll)
    {
        FolderTitle.Text = _folderSelection.IsChild && FolderList.ItemsSource is List<Folder> folders
            ? folders.FirstOrDefault(f => f.Selection == _folderSelection)?.DisplayPath ??
              (folders.FirstOrDefault(f => f.Selection == new GalleryFolderSelection(_folderSelection.Game))?.Name ?? "") + " / " + _folder
            : _folder;
        FolderInfo.Text = clips.Count == 0 ? "" : $"{clips.Count} clip{(clips.Count == 1 ? "" : "s")}  ·  {Fmt.Size(clips.Sum(c => c.Size))}";
        var zone = TimeZoneInfo.Local;
        UpdateGameHub(clips);
        if (_renderedColumns != _columns || _renderedFavoritesFirst != _filter.FavoritesFirst ||
            !Equals(_renderedZone, zone) || !_filteredClips.SequenceEqual(clips))
        {
            var scroll = Rows.Template?.FindName("GalleryScroll", Rows) as ScrollViewer;
            double offset = scroll?.VerticalOffset ?? 0;
            _renderedColumns = _columns;
            _filteredClips = clips;
            _renderedZone = zone;
            _renderedFavoritesFirst = _filter.FavoritesFirst;
            var rows = GalleryDayFeed.Build(clips, _columns, zone, _renderedFavoritesFirst);
            _visibleClips = rows.OfType<GalleryClipRow>().SelectMany(row => row.Clips).ToArray();
            Rows.ItemsSource = rows;
            if (preserveScroll && offset > 0) scroll?.ScrollToVerticalOffset(offset);
        }
        UpdateSelection();

        EmptyState.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _filter.IsFiltered ? "No matching clips" : "No clips yet";
        _agoTimer.Start();
        if (_filter.IsFiltered)
        {
            EmptyText.Text = "Change your search or clear the filters to see more clips.";
            return;
        }
        var s = AppSettings.Current;
        string save = App.Host?.ActiveSaveShortcut is { } shortcut
            ? $"Press {shortcut} while you play"
            : "Use Save clip while recording, or set a shortcut in Settings";
        EmptyText.Text = $"{save} to save the last {RecordingPresets.DurationLabel(s.BufferSeconds)}. " +
                         $"Clips are filed by game in {s.ClipsFolder}.";
    }

    internal static int ColumnsForWidth(double available) =>
        Math.Clamp((int)(available / (PreferredCardWidth + CardGap)), 1, 4);

    private void ClipArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) LayoutCards(e.NewSize.Width);
    }

    internal static int ZoomCardWidth(double displayedWidth, int preferred, int wheelDelta)
    {
        // Default cards stretch to fill each row. Start the first wheel movement
        // from the visible width, not the nominal 440px setting.
        int current = preferred == PreferredCardWidth
            ? Math.Clamp((int)Math.Round(displayedWidth), 180, 800)
            : Math.Clamp(preferred, 180, 800);
        return Math.Clamp(current + Math.Sign(wheelDelta) * 40, 180, 800);
    }

    private void ClipArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        int target = ZoomCardWidth(CardWidth, _preferredThumbnailWidth, e.Delta);
        if (target != _preferredThumbnailWidth)
        {
            _preferredThumbnailWidth = target;
            LayoutCards(ClipArea.ActualWidth, refreshCards: true);
            AppSettings.Current.GalleryThumbnailWidth = target;
            if (!AppSettings.Current.TrySave())
                ShowStatus("Could not save the Gallery thumbnail size.");
        }
        e.Handled = true; // Ctrl+wheel changes thumbnail size, not the scroll position.
    }

    private void LayoutCards(double available, bool refreshCards = false)
    {
        if (available <= 0) return;
        double width = Math.Max(1, available - SystemParameters.VerticalScrollBarWidth);
        int maximum = _preferredThumbnailWidth >= PreferredCardWidth ? 4 : 8;
        int columns = Math.Clamp((int)(width / (_preferredThumbnailWidth + CardGap)), 1, maximum);
        double cardWidth = _preferredThumbnailWidth == PreferredCardWidth
            ? Math.Max(1, Math.Floor(width / columns) - CardGap)
            : Math.Max(1, Math.Min(_preferredThumbnailWidth, width - CardGap));
        SetValue(CardWidthProperty, cardWidth);
        SetValue(PosterHeightProperty, Math.Max(1, cardWidth - 2) * 9 / 16);
        if (columns == _columns && !refreshCards) return;
        _columns = columns;
        if (refreshCards) _renderedColumns = 0; // Re-request appropriately sized thumbnails.
        if (_clips.Count > 0) ShowFolder(preserveScroll: true);
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ---- cards ---------------------------------------------------------------------------------

    private static ClipItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    private void Card_Loaded(object sender, RoutedEventArgs e) => RequestThumbnail((FrameworkElement)sender);

    private void Poster_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is FrameworkElement poster && poster.IsLoaded) RequestThumbnail(poster);
    }

    private void Poster_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Window.GetWindow(this)?.IsActive == true && sender is Grid poster && ItemOf(poster) is { } item)
            _hoverPreview.Start(poster, item);
    }

    internal void StopHoverPreview() => _hoverPreview.Stop();

    private void Poster_MouseLeave(object sender, RoutedEventArgs e)
    {
        if (sender is Grid poster) _hoverPreview.Stop(poster);
    }

    // Scrolling reuses cards for other clips without raising Loaded again.
    private void Card_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && element.IsLoaded) RequestThumbnail(element);
    }

    private async void RequestThumbnail(FrameworkElement card)
    {
        if (ItemOf(card) is not { } item) return;
        int width = ThumbnailCache.WidthFor(CardWidth * VisualTreeHelper.GetDpi(card).DpiScaleX);
        if (item.ThumbnailWidth >= width)
        {
            if (item.Thumbnail != null) RememberThumbnail(item);
            return;
        }
        item.ThumbnailWidth = width;
        try
        {
            var token = _lifetime.Token;
            string? thumb = await Task.Run(() => ThumbnailCache.GetAsync(item.File, item.Duration, width, token), token);
            if (thumb != null)
            {
                var image = await Task.Run(() => ThumbnailCache.LoadImage(thumb, width));
                if (!_released && item.ThumbnailWidth == width)
                {
                    item.Thumbnail = image;
                    RememberThumbnail(item);
                }
            }
            // A failed decode keeps the placeholder until the clip changes or the library is refreshed.
        }
        catch (OperationCanceledException) when (_released) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Loading thumbnail failed", ex);
        }
    }

    private void RememberThumbnail(ClipItem item)
    {
        if (_thumbnailNodes.Remove(item, out var previous)) _decodedThumbnails.Remove(previous);
        _thumbnailNodes[item] = _decodedThumbnails.AddFirst(item);
        if (_decodedThumbnails.Count <= DecodedThumbnailLimit) return;
        var oldest = _decodedThumbnails.Last!.Value;
        _decodedThumbnails.RemoveLast();
        _thumbnailNodes.Remove(oldest);
        oldest.Thumbnail = null;
        oldest.ThumbnailWidth = 0;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (_dragged || IsCardControl(e.OriginalSource, sender) || ItemOf(sender) is not { } item) return;
        // While clips are being picked, a click picks; Ctrl+click always does.
        if (HasSelection || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) item.IsSelected = !item.IsSelected;
        else OpenClip(item, sender as FrameworkElement);
    }

    private bool IsAvailable(ClipItem item)
    {
        if (File.Exists(item.Path)) return true;
        ShowStatus("This clip is missing or unavailable.");
        _scanRoot = true;
        QueueReload();
        return false;
    }

    private void OpenClip(ClipItem item, FrameworkElement? card = null)
    {
        if (!IsAvailable(item)) return;
        if (AppProfile.IsPersonal && card != null && CardOpenRequested is { } open)
            open(item.Path, card);
        else OpenRequested?.Invoke(item.Path);
    }

    internal void ShowEditorOpenStatus(string message) => ShowStatus(message);

    private void Play(ClipItem? item)
    {
        if (item == null || !IsAvailable(item)) return;
        var clips = CurrentClips.ToList();
        int index = clips.IndexOf(item);
        if (index >= 0) PlayRequested?.Invoke(clips, index);
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e) => Play(ItemOf(sender));

    private void ShareClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _ = ClipSharing.ShareAsync(item.Path);
    }

    /// <summary>What a clip's menu or a drag acts on: every selected clip if this one is selected, else just it.</summary>
    private List<string> ClipsFor(ClipItem item) => item.IsSelected ? SelectedPaths() : [item.Path];

    private static IEnumerable<MenuItem> MenuEntries(ItemCollection items)
    {
        foreach (var entry in items.OfType<MenuItem>())
        {
            yield return entry;
            if (entry.Tag is "organize")
                foreach (var child in MenuEntries(entry.Items)) yield return child;
        }
    }

    private void ClipMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not ClipItem item) return;
        int count = ClipsFor(item).Count;
        string clips = count == 1 ? "" : $" {count} selected clips";
        // Snapshot before populating dynamic submenus.
        foreach (var entry in MenuEntries(menu.Items).ToArray())
        {
            entry.DataContext = item;
            if (entry.Tag is "external-player")
                entry.Header = AppSettings.Current.ExternalPlayerPath == null ? "Open with Windows default" : "Open in " + ExternalPlayer.Label(AppSettings.Current.ExternalPlayerPath);
            if (entry.Tag is "external-choices")
                ExternalPlayerDialogs.Populate(entry.Items, Window.GetWindow(this), player =>
                {
                    if (IsAvailable(item)) ExternalPlayerDialogs.Open(Window.GetWindow(this), item.Path, player);
                });
            if (entry.Tag is "share-profiles")
                SharingDialogs.Populate(entry.Items, Window.GetWindow(this), profile => _ = ClipSharing.ShareAsync(item.Path, profile));
            if (entry.Tag is "details" or "favorite") entry.Visibility = PersonalAnnotations ? Visibility.Visible : Visibility.Collapsed;
            if (entry.Tag is "collection") entry.Visibility = PersonalLibrary.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (entry.Tag is "favorite") entry.Header = item.IsFavorite ? "Remove from favorites" : "Add to favorites";
            if (entry.Tag as string == "copy") entry.Header = count == 1 ? "Copy to folder…" : $"Copy{clips} to folder…";
            if (entry.Tag as string != "projects") continue;
            entry.Header = count == 1 ? "Add to project" : $"Add{clips} to project";
            entry.Items.Clear();
            foreach (var project in _projects)
            {
                // A TextBlock header: an underscore in a name isn't an access key.
                var add = new MenuItem { Header = new TextBlock { Text = $"{project.Name}  ({project.ClipCount})" } };
                add.Click += async (_, _) => await AddClipsToAsync(ClipsFor(item), project.Folder, open: false);
                entry.Items.Add(add);
            }
            if (_projects.Count > 0) entry.Items.Add(new Separator());
            var create = new MenuItem { Header = "New project…" };
            create.Click += async (_, _) =>
            {
                var paths = ClipsFor(item);
                if (CreateProject() is { } folder) await AddClipsToAsync(paths, folder, open: true);
            };
            entry.Items.Add(create);
        }
    }

    private async void EditClipDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!AppProfile.IsPersonal || ItemOf(sender) is not { } item) return;
        if (!await FlushAnnotationsAsync()) return;
        InlineDetails.Visibility = Visibility.Visible;
        GalleryDetailsEditor.Open(item.Path, item.Details, focus: true, duration: item.Duration);
    }

    private async void FavoriteCard_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (_editingAnnotations || sender is not Button button) return;
        bool next = !item.IsFavorite;
        button.SetCurrentValue(UIElement.IsEnabledProperty, false);
        var animation = new System.Windows.Media.Animation.DoubleAnimation(0.55, 1,
            TimeSpan.FromMilliseconds(280)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        button.BeginAnimation(UIElement.OpacityProperty, animation);
        try { await UpdateAnnotationsAsync([item], favorite: next); }
        finally
        {
            button.BeginAnimation(UIElement.OpacityProperty, null);
            button.SetCurrentValue(UIElement.IsEnabledProperty, true);
        }
    }

    private async void FavoriteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await UpdateAnnotationsAsync([item], favorite: !item.IsFavorite);
    }

    private async void EditTagsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await ManageSelectedTagsAsync(explicitClips: item.IsSelected ? CurrentClips.Where(c => c.IsSelected).ToArray() : [item]);
    }

    private void OrganizeSelected_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OrganizeSelectedButton, Placement = PlacementMode.Bottom };
        var tag = new MenuItem { Header = "Add tag…" };
        tag.Click += async (_, _) =>
        {
            var targets = CurrentClips.Where(clip => clip.IsSelected).ToArray();
            if (targets.Length == 0) return;
            string? name = TextPrompt.Ask(Window.GetWindow(this), $"Add tag to {targets.Length} clip(s)", "", "Add tag");
            if (name != null) await UpdateAnnotationsAsync(targets, addTag: name.Trim());
        };
        var favorite = new MenuItem { Header = "Add to favorites" };
        favorite.Click += async (_, _) => await UpdateAnnotationsAsync(CurrentClips.Where(clip => clip.IsSelected).ToArray(), favorite: true);
        var unfavorite = new MenuItem { Header = "Remove from favorites" };
        unfavorite.Click += async (_, _) => await UpdateAnnotationsAsync(CurrentClips.Where(clip => clip.IsSelected).ToArray(), favorite: false);
        if (PersonalLibrary.Enabled)
        {
            var manage = new MenuItem { Header = "Manage tags…" };
            manage.Click += async (_, _) => await ManageSelectedTagsAsync();
            menu.Items.Add(manage);
            var collection = new MenuItem { Header = "Move to folder..." };
            collection.Click += async (_, _) => await AssignCollectionToAsync(CurrentClips.Where(c => c.IsSelected).ToArray());
            menu.Items.Add(collection);
            var replace = new MenuItem { Header = "Replace tag…" };
            replace.Click += async (_, _) => await ManageSelectedTagsAsync(replace: true);
            menu.Items.Add(replace);
        }
        else menu.Items.Add(tag);
        menu.Items.Add(favorite);
        menu.Items.Add(unfavorite);
        menu.IsOpen = true;
    }

    private async Task UpdateAnnotationsAsync(IReadOnlyList<ClipItem> clips, string? addTag = null, bool? favorite = null)
    {
        if (!PersonalAnnotations || _editingAnnotations || _released || !SelectionActions.IsEnabled || clips.Count == 0) return;
        if (InlineDetails.IsVisible && (GalleryDetailsEditor.HasDraft || GalleryDetailsEditor.IsSaving))
        {
            if (!await FlushAnnotationsAsync()) return;
        }
        if (addTag != null && !ClipDetails.IsValidTag(addTag))
        {
            ShowStatus($"Enter a tag of 1–{ClipDetails.MaxTagLength} characters.");
            return;
        }
        _editingAnnotations = true;
        GalleryDetailsEditor.IsEnabled = false;
        try
        {
            var targets = clips.Select(ClipAnnotationTarget.For).ToArray();
            var job = targets.Length == 1 && favorite.HasValue && addTag == null
                ? AnnotationActions.ApplyAsync(targets, addTag, favorite, CancellationToken.None)
                : WithProgressAsync("Updating annotations", targets.Length,
                _ => AnnotationActions.ApplyAsync(targets, addTag, favorite, CancellationToken.None));
            _annotationJob = job;
            var results = await job;
            if (_released) return;
            ApplyClipDetails(results);
            int failures = results.Count(result => result.Error != null);
            if (failures > 0)
                ShowStatus($"Updated {results.Count - failures} of {results.Count} clip(s). {results.First(result => result.Error != null).Error}");
            else if (clips.Count > 1) ShowStatus($"Updated {results.Count} clips.");
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Could not update gallery annotations", ex);
            if (!_released) ShowStatus("Couldn’t update clip annotations.");
        }
        finally
        {
            _annotationJob = null;
            _editingAnnotations = false;
            GalleryDetailsEditor.IsEnabled = true;
        }
    }

    private void ClipOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Border { ContextMenu: { } menu } } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource != sender || ItemOf(sender) is not { } item || e.IsRepeat) return;
        if (e.Key == Key.F2 && sender is FrameworkElement { Tag: TextBox renameBox } && PersonalAnnotations)
            BeginRename(renameBox);
        else if (e.Key == Key.Space || e.Key == Key.Enter && (HasSelection || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            item.IsSelected = !item.IsSelected;
        else if (e.Key == Key.Enter) OpenClip(item);
        else return;
        e.Handled = true;
    }

    private async void CopyClipsToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await CopyClipsAsync(ClipsFor(item));
    }

    // ---- dragging clips onto projects ----------------------------------------------------------

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCardControl(e.OriginalSource, sender)) { _dragStart = null; return; }
        _dragStart = e.GetPosition(this);
        _dragItem = ItemOf(sender);
        _dragged = false;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragItem is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(at.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        _dragged = true;
        // As files, so they can be dropped on a project here or in Explorer, Discord and so on.
        var paths = ClipsFor(item);
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy);
    }

    /// <summary>The video files being dragged, or none if it isn't video files.</summary>
    private static List<string> DraggedClips(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => File.Exists(f) && ShellIntegration.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList()
            : [];

    private void Project_DragOver(object sender, DragEventArgs e)
    {
        bool ok = DraggedClips(e).Count > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && sender is Panel row) row.SetResourceReference(Panel.BackgroundProperty, "SelectionBrush");
        e.Handled = true;
    }

    private void Project_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Panel row) row.Background = Brushes.Transparent;
    }

    private void Project_Drop(object sender, DragEventArgs e)
    {
        Project_DragLeave(sender, e);
        var paths = DraggedClips(e);
        if (paths.Count == 0 || ProjectOf(sender) is not { } project) return;
        e.Handled = true;
        // After the drop finishes: the drag holds the mouse until this handler returns.
        Dispatcher.BeginInvoke(async () => await AddClipsToAsync(paths, project.Folder, open: false));
    }

    private void NewProject_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DraggedClips(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void NewProject_Drop(object sender, DragEventArgs e)
    {
        var paths = DraggedClips(e);
        if (paths.Count == 0) return;
        e.Handled = true;
        Dispatcher.BeginInvoke(async () =>
        {
            if (CreateProject() is { } folder) await AddClipsToAsync(paths, folder, open: true);
        });
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private void OpenClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) OpenClip(item);
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && IsAvailable(item))
            ExternalPlayerDialogs.Open(Window.GetWindow(this), item.Path, AppSettings.Current.ExternalPlayerPath);
    }

    private void RevealClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    private void SelectClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.IsSelected = true;
    }

    private void OnDeletionChanged() => Dispatcher.BeginInvoke(() =>
    {
        UndoDeleteButton.IsEnabled = ClipDeletion.Current.CanUndo;
        _scanRoot = true;
        QueueReload();
    });

    private async void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _dragStart = null;
        if (ItemOf(sender) is not { } item) return;
        await DeleteClipsAsync(ClipsFor(item));
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) => await DeleteClipsAsync(SelectedPaths());

    private async Task DeleteClipsAsync(List<string> paths)
    {
        if (!SelectionActions.IsEnabled || ClipDeletion.Current.Busy) return;
        var result = await WithProgressAsync("Moving to the Recycle Bin", paths.Count, progress =>
            ClipDeletion.Current.DeleteAsync(paths,
                batch => ClipDeleteDialog.Confirm(Window.GetWindow(this), batch, force: batch.Count > 1),
                StopHoverPreview, progress));
        if (result.Cancelled) return;
        string message = result.Deleted == 0 ? "No clips were moved to the Recycle Bin."
            : $"{result.Deleted} clip(s) sent to the Recycle Bin.";
        if (result.Failed > 0) message += $" {result.Failed} could not be recycled.";
        if (result.Deleted > 0 && ClipDeletion.Current.CanUndo) message += " Use Undo delete to restore available clips.";
        ShowStatus(message);
        await ReloadAsync();
    }

    private async void UndoDelete_Click(object sender, RoutedEventArgs e)
    {
        var result = await ClipDeletion.Current.UndoAsync();
        if (result.Cancelled) return;
        ShowStatus($"Restored {result.Deleted} clip(s)." + (result.Failed > 0 ? " Some could not be restored; check the Windows Recycle Bin. Existing files were preserved." : ""));
        await ReloadAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string root = AppSettings.Current.ClipsFolder;
        string dir = _folder == ClipLibrary.AllClips ? root :
            CurrentClips.FirstOrDefault() is { } clip ? Path.GetDirectoryName(clip.Path)! : root;
        if (!Directory.Exists(dir))
        {
            ShowStatus("The selected folder is unavailable.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OpenStorage_Click(object sender, RoutedEventArgs e)
    {
        ClipsPane.Visibility = Visibility.Collapsed;
        StoragePane.Visibility = Visibility.Visible;
        StoragePane.BackButton.Focus();
    }

    private void CloseStorage()
    {
        if (StoragePane.Visibility != Visibility.Visible) return;
        StoragePane.Visibility = Visibility.Collapsed;
        ClipsPane.Visibility = _openProject == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var exts = string.Join(";", ShellIntegration.Extensions.Select(x => "*" + x));
        var dlg = new OpenFileDialog
        {
            Title = "Open a video or audio file",
            Filter = $"Media files|{exts}|All files|*.*",
            InitialDirectory = AppSettings.Current.LastFolder,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) OpenRequested?.Invoke(dlg.FileName);
    }

    // ---- selection -----------------------------------------------------------------------------

    private void OnClipSelectionChanged(ClipItem clip)
    {
        if (clip.IsSelected) _selected.Add(clip.Path);
        else _selected.Remove(clip.Path);
        if (!_updatingSelection) UpdateSelection();
    }

    private void UpdateSelection()
    {
        foreach (var day in Rows.Items.OfType<GalleryDayHeader>()) day.RefreshSelection();
        var selected = CurrentClips.Where(clip => clip.IsSelected).ToArray();
        HasSelection = selected.Length > 0;
        DeleteSelectedButton.IsEnabled = OrganizeSelectedButton.IsEnabled = AddToProjectButton.IsEnabled =
            CopySelectedButton.IsEnabled = HasSelection;
        if (!SelectionActions.IsEnabled) return; // showing progress instead
        SelectionBar.Visibility = _selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        int hidden = _selected.Count - selected.Length;
        SelectionText.Text = hidden > 0
            ? $"{_selected.Count:N0} selected · {selected.Length:N0} visible (actions apply to visible clips)"
            : $"{selected.Length:N0} selected";
        if (selected.Length > 0) SelectionText.Text += $" · {Fmt.Size(selected.Sum(clip => clip.Size))}";
    }

    /// <summary>Selected clips, oldest first (the order they happened in, which is how a montage usually starts).</summary>
    private List<string> SelectedPaths() =>
        GalleryDayFeed.SelectedPaths(CurrentClips);

    private void SelectDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryDayHeader day })
            ChangeSelection(day.Clips, day.Selection != true);
    }

    private void ClearSelection()
    {
        ChangeSelection(_clips.Where(c => c.IsSelected), false);
    }

    private void ClearProcessedSelection(IEnumerable<string> paths)
    {
        var processed = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ChangeSelection(_clips.Where(clip => clip.IsSelected && processed.Contains(clip.Path)), false);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        ChangeSelection(CurrentClips, true);
    }

    private void ChangeSelection(IEnumerable<ClipItem> clips, bool selected)
    {
        _updatingSelection = true;
        foreach (var clip in clips) clip.IsSelected = selected;
        _updatingSelection = false;
        UpdateSelection();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddToProjectButton, Placement = PlacementMode.Bottom };
        foreach (var project in _projects)
        {
            // A TextBlock header: an underscore in a name isn't an access key.
            var item = new MenuItem { Header = new TextBlock { Text = $"{project.Name}  ({project.ClipCount})" } };
            item.Click += async (_, _) => await AddSelectionToAsync(project.Folder, open: false);
            menu.Items.Add(item);
        }
        if (_projects.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "New project…" };
        create.Click += async (_, _) =>
        {
            if (CreateProject() is { } folder) await AddSelectionToAsync(folder, open: true);
        };
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private Task AddSelectionToAsync(string folder, bool open) => AddClipsToAsync(SelectedPaths(), folder, open);

    private async Task AddClipsToAsync(List<string> paths, string folder, bool open)
    {
        if (paths.Count == 0) return;
        try
        {
            var project = ProjectLibrary.Load(folder);
            int added = await WithProgressAsync($"Adding to {project.Name}", paths.Count,
                progress => ProjectLibrary.AddClipsAsync(project, paths, progress));
            ClearProcessedSelection(paths);
            ShowStatus(added == paths.Count
                ? $"Added {added} clip{(added == 1 ? "" : "s")} to {project.Name}."
                : $"Added {added} clip{(added == 1 ? "" : "s")} to {project.Name}; {paths.Count - added} {(paths.Count - added == 1 ? "was" : "were")} already in it.");
            await ReloadAsync();
            if (open) OpenProject(project.Folder);
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Adding clips to a project failed", ex);
            ModalDialog.Show(Window.GetWindow(this), "Couldn't add the clips to the project:\n\n" + ex.Message, "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CopyToFolder_Click(object sender, RoutedEventArgs e) => await CopyClipsAsync(SelectedPaths());

    private async Task CopyClipsAsync(List<string> paths)
    {
        if (paths.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = $"Copy {paths.Count} clip{(paths.Count == 1 ? "" : "s")} to" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await WithProgressAsync("Copying", paths.Count, async progress =>
            {
                await ProjectLibrary.CopyToFolderAsync(paths, dialog.FolderName, progress);
                return paths.Count;
            });
            ClearProcessedSelection(paths);
            ShowStatus($"Copied {paths.Count} clip{(paths.Count == 1 ? "" : "s")} to {dialog.FolderName}.");
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Copying clips failed", ex);
            ModalDialog.Show(Window.GetWindow(this), "Couldn't copy the clips:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Shows "Copying… 2 of 5" in the selection bar (its buttons disabled) while <paramref name="work"/> runs.</summary>
    private async Task<T> WithProgressAsync<T>(string what, int count, Func<IProgress<int>, Task<T>> work)
    {
        using var activity = QuickClip.Shell.BackgroundActivity.Begin(what);
        SelectionBar.Visibility = Visibility.Visible; // also for a single clip from its menu or a drag
        SelectionActions.IsEnabled = false;
        SelectionText.Text = $"{what}…";
        try
        {
            return await work(new Progress<int>(done =>
            {
                activity.Report($"{what} · {done}/{count}", count > 0 ? (double)done / count : null);
                if (!SelectionActions.IsEnabled) SelectionText.Text = $"{what}… {done} of {count}";
            }));
        }
        finally
        {
            SelectionActions.IsEnabled = true;
            UpdateSelection();
        }
    }

    // ---- projects ------------------------------------------------------------------------------

    private void RebuildProjects()
    {
        if (_openProject != null && !Directory.Exists(_openProject)) CloseProject();
        _settingLists = true;
        ProjectList.ItemsSource = _projects;
        ProjectList.SelectedItem = _projects.FirstOrDefault(p => string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase));
        _settingLists = false;
        NoProjectsText.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLists || ProjectList.SelectedItem is not ProjectSummary p) return;
        if (!string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase)) OpenProject(p.Folder);
    }

    private async void OpenProject(string folder)
    {
        CloseStorage();
        _openProject = folder;
        _settingLists = true;
        FolderList.SelectedItem = null;
        ProjectList.SelectedItem = _projects.FirstOrDefault(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase));
        _settingLists = false;
        ClipsPane.Visibility = Visibility.Collapsed;
        ProjectPane.Visibility = Visibility.Visible;
        try
        {
            await ProjectPane.ShowAsync(ProjectLibrary.Load(folder));
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Opening a project failed", ex);
        }
    }

    private void CloseProject()
    {
        _openProject = null;
        ProjectPane.Close();
        ProjectPane.Visibility = Visibility.Collapsed;
        ClipsPane.Visibility = Visibility.Visible;
        _settingLists = true;
        ProjectList.SelectedItem = null;
        FolderList.SelectedItem = (FolderList.ItemsSource as List<Folder>)?.FirstOrDefault(f => f.Selection == _folderSelection);
        _settingLists = false;
    }

    /// <summary>Asks for a name and makes the project; returns its folder, or null if cancelled.</summary>
    private string? CreateProject()
    {
        var name = TextPrompt.Ask(Window.GetWindow(this), "New project", "Montage", "Create");
        if (name == null) return null;
        try
        {
            return ProjectLibrary.Create(AppSettings.Current.ClipsFolder, name).Folder;
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Creating a project failed", ex);
            ModalDialog.Show(Window.GetWindow(this), "Couldn't create the project:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (CreateProject() is not { } folder) return;
        await ReloadAsync();
        OpenProject(folder);
    }

    private static ProjectSummary? ProjectOf(object sender) => (sender as FrameworkElement)?.DataContext as ProjectSummary;

    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOf(sender) is not { } p) return;
        var name = TextPrompt.Ask(Window.GetWindow(this), "Rename project", p.Name, "Rename");
        if (name == null) return;
        bool wasOpen = string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase);
        // The preview has the project's files open; let go of them first.
        if (wasOpen) ProjectPane.Unload();
        Project? renamed = null;
        for (int attempt = 0; attempt < 20 && renamed == null; attempt++)
        {
            try
            {
                renamed = ProjectLibrary.Rename(ProjectLibrary.Load(p.Folder), name);
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                ModalDialog.Show(Window.GetWindow(this), "Couldn't rename the project:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
        }
        if (wasOpen) _openProject = renamed?.Folder ?? p.Folder;
        await ReloadAsync();
        if (wasOpen) OpenProject(_openProject!);
    }

    private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOf(sender) is { } p) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.Folder}\"") { UseShellExecute = true });
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOf(sender) is not { } p) return;
        var answer = ModalDialog.Show(Window.GetWindow(this),
            $"Move the project \"{p.Name}\" to the Recycle Bin?\n\nThe clips stay in the gallery; its montages, music and order go with it.",
            "QuickClip", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase)) CloseProject();
        bool deleted = false;
        for (int attempt = 0; attempt < 20 && !(deleted = ProjectLibrary.Delete(ProjectLibrary.Load(p.Folder))); attempt++)
            await Task.Delay(100);
        if (!deleted)
            ModalDialog.Show(Window.GetWindow(this), $"Couldn't move \"{p.Name}\" to the Recycle Bin.", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        await ReloadAsync();
    }

#if DEBUG
    internal async Task<string> VerifySearchForRenderAsync()
    {
        if (CurrentClips.Count < 2) return "Gallery search smoke skipped: fewer than two clips.";
        string dayReport = "";
        var days = Rows.Items.OfType<GalleryDayHeader>().Take(2).ToArray();
        if (_selected.Count == 0 && days.Length == 2 && days[0].Clips.Count > 1)
        {
            ChangeSelection([days[0].Clips[0]], true);
            if (days[0].Selection != null) throw new InvalidOperationException("Partially selected day was not mixed.");
            var firstHeader = new CheckBox { DataContext = days[0] };
            var secondHeader = new CheckBox { DataContext = days[1] };
            SelectDay_Click(firstHeader, new RoutedEventArgs());
            SelectDay_Click(secondHeader, new RoutedEventArgs());
            if (SelectedPaths().Count != days.Sum(day => day.Clips.Count))
                throw new InvalidOperationException("Day selection did not combine both days.");
            SelectDay_Click(firstHeader, new RoutedEventArgs());
            if (SelectedPaths().Count != days[1].Clips.Count)
                throw new InvalidOperationException("Deselecting a day changed another day's selection.");
            ClearSelection();
            dayReport = " Day selection smoke passed for mixed and multiple days.";
        }
        var first = CurrentClips[0];
        int count = CurrentClips.Count;
        string saved = ClipSearchBox.Text;
        bool selected = first.IsSelected;
        try
        {
            first.IsSelected = true;
            ClipSearchBox.Text = first.File.Name;
            _filterTimer.Stop();
            await SearchAsync();
            if (!CurrentClips.Contains(first) || CurrentClips.Count >= count || !first.IsSelected)
                throw new InvalidOperationException("Gallery search lost the matching clip or its selection.");
            return $"Gallery search: {CurrentClips.Count} matches from {count} clips, visible selection preserved.{dayReport}";
        }
        finally
        {
            first.IsSelected = selected;
            ClipSearchBox.Text = saved;
            _filterTimer.Stop();
            await SearchAsync();
        }
    }

    internal ClipItem? DraftCardForRender(string path) => _clips.FirstOrDefault(clip => clip.Path == path);

    internal async Task VerifyHoverOriginalForRenderAsync(ClipItem item)
    {
        var poster = FindPoster(Rows) ?? throw new InvalidOperationException("The draft card poster was not realized.");
        await _hoverPreview.StartForRenderAsync(poster, item);
        bool played = false;
        try
        {
            for (int i = 0; i < 120 && _hoverPreview.IsActive && !played; i++)
            {
                if (_hoverPreview.VisiblePositionForRender is { } position)
                {
                    if (position > 5.5)
                        throw new InvalidOperationException($"Hover played beyond its preview interval at {position:0.00} s.");
                    played |= position > 0.1;
                }
                await Task.Delay(40);
            }
            if (!played)
                throw new InvalidOperationException("Original hover playback failed for a trimmed card.");
        }
        finally { _hoverPreview.Stop(); }

        Grid? FindPoster(DependencyObject parent)
        {
            if (parent is Grid { Name: "ClipPoster" } grid && ReferenceEquals(grid.DataContext, item)) return grid;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindPoster(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
            return null;
        }
    }

    internal bool OpenFirstForRender()
    {
        if (CurrentClips.FirstOrDefault() is not { } first) return false;
        OpenRequested?.Invoke(first.Path);
        return true;
    }

    internal string RealizedRowsForRender => $"{Enumerable.Range(0, Rows.Items.Count).Count(i => Rows.ItemContainerGenerator.ContainerFromIndex(i) != null)}/{Rows.Items.Count}";

    internal bool PlayFirstForRender()
    {
        if (CurrentClips.FirstOrDefault() is not { } first) return false;
        Play(first);
        return true;
    }

    internal List<string> SelectForRender(int count)
    {
        foreach (var clip in CurrentClips.Take(count)) clip.IsSelected = true;
        return SelectedPaths();
    }

    internal Task AddSelectionForRender(string folder) => AddSelectionToAsync(folder, open: true);

    internal ProjectView ProjectForRender => ProjectPane;
#endif
}

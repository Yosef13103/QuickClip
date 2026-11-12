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

    private sealed record Folder(string Name, int Count, DateTime Latest);

    private const double CardGap = 14; // must match the card's right margin
    private const double PreferredCardWidth = 440;

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
    private bool _loading, _reloadQueued, _released, _settingLists;
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

    /// <summary>The user wants to watch a clip; the folder's other clips come along for previous/next.</summary>
    public event Action<IReadOnlyList<ClipItem>, int>? PlayRequested;

    /// <summary>Some clips are ticked (each card then shows its checkbox, and clicking a card ticks it).</summary>
    public bool HasSelection
    {
        get => (bool)GetValue(HasSelectionProperty);
        private set => SetValue(HasSelectionProperty, value);
    }

    public GalleryView()
    {
        InitializeComponent();
        GalleryDetailsEditor.KnownTags = () => KnownTags;
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
            var settings = AppSettings.Current;
            var sources = SavedRecordingFolders.Additional(settings);
            if (_loadedRoot != settings.ClipsFolder ||
                !_loadedSources.SequenceEqual(sources, SavedRecordingFolders.Paths) ||
                !_availableSources.SequenceEqual(AvailableFolders(settings.ClipsFolder, sources), SavedRecordingFolders.Paths) ||
                _loadedIndexed != PersonalLibrary.Enabled ||
                !_loadedExclusions.SequenceEqual(settings.LibraryExclusions ?? [])) _ = ReloadAsync();
        };
        Loaded += (_, _) => _ = ReloadAsync();
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

    private void OnClipSaved(SavedClip clip) => Dispatcher.BeginInvoke(() => QueueScopedReload(clip.Path));

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

    private async Task ReloadAsync()
    {
        if (_released) return;
        if (_loading)
        {
            _reloadQueued = true;
            return;
        }
        _loading = true;
        try
        {
            string root = AppSettings.Current.ClipsFolder;
            string[] sources = SavedRecordingFolders.Additional(AppSettings.Current);
            string[] available = AvailableFolders(root, sources);
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
            _projects = await Task.Run(() => available.SelectMany(LoadProjects).ToList(), _lifetime.Token);
            if (indexed)
            {
                if (scopes == null || scopes.Length > 0 || _clips.Count == 0)
                {
                    var loaded = new List<ClipItem>();
                    bool incomplete = false;
                    foreach (string folder in (scopes == null ? sources : []).Prepend(root))
                    {
                        try
                        {
                            var result = await PersonalLibrary.Current.LoadAsync(folder, exclusions,
                                SavedRecordingFolders.Paths.Equals(folder, root) ? scopes : null, _lifetime.Token);
                            loaded.AddRange(result.Clips);
                            incomplete |= !result.Scan.Complete;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            incomplete = true;
                            Log.Write("Could not refresh an indexed clips folder: " + ex.GetType().Name);
                        }
                    }
                    if (scopes != null)
                        loaded.AddRange(_clips.Where(clip => sources.Any(folder => LibraryFiles.Within(clip.Path, folder))));
                    _clips = loaded.OrderByDescending(clip => clip.Details.CapturedAt).ToList();
                    if (incomplete) ShowStatus("One or more Gallery sources are unavailable or could not be fully scanned.");
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
            foreach (var clip in _clips)
            {
                // Keep thumbnails already on screen so a new clip doesn't make every card flicker.
                if (previous.TryGetValue(clip.Path, out var old) && old.Size == clip.Size && old.Thumbnail != null &&
                    old.File.LastWriteTimeUtc == clip.File.LastWriteTimeUtc && old.File.CreationTimeUtc == clip.File.CreationTimeUtc)
                {
                    clip.Thumbnail = old.Thumbnail;
                    clip.ThumbnailWidth = old.ThumbnailWidth;
                }
                clip.IsSelected = _selected.Contains(clip.Path);
                clip.SelectionChanged += OnClipSelectionChanged;
            }
            _selected.IntersectWith(_clips.Select(c => c.Path));
            RefreshKnownTags();
            RebuildFolders();
            RebuildProjects();
            UpdateSelection();
        }
        catch (Exception ex)
        {
            if (!_released)
            {
                _scanRoot = true;
                ShowStatus("Couldn’t refresh the clips folder. Check its availability and library settings.");
                Log.Write("Loading clips failed: " + ex.GetType().Name);
            }
        }
        finally
        {
            _loading = false;
        }
        if (_reloadQueued)
        {
            _reloadQueued = false;
            await ReloadAsync();
        }
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
            Log.Write("Can't watch a clips folder: " + ex.GetType().Name);
        }
    }

    // ---- folders ------------------------------------------------------------------------------

    private void RebuildFolders(bool preserveScroll = false)
    {
        var folders = new List<Folder> { new(ClipLibrary.AllClips, _clips.Count, _clips.FirstOrDefault()?.Created ?? DateTime.MinValue) };
        folders.AddRange(_clips.GroupBy(c => c.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Folder(g.First().Folder, g.Count(), g.Max(c => c.Created)))
            .OrderByDescending(f => f.Latest));
        if (folders.All(f => !string.Equals(f.Name, _folder, StringComparison.OrdinalIgnoreCase))) _folder = ClipLibrary.AllClips;
        _settingLists = true;
        FolderList.ItemsSource = folders;
        FolderList.SelectedItem = _openProject != null ? null : folders.First(f => string.Equals(f.Name, _folder, StringComparison.OrdinalIgnoreCase));
        _settingLists = false;
        ShowFolder(preserveScroll);
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLists || FolderList.SelectedItem is not Folder f) return;
        CloseStorage();
        if (_openProject != null) CloseProject();
        if (f.Name == _folder) return;
        _folder = f.Name;
        ShowFolder();
    }

    private IReadOnlyList<ClipItem> CurrentClips => _visibleClips;

    private void OrganizationFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_filterTimer == null || _released) return;
        _searchCancellation?.Cancel();
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async Task SearchAsync()
    {
        var filter = new GalleryClipFilter(ClipSearchBox.Text.Trim(), TagSearchBox.Text.Trim(),
            FavoritesFilter.IsChecked == true, FavoritesSort.IsChecked == true);
        string folder = _folder;
        var clips = _clips.ToArray();
        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try
        {
            var matches = await Task.Run(() => filter.Apply(clips, folder, cancellation.Token), cancellation.Token);
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
        KnownTags = ClipTagChoices.Match(_clips.SelectMany(clip => clip.Details.Tags), "", []).ToArray();
        ExistingTagsCombo.ItemsSource = new[] { "Tags…" }.Concat(KnownTags);
        ExistingTagsCombo.SelectedIndex = 0;
        ExistingTagsCombo.IsEnabled = KnownTags.Count > 0;
    }

    private void ExistingTags_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ExistingTagsCombo.SelectedIndex <= 0 || ExistingTagsCombo.SelectedItem is not string tag) return;
        TagSearchBox.Text = tag;
        ExistingTagsCombo.SelectedIndex = 0;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ClipSearchBox.Clear();
        TagSearchBox.Clear();
        FavoritesFilter.IsChecked = FavoritesSort.IsChecked = false;
    }

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
            regroup |= clip.Folder != details.Game;
            clip.ApplyDetails(details);
        }
        if (saved.Count == 0) return;
        RefreshKnownTags();
        if (regroup) RebuildFolders(preserveScroll: true);
        else ShowFolder(preserveScroll: true);
    }

    private void ShowFolder(bool preserveScroll = false)
    {
        if (_searchCancellation is { IsCancellationRequested: false })
            OrganizationFilter_Changed(this, new RoutedEventArgs());
        ApplyRows(_filter.Apply(_clips, _folder), preserveScroll);
    }

    private void ApplyRows(IReadOnlyList<ClipItem> clips, bool preserveScroll)
    {
        if (HasSelection)
        {
            var visible = clips.Select(clip => clip.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ChangeSelection(_clips.Where(clip => clip.IsSelected && !visible.Contains(clip.Path)), false);
        }
        FolderTitle.Text = _folder;
        FolderInfo.Text = clips.Count == 0 ? "" : $"{clips.Count} clip{(clips.Count == 1 ? "" : "s")}  ·  {Fmt.Size(clips.Sum(c => c.Size))}";
        if (_renderedColumns != _columns || !_visibleClips.SequenceEqual(clips))
        {
            var scroll = Rows.Template?.FindName("GalleryScroll", Rows) as ScrollViewer;
            double offset = scroll?.VerticalOffset ?? 0;
            _renderedColumns = _columns;
            _visibleClips = clips;
            Rows.ItemsSource = clips.Chunk(_columns).ToList();
            if (preserveScroll && offset > 0) scroll?.ScrollToVerticalOffset(offset);
        }

        EmptyState.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _filter.IsFiltered ? "No matching clips" : "No clips yet";
        _agoTimer.Start();
        if (_filter.IsFiltered)
        {
            EmptyText.Text = "Change your search or clear the filters to see more clips.";
            return;
        }
        var s = AppSettings.Current;
        EmptyText.Text = $"Press {new Hotkey(s.HotkeyModifiers, s.HotkeyKey)} while you play to save the last " +
                         $"{RecordingPresets.DurationLabel(s.BufferSeconds)}. Clips are filed by game in {s.ClipsFolder}.";
    }

    internal static int ColumnsForWidth(double available) =>
        Math.Clamp((int)(available / (PreferredCardWidth + CardGap)), 1, 4);

    private void ClipArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || e.NewSize.Width <= 0) return;
        // Reserve scrollbar space even when it is hidden so filtering does not change the columns.
        double width = Math.Max(1, e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
        int columns = ColumnsForWidth(width);
        double cardWidth = Math.Max(1, Math.Floor(width / columns) - CardGap);
        SetValue(CardWidthProperty, cardWidth);
        SetValue(PosterHeightProperty, Math.Max(1, cardWidth - 2) * 9 / 16);
        if (columns == _columns) return;
        _columns = columns;
        if (_clips.Count > 0) ShowFolder();
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
        if (item.ThumbnailWidth >= width) return;
        item.ThumbnailWidth = width;
        try
        {
            string? thumb = await Task.Run(() => ThumbnailCache.GetAsync(item.File, item.Duration, width));
            if (thumb != null)
            {
                var image = await Task.Run(() => ThumbnailCache.LoadImage(thumb, width));
                if (!_released && item.ThumbnailWidth == width) item.Thumbnail = image;
            }
            else if (item.ThumbnailWidth == width) item.ThumbnailWidth = 0;
        }
        catch (Exception ex)
        {
            if (item.ThumbnailWidth == width) item.ThumbnailWidth = 0;
            Log.Write("Loading thumbnail failed: " + ex.GetType().Name);
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (_dragged || ItemOf(sender) is not { } item) return;
        // While clips are being picked, a click picks; Ctrl+click always does.
        if (HasSelection || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) item.IsSelected = !item.IsSelected;
        else OpenRequested?.Invoke(item.Path);
    }

    private void Play(ClipItem? item)
    {
        if (item == null) return;
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

    private void ClipMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not ClipItem item) return;
        int count = ClipsFor(item).Count;
        string clips = count == 1 ? "" : $" {count} selected clips";
        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            if (entry.Tag is "details" or "favorite") entry.Visibility = PersonalAnnotations ? Visibility.Visible : Visibility.Collapsed;
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

    private void EditClipDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!AppProfile.IsPersonal || ItemOf(sender) is not { } item) return;
        if (InlineDetails.IsVisible && (GalleryDetailsEditor.HasDraft || GalleryDetailsEditor.IsSaving))
        {
            ShowStatus("Save or cancel the current clip details first.");
            return;
        }
        InlineDetails.Visibility = Visibility.Visible;
        GalleryDetailsEditor.Open(item.Path, item.Details, focus: true, duration: item.Duration);
    }

    private async void FavoriteCard_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        bool next = !item.IsFavorite;
        await UpdateAnnotationsAsync([item], favorite: next);
    }

    private async void FavoriteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await UpdateAnnotationsAsync([item], favorite: !item.IsFavorite);
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
        menu.Items.Add(tag);
        menu.Items.Add(favorite);
        menu.Items.Add(unfavorite);
        menu.IsOpen = true;
    }

    private async Task UpdateAnnotationsAsync(IReadOnlyList<ClipItem> clips, string? addTag = null, bool? favorite = null)
    {
        if (!PersonalAnnotations || _editingAnnotations || _released || !SelectionActions.IsEnabled || clips.Count == 0) return;
        if (InlineDetails.IsVisible && (GalleryDetailsEditor.HasDraft || GalleryDetailsEditor.IsSaving))
        {
            ShowStatus("Save or cancel the open details before organizing clips.");
            return;
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
            var job = WithProgressAsync("Updating annotations", targets.Length,
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
            Log.Write("Could not update gallery annotations: " + ex.Message);
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
        if (e.Key == Key.Enter) OpenRequested?.Invoke(item.Path);
        else if (e.Key == Key.Space) item.IsSelected = !item.IsSelected;
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
        for (var node = e.OriginalSource as DependencyObject; node != null && node != sender; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) { _dragStart = null; return; }
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

    private static readonly Brush DropHighlight = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x60, 0xCD, 0xFF)));

    private void Project_DragOver(object sender, DragEventArgs e)
    {
        bool ok = DraggedClips(e).Count > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && sender is Panel row) row.Background = DropHighlight;
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
        if (ItemOf(sender) is { } item) OpenRequested?.Invoke(item.Path);
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
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
        var result = await ClipDeletion.Current.DeleteAsync(paths, batch => ClipDeleteDialog.Confirm(Window.GetWindow(this), batch));
        if (result.Cancelled) return;
        foreach (var clip in _clips.Where(c => paths.Contains(c.Path, StringComparer.OrdinalIgnoreCase))) clip.IsSelected = false;
        ShowStatus($"{result.Deleted} clip(s) sent to the Recycle Bin." + (result.Failed > 0 ? $" {result.Failed} could not be recycled and were left in place." :
            ClipDeletion.Current.CanUndo ? " Use Undo delete to restore." : " Restore them through the Windows Recycle Bin."));
        await ReloadAsync();
    }

    private async void UndoDelete_Click(object sender, RoutedEventArgs e)
    {
        var result = ClipDeletion.Current.Undo();
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
        HasSelection = _selected.Count > 0;
        SelectionBar.Visibility = HasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!SelectionActions.IsEnabled) return; // showing progress instead
        long size = _clips.Where(c => c.IsSelected).Sum(c => c.Size);
        SelectionText.Text = $"{_selected.Count} clip{(_selected.Count == 1 ? "" : "s")} selected  ·  {Fmt.Size(size)}";
    }

    /// <summary>Selected clips, oldest first (the order they happened in, which is how a montage usually starts).</summary>
    private List<string> SelectedPaths() =>
        _clips.Where(c => c.IsSelected).OrderBy(c => c.Created).Select(c => c.Path).ToList();

    private void ClearSelection()
    {
        ChangeSelection(_clips.Where(c => c.IsSelected), false);
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
            if (paths.Any(_selected.Contains)) ClearSelection();
            ShowStatus(added == paths.Count
                ? $"Added {added} clip{(added == 1 ? "" : "s")} to {project.Name}."
                : $"Added {added} clip{(added == 1 ? "" : "s")} to {project.Name}; {paths.Count - added} {(paths.Count - added == 1 ? "was" : "were")} already in it.");
            await ReloadAsync();
            if (open) OpenProject(project.Folder);
        }
        catch (Exception ex)
        {
            Log.Write("Adding clips to a project failed: " + ex);
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
            if (paths.Any(_selected.Contains)) ClearSelection();
            ShowStatus($"Copied {paths.Count} clip{(paths.Count == 1 ? "" : "s")} to {dialog.FolderName}.");
        }
        catch (Exception ex)
        {
            Log.Write("Copying clips failed: " + ex);
            ModalDialog.Show(Window.GetWindow(this), "Couldn't copy the clips:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Shows "Copying… 2 of 5" in the selection bar (its buttons disabled) while <paramref name="work"/> runs.</summary>
    private async Task<T> WithProgressAsync<T>(string what, int count, Func<IProgress<int>, Task<T>> work)
    {
        SelectionBar.Visibility = Visibility.Visible; // also for a single clip from its menu or a drag
        SelectionActions.IsEnabled = false;
        SelectionText.Text = $"{what}…";
        try
        {
            return await work(new Progress<int>(done => SelectionText.Text = $"{what}… {done} of {count}"));
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
            Log.Write("Opening a project failed: " + ex);
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
        FolderList.SelectedItem = (FolderList.ItemsSource as List<Folder>)?.FirstOrDefault(f => f.Name == _folder);
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
            Log.Write("Creating a project failed: " + ex);
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
            return $"Gallery search: {CurrentClips.Count} matches from {count} clips, visible selection preserved.";
        }
        finally
        {
            first.IsSelected = selected;
            ClipSearchBox.Text = saved;
            _filterTimer.Stop();
            await SearchAsync();
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

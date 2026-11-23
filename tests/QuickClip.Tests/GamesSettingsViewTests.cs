using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuickClip.Discovery;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class GamesSettingsViewTests
{
    [Fact]
    public void Discord_app_card_describes_the_selected_capture_mode()
    {
        Assert.Equal("During voice and video calls", GamesSettingsView.DiscordCaptureDescription(
            QuickClip.Recording.Companion.DiscordCaptureMode.DuringCalls));
        Assert.Equal("While Discord is open", GamesSettingsView.DiscordCaptureDescription(
            QuickClip.Recording.Companion.DiscordCaptureMode.WhileOpen));
        Assert.Equal("Only when I start it", GamesSettingsView.DiscordCaptureDescription(
            QuickClip.Recording.Companion.DiscordCaptureMode.Manual));
        Assert.Equal("Capture turned off", GamesSettingsView.DiscordCaptureDescription(
            QuickClip.Recording.Companion.DiscordCaptureMode.Disabled));
    }

    [Fact]
    public Task Store_logos_are_local_nonempty_vectors_for_every_supported_tile() => OnSta(() =>
    {
        var page = new GamesSettingsView();
        string[] names = ["Steam", "Epic", "Gog", "Xbox", "Ea", "Riot", "BattleNet", "Ubisoft", "Rockstar", "Heroic", "Origin", "Amazon", "Itch"];
        foreach (string name in names)
        {
            var geometry = Assert.IsAssignableFrom<Geometry>(page.FindResource("StoreLogo" + name));
            Assert.False(geometry.Bounds.IsEmpty);
            Assert.True(geometry.Bounds.Width > 0 && geometry.Bounds.Height > 0);
        }
    });

    [Fact]
    public Task Store_tile_template_distinguishes_checked_state_without_native_button_chrome() => OnSta(() =>
    {
        var page = new GamesSettingsView();
        var tile = new System.Windows.Controls.Primitives.ToggleButton
        {
            Style = (Style)page.FindResource("StoreTile"),
            DataContext = new { LogoKey = "Steam", TileName = "Steam" },
            IsChecked = false,
            Width = 100,
            Height = 96,
        };
        Arrange(tile, 100, 96);
        tile.ApplyTemplate();
        var logo = Assert.IsType<System.Windows.Shapes.Path>(tile.Template.FindName("Logo", tile));
        var label = Assert.IsType<TextBlock>(tile.Template.FindName("StateLabel", tile));
        Assert.NotNull(logo.Data);
        Assert.Equal("Disabled", label.Text);
        Assert.True(logo.Opacity < 1);
        tile.IsChecked = true;
        Assert.Equal("Enabled", label.Text);
        Assert.Equal(1d, logo.Opacity);
        tile.IsChecked = false;
        Assert.Equal("Disabled", label.Text);
    });

    [Fact]
    public void Game_files_show_ready_and_blocked_using_the_runtime_match()
    {
        const string renderer = @"C:\Collection\osu!lazer\osu!.exe";
        const string helper = @"C:\Collection\osu!lazer\chrome.exe";
        var entry = new CatalogEntry("folder:osu", "osu!lazer", CatalogSource.Folder, [@"C:\Collection\osu!lazer"],
            [renderer, helper], new()) { RendererExecutables = [] };
        var row = new GamesSettingsView.GameRow(entry);
        Assert.Contains("Not ready to record", row.Executables.First().Label);
        var approved = entry with { Rules = new() { ApprovedExecutables = [renderer] } };
        row.Update(approved, false, [], new(new([approved], [], 0, false, default)));
        Assert.Contains("Ready to record", row.Summary);
        Assert.Contains("Ready to record", row.Executables.First().Label);
        Assert.Contains("Blocked", row.Executables.Last().Label);
        row.Update(approved, true, [], new(new([approved], [], 0, false, default)));
        Assert.Contains("scan incomplete", row.Executables.First().Label);
    }

    [Fact]
    public void Explicit_blocks_have_a_reversible_action_but_inferred_non_game_roles_do_not()
    {
        const string game = @"C:\Collection\Demo\Demo.exe";
        const string launcher = @"C:\Collection\Demo\Launcher.exe";
        var entry = new CatalogEntry("folder:demo", "Demo", CatalogSource.Folder,
            [@"C:\Collection\Demo"], [game, launcher], new()) { RendererExecutables = [game] };
        var row = new GamesSettingsView.GameRow(entry);
        row.Update(entry, false, [game], new(new([entry], [], 0, false, default), [game]));
        Assert.True(row.Executables.Single(item => item.Path == game).ExplicitlyBlocked);
        Assert.Equal("Unblock selected executable", row.Executables.Single(item => item.Path == game).BlockAction);
        Assert.False(row.Executables.Single(item => item.Path == launcher).ExplicitlyBlocked);
        row.Update(entry, false, [], new(new([entry], [], 0, false, default)));
        Assert.Equal("Block selected executable", row.Executables.Single(item => item.Path == game).BlockAction);
        Assert.True(row.DetectionReady);
    }

    [Fact]
    public void Executable_labels_distinguish_explicit_block_from_retained_denial()
    {
        const string renderer = @"C:\Collection\Demo\Demo.exe";
        var entry = new CatalogEntry("folder:demo", "Demo", CatalogSource.Folder, [@"C:\Collection\Demo"],
            [renderer], new()) { RendererExecutables = [renderer] };
        var row = new GamesSettingsView.GameRow(entry);
        var index = new GameCatalogIndex(new([entry], [], 0, false, default), [renderer]);
        row.Update(entry, false, [], index);
        var inherited = Assert.Single(row.Executables);
        Assert.Contains("retained denial", inherited.Label);
        Assert.Contains("retained denial", row.Summary);
        Assert.False(inherited.ExplicitlyBlocked);
        row.Update(entry, false, [renderer], index);
        Assert.Contains("explicit executable block", Assert.Single(row.Executables).Label);
    }

    [Theory]
    [InlineData(1050, 660, 1.0)]
    [InlineData(780, 480, 1.25)]
    public Task Games_page_loads_at_nonmaximized_sizes(int width, int height, double scale) => OnSta(() =>
    {
        var page = new GamesSettingsView { LayoutTransform = new ScaleTransform(scale, scale) };
        // Sticky search is collapsed until its natural catalog location is in view.
        // Realize it in a real WPF window instead of measuring a detached control.
        var window = new Window
        {
            Title = "QuickClip Sources Small Window Test",
            Content = page, Width = width, Height = height, ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            Assert.True(page.Games.ActualHeight > 0);
            var anchor = Assert.IsType<ListBoxItem>(page.FindName("SearchAnchorItem"));
            page.Games.ScrollIntoView(anchor);
            page.Search.Text = "Test";
            window.UpdateLayout();
            Assert.InRange(page.Search.ActualWidth,
                Math.Min(page.SearchDock.ActualWidth, 1340) - 3,
                Math.Min(page.SearchDock.ActualWidth, 1340) + 3);
            Assert.DoesNotContain(page.Search, Descendants<TextBox>(page.Games));
            Assert.True(page.AutoScan.IsChecked == true);
            Assert.Single(page.Applications.Items);
            page.Search.Clear();
            page.RenderRows(Fixture(200));
            window.UpdateLayout();
            var scroll = Descendants<ScrollViewer>(page.Games).First();
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.Null(page.Games.ItemContainerGenerator.ContainerFromIndex(200));
            page.Games.ScrollIntoView(page.Games.Items[200]);
            window.UpdateLayout();
            Assert.NotNull(page.Games.ItemContainerGenerator.ContainerFromIndex(200));
            // A loaded page may auto-start discovery, enabling Pause scan.
            // Scanner state is independent of the small-window layout contract.
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Game_search_keeps_keyboard_focus_during_each_filtered_keystroke() => OnSta(() =>
    {
        var page = new GamesSettingsView();
        page.RenderRows(Fixture(120));
        var window = new Window
        {
            Title = "QuickClip Search Focus Test",
            Content = page, Width = 900, Height = 680, ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            Assert.True(page.Search.Focus());
            Assert.True(page.Search.IsKeyboardFocused);
            foreach (char letter in "Test game")
            {
                page.Search.AppendText(letter.ToString());
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Assert.True(page.Search.IsKeyboardFocused);
                Assert.Single(Descendants<TextBox>(window), input => ReferenceEquals(input, page.Search));
            }
            Assert.Equal("Test game", page.Search.Text);
            // A real Loaded view may asynchronously replace synthetic catalog data
            // with the coordinator's current snapshot; focus and text must survive.
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true, 1470)]
    [InlineData(false, 1470)]
    [InlineData(false, 2400)]
    public Task Sources_search_is_visible_in_real_settings_with_68_games(bool basic, int windowWidth) => OnSta(() =>
    {
        if (!AppProfile.IsPersonal) return;
        var settings = AppSettings.Current;
        bool previous = settings.BasicSettings;
        int? priorStep = settings.GuidedSetupStep;
        Window? window = null;
        try
        {
            settings.BasicSettings = basic;
            settings.GuidedSetupStep = null;
            var view = new SettingsView();
            window = new Window
            {
                Content = view, Width = windowWidth, Height = 940, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000
            };
            window.Show();
            var setSection = typeof(SettingsView).GetMethod("ApplySettingsSection",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            setSection.Invoke(view, [view.GamesSection]);
            view.GamesPage.RenderRows(Fixture(68));
            window.UpdateLayout();
            window.UpdateLayout();

            var sources = view.GamesPage;
            var games = sources.Games;
            var scroll = Descendants<ScrollViewer>(games).FirstOrDefault();
            var marker = Assert.IsType<ListBoxItem>(games.Items[3]);
            var discovery = Assert.IsType<ListBoxItem>(games.Items[2]);
            double firstGameY = double.NaN;
            var row = games.Items.OfType<GamesSettingsView.GameRow>().FirstOrDefault();
            if (row != null && games.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
                firstGameY = container.TranslatePoint(new Point(), games).Y;
            var scrollerField = typeof(GamesSettingsView).GetField("_catalogScroller",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var anchorField = typeof(GamesSettingsView).GetField("_searchAnchorDocumentY",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True(sources.SearchDock.Visibility == Visibility.Visible,
                $"Basic={basic}, panelVisible={sources.IsVisible}, listActual={games.ActualHeight:F1}, " +
                $"scroll={scroll?.VerticalOffset:F1}, cachedScroller={scrollerField.GetValue(sources) != null}, " +
                $"cachedAnchor={anchorField.GetValue(sources)}, markerAncestor={games.IsAncestorOf(marker)}, " +
                $"markerY={marker.TranslatePoint(new Point(),games).Y:F1}, " +
                $"discoveryAncestor={games.IsAncestorOf(discovery)}, " +
                $"discoveryY={discovery.TranslatePoint(new Point(),games).Y:F1}, " +
                $"discoveryH={discovery.ActualHeight:F1}, firstGameY={firstGameY:F1}");
            Assert.NotNull(scrollerField.GetValue(sources)); // Attachment must finish in Settings, not only wizard.
            var expectedSearchWidth = Math.Min(sources.SearchDock.ActualWidth, 1340);
            Assert.InRange(sources.Search.ActualWidth, expectedSearchWidth - 3, expectedSearchWidth + 3);
            double searchLeft = sources.Search.TranslatePoint(new Point(), sources.SearchDock).X;
            double centeredLeft = (sources.SearchDock.ActualWidth - sources.Search.ActualWidth) / 2;
            Assert.InRange(Math.Abs(searchLeft - centeredLeft), 0, 3);
            double markerY = marker.TranslatePoint(new Point(), games).Y;
            Assert.InRange(sources.SearchDockTransform.Y, markerY - 3, markerY + 3);
            sources.Search.Text = "Test game 001";
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, sources.SearchDock.Visibility);
            Assert.InRange(sources.SearchDockTransform.Y, 0, 1);
            scroll!.ScrollToTop();
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, sources.SearchDock.Visibility);
            Assert.Same(discovery, games.Items[2]); // Apps/discovery remain accessible above filtered games.
            sources.Search.Clear();
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, sources.SearchDock.Visibility);
            var output = Environment.GetEnvironmentVariable("QUICKCLIP_UI_RENDER_OUTPUT");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                var pixels = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                pixels.Render(view);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pixels));
                using var file = File.Create(Path.Combine(output, basic
                    ? "sources-basic-68.png" : "sources-advanced-68.png"));
                png.Save(file);
            }
        }
        finally
        {
            window?.Close();
            settings.BasicSettings = previous;
            settings.GuidedSetupStep = priorStep;
        }
    });
    [Fact]
    public Task Sticky_game_search_survives_recycled_rows_and_expansion() => OnSta(() =>
    {
        var page = new GamesSettingsView();
        var window = new Window
        {
            Title = "QuickClip Sticky Search Test",
            Content = page, Width = 900, Height = 680, ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            page.RenderRows(Fixture(120));
            // The first layout must display Search even before scrolling,
            // including when the virtualized marker wasn't realized at Loaded.
            var initiallyVisibleScroll = Descendants<ScrollViewer>(page.Games).First();
            initiallyVisibleScroll.ScrollToTop();
            window.UpdateLayout();
            var initialDiscovery = Assert.IsType<ListBoxItem>(page.Games.Items[2]);
            double initialSearchY = initialDiscovery.TranslatePoint(new Point(), page.Games).Y + initialDiscovery.ActualHeight;
            if (initialSearchY < page.Games.ActualHeight)
            {
                Assert.Equal(Visibility.Visible, page.SearchDock.Visibility);
                Assert.InRange(page.SearchDockTransform.Y, initialSearchY - 2, initialSearchY + 2);
            }
            var row = page.Games.Items.OfType<GamesSettingsView.GameRow>().Skip(80).First();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                page.Games.ScrollIntoView(row);
                window.UpdateLayout();
            }
            Assert.Equal(Visibility.Visible, page.SearchDock.Visibility);
            Assert.InRange(page.SearchDockTransform.Y, 0, 1);

            Assert.True(page.ToggleDetails(row));
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, page.SearchDock.Visibility);
            Assert.False(page.ToggleDetails(row)); // Collapsing returns false by design.
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, page.SearchDock.Visibility);

            var scroll = Descendants<ScrollViewer>(page.Games).First();
            scroll.ScrollToTop();
            window.UpdateLayout();
            var discovery = Assert.IsType<ListBoxItem>(page.Games.Items[2]);
            double naturalY = discovery.TranslatePoint(new Point(0, 0), page.Games).Y + discovery.ActualHeight;
            if (naturalY >= page.Games.ActualHeight)
                Assert.Equal(Visibility.Collapsed, page.SearchDock.Visibility);
            else
            {
                // With a short synthetic discovery header, the inline search is
                // naturally visible near the bottom rather than hidden.
                Assert.Equal(Visibility.Visible, page.SearchDock.Visibility);
                Assert.InRange(page.SearchDockTransform.Y, naturalY - 2, naturalY + 2);
            }
            page.Games.ScrollIntoView(row);
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, page.SearchDock.Visibility);
            Assert.InRange(page.SearchDockTransform.Y, 0, 1);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(8)]
    [InlineData(50)]
    [InlineData(200)]
    public Task Rows_expand_inline_and_keep_identity_when_filtered(int count) => OnSta(() =>
    {
        var page = new GamesSettingsView();
        var state = Fixture(count);
        page.RenderRows(state);
        Arrange(page);
        Assert.Equal(count + 4, page.Games.Items.Count); // Sources, Apps, discovery and search anchor.
        var rows = page.Games.Items.OfType<GamesSettingsView.GameRow>().ToArray();
        Assert.Equal(count, rows.Length);
        var first = rows[0];
        var second = rows[1];
        int firstIndex = page.Games.Items.IndexOf(first);
        Assert.Equal("Manual · Ready to record", first.Summary);
        page.Games.ScrollIntoView(first);
        Arrange(page);
        var container = Assert.IsType<ListBoxItem>(page.Games.ItemContainerGenerator.ContainerFromIndex(firstIndex));
        double collapsedHeight = container.ActualHeight;
        var capture = Descendants<CheckBox>(container).Single(button =>
            AutomationProperties.GetName(button).StartsWith("Disable automatic targeting", StringComparison.Ordinal));
        Assert.True(capture.Focusable);
        Assert.Equal($"Disable automatic targeting for {first.Entry.DisplayName}", capture.ToolTip);
        Assert.True(page.ToggleDetails(first));
        Arrange(page);
        Assert.True(container.ActualHeight > collapsedHeight);
        Assert.False(second.IsExpanded);
        if (count == 200)
            Assert.Null(page.Games.ItemContainerGenerator.ContainerFromIndex(page.Games.Items.IndexOf(rows[^1])));

        page.Search.Text = second.Entry.Executables[0];
        Assert.Same(second, Assert.Single(page.Games.Items.OfType<GamesSettingsView.GameRow>()));
        page.Search.Clear();
        Assert.Same(first, page.Games.Items.OfType<GamesSettingsView.GameRow>().First());
        Assert.True(first.IsExpanded);
        Assert.True(page.ToggleDetails(second));
        Assert.False(first.IsExpanded);
        Assert.True(second.IsExpanded);
    });

    [Fact]
    public Task Rule_state_round_trips_and_updates_accessible_action_without_replacing_row() => OnSta(() =>
    {
        var page = new GamesSettingsView();
        var state = Fixture(8);
        page.RenderRows(state);
        var row = Assert.IsType<GamesSettingsView.GameRow>(
            page.Games.Items.OfType<GamesSettingsView.GameRow>().First());
        string enabledIcon = row.CaptureIcon;
        state = state with
        {
            Request = state.Request with
            {
                Overrides = [new(row.Entry.Id, new(Inclusion: CatalogInclusion.Exclude))],
            },
            OfflineIds = [row.Entry.Id],
        };
        page.RenderRows(AppSettings.Parse(new AppSettings { Games = state }.ToJson()).Games);
        Assert.Same(row, page.Games.Items.OfType<GamesSettingsView.GameRow>().First());
        Assert.NotEqual(enabledIcon, row.CaptureIcon);
        Assert.Contains("Disabled", row.Summary);
        Assert.Contains("games remembered", row.Summary);
        Assert.StartsWith("Enable automatic targeting", row.CaptureAction);
        Assert.Contains("reconnect", GamesSettingsView.ScanIssueText(new("Test root", CatalogIssueKind.Unavailable)));
        Assert.Contains("manifest", GamesSettingsView.ScanIssueText(new("Test manifest", CatalogIssueKind.InvalidManifest)));
    });

    [Fact]
    public void Game_file_selection_defaults_and_preserves_available_choice()
    {
        const string first = @"C:\Games\Fixture\First.exe";
        const string second = @"C:\Games\Fixture\Second.exe";
        var entry = new CatalogEntry("manual:fixture", "Fixture", CatalogSource.Manual, [], [first, second], new());
        var row = new GamesSettingsView.GameRow(entry);
        Assert.Equal(first, row.SelectedExecutable);
        row.SelectedExecutable = second;
        row.Update(entry, false, [], new(new([entry], [], 0, false, default)));
        Assert.Equal(second, row.SelectedExecutable);
        row.SetUnavailableExecutables([second]);
        Assert.Equal(first, row.SelectedExecutable);
        row.SetUnavailableExecutables([first, second]);
        Assert.Null(row.SelectedExecutable);
    }

    [Fact]
    public void Manual_file_validation_accepts_large_collections_and_exact_non_games_show_ready()
    {
        var collection = Enumerable.Range(0, 128)
            .Select(i => $@"C:\TestLibrary\Game{i:000}.exe").ToArray();
        Assert.Null(GamesSettingsView.ValidateManualGameExecutables(collection));
        Assert.Null(GamesSettingsView.ValidateManualGameExecutables([@"C:\Apps\Discord.exe"]));
        var discord = new GamesSettingsView.GameRow(new("manual:discord", "Discord", CatalogSource.Manual,
            [], [@"C:\Apps\Discord.exe"], new()));
        Assert.DoesNotContain("Blocked", discord.Summary);
        Assert.EndsWith(" · Ready to record", Assert.Single(discord.Executables).Label);
        Assert.NotNull(GamesSettingsView.ValidateManualGameExecutables([@"C:\Apps\not-video.txt"]));
    }

    [Fact]
    public Task Catalog_totals_distinguish_manual_rows_and_overlapping_discovery_subsets() => OnSta(() =>
    {
        const string root = @"C:\Discovered";
        var current = new CatalogEntry("folder:current", "Current", CatalogSource.Folder,
            [root], [root + @"\Current.exe"], new());
        var retained = current with { Id = "folder:retained", Name = "Retained", Executables = [root + @"\Retained.exe"] };
        var state = Fixture(1);
        state = state with
        {
            Request = state.Request with { GameRoots = [new(root)] },
            Snapshot = new([current, retained], [], 12, true, default),
            DiscoveredCount = 1,
            RetainedIds = [retained.Id, "folder:missing"],
            OfflineIds = [retained.Id],
        };
        var page = new GamesSettingsView();
        page.RenderRows(state);

        Assert.Equal("Catalog · 3 unique games and apps · 1 manual entries", page.Count.Text);
        Assert.Equal("1 entries with cached paths · 1 awaiting verification", page.DiscoveryCountDetail.Text);
        page.Search.Text = "Retained";
        Assert.EndsWith(" · 1 shown", page.Count.Text);
        Assert.Single(page.Games.Items.OfType<GamesSettingsView.GameRow>());
    });

    [Fact]
    public async Task Loose_collection_executable_needs_association_and_dismissal_follows_issue_type()
    {
        var scratch = Directory.CreateTempSubdirectory("quickclip-collection-");
        try
        {
            string path = Path.Combine(scratch.FullName, "Fixture.exe");
            await File.WriteAllBytesAsync(path, [1]);
            var scan = await GameCatalogService.ScanAsync(new CatalogRequest { GameRoots = [new(scratch.FullName, true)] });
            var loose = Assert.Single(scan.Entries, entry => entry.Executables.Contains(path, StringComparer.OrdinalIgnoreCase));
            var row = new GamesSettingsView.GameRow(loose);
            row.EvaluateIssue([], scanning: false);
            Assert.Equal(GamesSettingsView.DetectionIssue.UnassociatedExecutable, row.Issue);
            var ignored = AppSettings.Parse(new AppSettings { IgnoredGameIssues = row.IssueKeys().ToArray() }.ToJson()).IgnoredGameIssues;
            row.EvaluateIssue(ignored, scanning: false);
            Assert.True(row.IssueIgnored);
            Assert.False(row.DetectionReady);

            var approved = loose with { Rules = new() { ApprovedExecutables = [path] } };
            row.Update(approved, false, [], new(new([approved], [], 0, false, default)));
            row.EvaluateIssue(ignored, scanning: false);
            Assert.True(row.DetectionReady);
            Assert.False(row.HasIssue);

            var offline = loose with { UnverifiedExecutables = [path] };
            row.Update(offline, true, [], new(new([offline], [], 0, false, default)));
            row.EvaluateIssue(ignored, scanning: true);
            Assert.False(row.HasIssue);
            row.EvaluateIssue(ignored, scanning: false);
            Assert.Equal(GamesSettingsView.DetectionIssue.SourceUnavailable, row.Issue);
            Assert.False(row.IssueIgnored);
        }
        finally { scratch.Delete(recursive: true); }
    }

    [Fact]
    public void Launcher_and_block_rules_stay_out_of_actionable_issues_except_minecraft_proxy()
    {
        var proxy = new CatalogEntry("xbox:fixture", "Fixture", CatalogSource.Xbox, [@"C:\Games\Fixture"],
            [@"C:\Games\Fixture\MCPlaceholderStub.exe"], new());
        var row = new GamesSettingsView.GameRow(proxy);
        row.EvaluateIssue([], scanning: false);
        Assert.Equal(GamesSettingsView.DetectionIssue.MinecraftJava, row.Issue);

        var launcher = proxy with { Executables = [@"C:\Games\Fixture\Launcher.exe"] };
        row.Update(launcher, false, [], new(new([launcher], [], 0, false, default)));
        row.EvaluateIssue([], scanning: false);
        Assert.False(row.HasIssue);

        var disabled = proxy with { Rules = new(Inclusion: CatalogInclusion.Exclude) };
        row.Update(disabled, false, [], new(new([disabled], [], 0, false, default)));
        row.EvaluateIssue([], scanning: false);
        Assert.False(row.HasIssue);
    }

    private static GameCatalogSettings Fixture(int count) => new()
    {
        Request = new()
        {
            ManualEntries = Enumerable.Range(0, count).Select(index => new ManualCatalogEntry(
                Guid.NewGuid(), $"Test game {index:000}", [$@"C:\SyntheticGames\Game{index:000}.exe"])).ToImmutableArray(),
        },
    };

    private static void Arrange(FrameworkElement element, double width = 1050, double height = 660)
    {
        var size = new Size(width, height);
        element.InvalidateMeasure();
        element.InvalidateArrange();
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task OnSta(Action run)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                run();
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

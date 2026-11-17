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
        var page = new GamesSettingsView();
        page.LayoutTransform = new ScaleTransform(scale, scale);
        Arrange(page, width, height);
        Assert.True(page.Games.ActualHeight > 0);
        Assert.True(page.Search.ActualWidth > 0);
        Assert.False(Descendants<TextBox>(page.Games).Contains(page.Search));
        Assert.True(page.AutoScan.IsChecked == true);
        page.RenderRows(Fixture(200));
        Arrange(page, width, height);
        var scroll = Descendants<ScrollViewer>(page.Games).First();
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.Null(page.Games.ItemContainerGenerator.ContainerFromIndex(200));
        page.Games.ScrollIntoView(page.Games.Items[200]);
        Arrange(page, width, height);
        Assert.NotNull(page.Games.ItemContainerGenerator.ContainerFromIndex(200));
        Assert.False(page.CancelButton.IsEnabled);
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
        Assert.Equal(count + 1, page.Games.Items.Count);
        var first = Assert.IsType<GamesSettingsView.GameRow>(page.Games.Items[1]);
        var second = Assert.IsType<GamesSettingsView.GameRow>(page.Games.Items[2]);
        Assert.Equal("Manual · Ready to record", first.Summary);
        var container = Assert.IsType<ListBoxItem>(page.Games.ItemContainerGenerator.ContainerFromIndex(1));
        double collapsedHeight = container.ActualHeight;
        var capture = Descendants<CheckBox>(container).Single(button =>
            AutomationProperties.GetName(button).StartsWith("Disable automatic targeting", StringComparison.Ordinal));
        Assert.True(capture.Focusable);
        Assert.Equal($"Disable automatic targeting for {first.Entry.DisplayName}", capture.ToolTip);
        Assert.True(page.ToggleDetails(first));
        Arrange(page);
        Assert.True(container.ActualHeight > collapsedHeight);
        Assert.False(second.IsExpanded);
        if (count == 200) Assert.Null(page.Games.ItemContainerGenerator.ContainerFromIndex(count));

        page.Search.Text = second.Entry.Executables[0];
        Assert.Same(second, Assert.Single(page.Games.Items.OfType<GamesSettingsView.GameRow>()));
        page.Search.Clear();
        Assert.Same(first, page.Games.Items[1]);
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
        var row = Assert.IsType<GamesSettingsView.GameRow>(page.Games.Items[1]);
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
        Assert.Same(row, page.Games.Items[1]);
        Assert.NotEqual(enabledIcon, row.CaptureIcon);
        Assert.Contains("Disabled", row.Summary);
        Assert.Contains("games remembered", row.Summary);
        Assert.StartsWith("Enable automatic targeting", row.CaptureAction);
        Assert.Contains("reconnect", GamesSettingsView.ScanIssueText(new("Test root", CatalogIssueKind.Unavailable)));
        Assert.Contains("manifest", GamesSettingsView.ScanIssueText(new("Test manifest", CatalogIssueKind.InvalidManifest)));
    });

    [Fact]
    public void Manual_file_validation_accepts_large_collections_and_saved_non_games_show_blocked()
    {
        var collection = Enumerable.Range(0, 128)
            .Select(i => $@"C:\TestLibrary\Game{i:000}.exe").ToArray();
        Assert.Null(GamesSettingsView.ValidateManualGameExecutables(collection));
        Assert.Null(GamesSettingsView.ValidateManualGameExecutables([@"C:\Apps\Discord.exe"]));
        var discord = new GamesSettingsView.GameRow(new("manual:discord", "Discord", CatalogSource.Manual,
            [], [@"C:\Apps\Discord.exe"], new()));
        Assert.Contains("Blocked", discord.Summary);
        Assert.EndsWith(" · Blocked (companion application)", Assert.Single(discord.Executables).Label);
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

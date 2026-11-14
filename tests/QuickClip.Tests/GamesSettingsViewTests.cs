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
    public void Collection_candidate_explains_missing_evidence_and_accepts_only_its_selected_renderer()
    {
        const string renderer = @"C:\Collection\osu!lazer\osu!.exe";
        const string helper = @"C:\Collection\osu!lazer\chrome.exe";
        var entry = new CatalogEntry("folder:osu", "osu!lazer", CatalogSource.Folder, [@"C:\Collection\osu!lazer"],
            [renderer, helper], new()) { RendererExecutables = [] };
        var row = new GamesSettingsView.GameRow(entry);
        Assert.Contains("no renderer evidence", row.Executables.First().Label);
        Assert.NotNull(row.RulesAfterApproval([renderer]));
        Assert.Null(row.RulesAfterApproval([helper]));
        var approved = entry with { Rules = new() { ApprovedExecutables = [renderer] } };
        row.Update(approved, false, [], new(new([approved], [], 0, false, default)));
        Assert.Contains("Game · enabled", row.Summary);
        Assert.Contains("verified window and display permission", row.Executables.First().Label);
        Assert.Null(row.RulesAfterApproval([renderer]));
        row.Update(approved, true, [], new(new([approved], [], 0, false, default)));
        Assert.Contains("scan incomplete", row.Executables.First().Label);
    }

    [Fact]
    public Task Games_page_loads_at_a_typical_scaled_desktop_size() => OnSta(() =>
    {
        var page = new GamesSettingsView();
        Arrange(page);
        Assert.True(page.Games.ActualHeight > 0);
        Assert.True(page.AutoScan.IsChecked == true);
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
        Assert.Equal(count, page.Games.Items.Count);
        var first = Assert.IsType<GamesSettingsView.GameRow>(page.Games.Items[0]);
        var second = Assert.IsType<GamesSettingsView.GameRow>(page.Games.Items[1]);
        Assert.Equal("Manual · Game · enabled", first.Summary);
        var container = Assert.IsType<ListBoxItem>(page.Games.ItemContainerGenerator.ContainerFromIndex(0));
        double collapsedHeight = container.ActualHeight;
        var capture = Descendants<Button>(container).Single(button =>
            AutomationProperties.GetName(button).StartsWith("Disable automatic targeting", StringComparison.Ordinal));
        Assert.True(capture.Focusable);
        Assert.Equal($"Disable automatic targeting for {first.Entry.DisplayName}", capture.ToolTip);
        Assert.True(page.ToggleDetails(first));
        Arrange(page);
        Assert.True(container.ActualHeight > collapsedHeight);
        Assert.False(second.IsExpanded);
        if (count == 200) Assert.Null(page.Games.ItemContainerGenerator.ContainerFromIndex(count - 1));

        page.Search.Text = second.Entry.Executables[0];
        Assert.Same(second, Assert.Single(page.Games.Items));
        page.Search.Clear();
        Assert.Same(first, page.Games.Items[0]);
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
        var row = Assert.IsType<GamesSettingsView.GameRow>(page.Games.Items[0]);
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
        Assert.Same(row, page.Games.Items[0]);
        Assert.NotEqual(enabledIcon, row.CaptureIcon);
        Assert.Contains("Disabled", row.Summary);
        Assert.Contains("games remembered", row.Summary);
        Assert.StartsWith("Enable automatic targeting", row.CaptureAction);
        Assert.Contains("reconnect", GamesSettingsView.ScanIssueText(new("Test root", CatalogIssueKind.Unavailable)));
        Assert.Contains("manifest", GamesSettingsView.ScanIssueText(new("Test manifest", CatalogIssueKind.InvalidManifest)));
    });

    [Fact]
    public void Manual_file_selection_accepts_large_collections_and_companion_apps()
    {
        var collection = Enumerable.Range(0, 128)
            .Select(i => $@"C:\TestLibrary\Game{i:000}.exe").ToArray();
        Assert.Null(GamesSettingsView.ValidateManualGameExecutables(collection));
        Assert.Null(GamesSettingsView.ValidateManualGameExecutables([@"C:\Apps\Discord.exe"]));
        var discord = new GamesSettingsView.GameRow(new("manual:discord", "Discord", CatalogSource.Manual,
            [], [@"C:\Apps\Discord.exe"], new()));
        Assert.Contains("Companion app", discord.Summary);
        Assert.EndsWith(" · companion", Assert.Single(discord.Executables).Label);
        Assert.NotNull(GamesSettingsView.ValidateManualGameExecutables([@"C:\Apps\not-video.txt"]));
    }

    private static GameCatalogSettings Fixture(int count) => new()
    {
        Request = new()
        {
            ManualEntries = Enumerable.Range(0, count).Select(index => new ManualCatalogEntry(
                Guid.NewGuid(), $"Test game {index:000}", [$@"C:\SyntheticGames\Game{index:000}.exe"])).ToImmutableArray(),
        },
    };

    private static void Arrange(FrameworkElement element)
    {
        var size = new Size(1050, 660);
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

using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickClip.Recording;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class GuidedSetupRenderTests
{
    [Fact]
    public async Task Render_synthetic_guided_display_cards_and_visual_feedback_without_screen_capture()
    {
        // Opt-in evidence only: run under QUICKCLIP_PERSONAL_DATA_DIR pointing to an
        // isolated fixture folder. Never render the user's real profile or desktop.
        var output = Environment.GetEnvironmentVariable("QUICKCLIP_UI_RENDER_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Assert.False(AppProfile.UsesDefaultDataRoot);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var settings = AppSettings.Current;
                var app = new App();
                app.InitializeComponent();
                (settings.InterfaceScale ?? new()).Apply(app.Resources);
                app.Themes.Attach(app);
                app.Themes.Apply(app, settings);
                settings.GuidedSetupStep = 6;
                settings.AllowRecordingDisplaysByDefault = false;
                settings.AllowedRecordingDisplays = ["synthetic-a"];
                settings.ExcludedGameDisplays = [];
                settings.RecordMonitor = @"\\.\DISPLAY1";
                settings.RecordMonitorIdentity = "synthetic-a";
                settings.RecordingEnabled = false;
                var view = new SettingsView();
                view.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "PrimaryTextBrush");
                view.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
                var field = typeof(SettingsView).GetField("_monitors",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var render = typeof(SettingsView).GetMethod("RenderDisplayCards",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var monitors = new[]
                {
                    new MonitorInfo(@"\\.\DISPLAY1", "Gaming ultrawide", 0, 0, 3440, 1440) { CaptureIdentity = "synthetic-a" },
                    new MonitorInfo(@"\\.\DISPLAY2", "Chat monitor", 3440, 0, 1920, 1080) { CaptureIdentity = "synthetic-b" }
                };
                field.SetValue(view, monitors);
                view.BeginGuidedSetup();
                render.Invoke(view, []);
                Directory.CreateDirectory(output);
                SaveFrame(view, Path.Combine(output, "two-monitors.png"));
                settings.GuidedSetupStep = 5;
                typeof(SettingsView).GetMethod("ShowGuideStep",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, []);
                SaveFrame(view, Path.Combine(output, "sources.png"));
                var sources = (GamesSettingsView)view.FindName("GamesPage")!;
                var catalog = (System.Windows.Controls.ListBox)sources.FindName("Games")!;
                var apps = (System.Windows.Controls.ItemsControl)sources.FindName("Applications")!;
                var appsRow = Assert.IsType<System.Windows.Controls.ListBoxItem>(catalog.Items[1]);
                var appsRoot = Assert.IsType<System.Windows.Controls.StackPanel>(appsRow.Content);
                Assert.Contains(apps, appsRoot.Children.Cast<UIElement>());
                var search = Assert.IsType<System.Windows.Controls.TextBox>(sources.FindName("Search"));
                var placeholder = Assert.IsType<System.Windows.Controls.ListBoxItem>(catalog.Items[3]);
                Assert.Same(placeholder, sources.FindName("SearchAnchorItem"));
                Assert.False(catalog.IsAncestorOf(search)); // Filtering cannot recycle the input.
                var collection = Assert.IsType<System.Windows.Data.CompositeCollection>(catalog.ItemsSource);
                var padding = Assert.IsType<System.Windows.Data.CollectionContainer>(
                    sources.FindName("SearchScrollPadding"));
                int insertAt = collection.IndexOf(padding);
                Assert.True(insertAt > 3);
                var fixtureRows = new List<System.Windows.Controls.ListBoxItem>();
                for (int i = 0; i < 24; i++)
                {
                    var row = new System.Windows.Controls.ListBoxItem
                    {
                        Content = $"Fixture game {i + 1} · Ready to record",
                        Focusable = false, Height = 60
                    };
                    fixtureRows.Add(row);
                    collection.Insert(insertAt + i, row);
                }
                SaveFrame(view, Path.Combine(output, "sources-catalog-fixture.png"));
                // RenderTargetBitmap fixtures are not hosted by a Window, so
                // Loaded is not raised automatically; attach the real listener.
                typeof(GamesSettingsView).GetMethod("AttachStickySearch",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sources, []);
                view.UpdateLayout();
                // Save the first unfiltered Sources layout before forcibly
                // scrolling the search marker; the old screenshot tests only
                // exercised the scrolled/sticky state.
                SaveFrame(view, Path.Combine(output, "sources-initial-search.png"));
                var findScroller = typeof(GamesSettingsView).GetMethod("FindPageScrollViewer",
                    BindingFlags.NonPublic | BindingFlags.Static)!;
                var scroller = Assert.IsType<System.Windows.Controls.ScrollViewer>(
                    findScroller.Invoke(null, [catalog]));
                var positionSearch = typeof(GamesSettingsView).GetMethod("PositionGameSearch",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                positionSearch.Invoke(sources, []);
                double anchorY = placeholder.TranslatePoint(new Point(0, 0), catalog).Y
                    + scroller.VerticalOffset;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    scroller.ScrollToVerticalOffset(anchorY + 140);
                    view.UpdateLayout();
                }
                SaveFrame(view, Path.Combine(output, "sources-sticky-search.png"));
                var dock = Assert.IsType<System.Windows.Controls.Border>(sources.FindName("SearchDock"));
                var transform = Assert.IsType<TranslateTransform>(
                    sources.FindName("SearchDockTransform"));
                Assert.True(dock.Visibility == Visibility.Visible,
                    $"Sticky search collapsed at offset={scroller.VerticalOffset:F1}; " +
                    $"extent={scroller.ExtentHeight:F1}, view={scroller.ViewportHeight:F1}, " +
                    $"lastAnchor={typeof(GamesSettingsView).GetField("_searchAnchorDocumentY", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sources)}, " +
                    $"markerY={placeholder.TranslatePoint(new Point(0, 0), catalog).Y:F1}, " +
                    $"markerAncestor={catalog.IsAncestorOf(placeholder)}, " +
                    $"catalogActual={catalog.ActualHeight:F1}");
                Assert.InRange(transform.Y, 0, 1);

                // Typing never removes Apps/Discord or discovery from the list.
                search.Text = "port";
                search.CaretIndex = search.Text.Length;
                search.Text += "al";
                search.CaretIndex = search.Text.Length;
                Assert.Same(appsRow, catalog.Items[1]);
                Assert.Same(placeholder, catalog.Items[3]);
                Assert.Same(search, sources.FindName("Search"));
                Assert.Equal("portal", search.Text);
                Assert.Equal(6, search.CaretIndex);
                Assert.Equal(Visibility.Visible, dock.Visibility);
                Assert.InRange(transform.Y, 0, 1);
                Assert.True(padding.Collection?.Cast<object>().Count() >= 16);

                // Simulate 24 games narrowing to just one, and then none.
                // The result must sit directly below search, not remain at
                // a stale position or collapse the scroll range.
                foreach (var row in fixtureRows.Skip(1)) collection.Remove(row);
                positionSearch.Invoke(sources, []);
                view.UpdateLayout();
                Assert.True(scroller.ScrollableHeight >= anchorY - 2,
                    $"Narrow query could not reach search: max={scroller.ScrollableHeight:F1}, anchor={anchorY:F1}");
                Assert.InRange(transform.Y, 0, 1);
                SaveFrame(view, Path.Combine(output, "sources-one-result-stable.png"));

                // Scroll UP with the query still active. Apps and discovery must
                // be reachable by ordinary scrolling, without clearing search.
                scroller.ScrollToTop();
                view.UpdateLayout();
                Assert.InRange(scroller.VerticalOffset, 0, 1);
                Assert.Same(appsRow, catalog.Items[1]);
                Assert.Contains(apps, appsRoot.Children.Cast<UIElement>());
                Assert.True(appsRow.TranslatePoint(new Point(0, 0), catalog).Y < catalog.ActualHeight);
                Assert.True(transform.Y > 10, "Search should scroll beneath Apps while browsing upward.");
                SaveFrame(view, Path.Combine(output, "sources-search-scroll-up-apps.png"));
                positionSearch.Invoke(sources, []);
                view.UpdateLayout();
                Assert.InRange(transform.Y, 0, 1);

                collection.Remove(fixtureRows[0]);
                positionSearch.Invoke(sources, []);
                SaveFrame(view, Path.Combine(output, "sources-zero-results.png"));
                Assert.Same(appsRow, catalog.Items[1]);
                Assert.InRange(transform.Y, 0, 1);

                // Clearing restores the game list in place and drops the
                // temporary scroll padding without replacing the search input.
                foreach (var row in fixtureRows) collection.Insert(collection.IndexOf(padding), row);
                search.Clear();
                Assert.Null(padding.Collection);
                Assert.Same(appsRow, catalog.Items[1]);
                Assert.Same(search, sources.FindName("Search"));
                SaveFrame(view, Path.Combine(output, "sources-after-clear.png"));
                Assert.InRange(transform.Y, 0, 1);

                // Regression for intermittent overlay/focus faults after several
                // search/clear cycles and WPF's virtualized extent recalculation.
                for (int cycle = 0; cycle < 12; cycle++)
                {
                    search.Text = $"game {cycle}";
                    search.CaretIndex = search.Text.Length;
                    Assert.Same(appsRow, catalog.Items[1]);
                    Assert.True(padding.Collection?.Cast<object>().Count() >= 16);
                    Assert.Same(search, sources.FindName("Search"));
                    search.Clear();
                    Assert.Null(padding.Collection);
                    Assert.Same(appsRow, catalog.Items[1]);
                    Assert.Equal(1, catalog.Items.Cast<object>().Count(item => ReferenceEquals(item, appsRow)));
                    Assert.Same(search, sources.FindName("Search"));
                }
                view.UpdateLayout();
                Assert.Equal(Visibility.Visible, dock.Visibility);
                Assert.InRange(transform.Y, 0, 1);
                SaveFrame(view, Path.Combine(output, "sources-after-repeated-search.png"));
                settings.GuidedSetupStep = 6;
                typeof(SettingsView).GetMethod("ShowGuideStep",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, []);
                settings.RecordMonitor = @"\\.\DISPLAY2";
                settings.RecordMonitorIdentity = "disconnected-identity";
                field.SetValue(view, monitors);
                render.Invoke(view, []);
                SaveFrame(view, Path.Combine(output, "reordered-stale-target.png"));

                settings.RecordMonitor = "";
                settings.RecordMonitorIdentity = null;
                field.SetValue(view, monitors);
                render.Invoke(view, []);
                Assert.True(((System.Windows.Controls.RadioButton)view.FindName("NoManualDefaultButton")!).IsChecked);
                SaveFrame(view, Path.Combine(output, "two-monitors-no-default.png"));
                field.SetValue(view, monitors[..1]);
                render.Invoke(view, []);
                SaveFrame(view, Path.Combine(output, "one-monitor.png"));

                settings.GuidedSetupStep = 7;
                typeof(SettingsView).GetMethod("ShowGuideStep",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, []);
                SaveFrame(view, Path.Combine(output, "notification-feedback.png"));

                settings.GuidedSetupStep = 8;
                typeof(SettingsView).GetMethod("ShowGuideStep",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, []);
                SaveFrame(view, Path.Combine(output, "completion.png"));

                settings.Theme = "Light";
                app.Themes.Apply(app, settings);
                settings.GuidedSetupStep = 6;
                settings.RecordMonitor = @"\\.\DISPLAY1";
                settings.RecordMonitorIdentity = "synthetic-a";
                var lightView = new SettingsView();
                lightView.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "PrimaryTextBrush");
                lightView.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
                field.SetValue(lightView, monitors);
                lightView.BeginGuidedSetup();
                render.Invoke(lightView, []);
                SaveFrame(lightView, Path.Combine(output, "two-monitors-light.png"));
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void SaveFrame(FrameworkElement view, string path)
    {
        const int width = 860, height = 930;
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

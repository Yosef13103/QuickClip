using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using QuickClip.Shell;
using QuickClip.Themes;
using QuickClip.Views;
using Xunit.Abstractions;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class FrontendWindowAcceptanceTests(ITestOutputHelper output)
{
    private sealed class ThemeTestApp : App
    {
        // WPF queues OnStartup even when a fixture only pumps the dispatcher.
        // Keep the real tray, single-instance IPC and recorder out of this fixture.
        protected override void OnStartup(StartupEventArgs e) => ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    [OptInFact("QUICKCLIP_TEST_FRONTEND_WINDOWS")]
    public Task Theme_import_switch_and_reopen_preserves_the_saved_picker_library() => OnDispatcher(async () =>
    {
        if (!AppProfile.IsPersonal) return;
        var app = new ThemeTestApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Themes.Attach(app);
        var profile = AppSettings.Current;
        app.Themes.Apply(app, profile);
        var fixture = Directory.CreateTempSubdirectory("quickclip-theme-window-");
        var view = new AppearanceSettings();
        var window = new Window
        {
            Width = 600, Height = 450, Left = -16000, Top = -16000, ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Content = view,
        };
        var saveDisabled = AppSettings.SaveDisabled;
        try
        {
            AppSettings.SaveDisabled = false; // TestSetup redirects settings to a disposable profile.
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            foreach (var (name, choice) in new[] { ("Fixture Slate", ThemeChoice.Slate), ("Fixture AMOLED", ThemeChoice.Amoled) })
            {
                string path = Path.Combine(fixture.FullName, name + ".json");
                using (var file = File.Create(path)) new ThemeDocument(name, ThemePalette.Get(choice)).Write(file);
                var designer = view.CreateThemeDesigner(window);
                designer.ShowActivated = false;
                designer.WindowStartupLocation = WindowStartupLocation.Manual;
                designer.Left = designer.Top = -16000;
                try
                {
                    designer.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var editor = Assert.IsType<CustomThemeEditor>(Assert.IsType<ScrollViewer>(designer.Content).Content);
                    editor.ImportTheme(path);
                }
                finally { designer.Close(); }
                Assert.Null(designer.Content);
                File.Delete(path);
            }
            Assert.Equal(2, profile.SavedCustomThemes.Length);
            Assert.Equal(6, view.ThemePicker.Items.Count);
            await VerifyThemePopupAsync(view.ThemePicker, "Fixture Slate", "Fixture AMOLED");
            view.ThemePicker.SelectionChanged += (_, e) => output.WriteLine($"selection={view.ThemePicker.SelectedIndex}, loaded={view.IsLoaded}, pickerLoaded={view.ThemePicker.IsLoaded}, applied={profile.Theme}, source={e.OriginalSource?.GetType().Name}");
            view.ThemePicker.SelectedIndex = 3; // Light changes the WPF Fluent theme mode.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(nameof(ThemeChoice.Light), profile.Theme);
            Assert.Equal(6, view.ThemePicker.Items.Count);
            output.WriteLine($"before returning to custom: selection={view.ThemePicker.SelectedIndex}, loaded={view.IsLoaded}, pickerLoaded={view.ThemePicker.IsLoaded}");
            view.ThemePicker.SelectedIndex = 4;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(nameof(ThemeChoice.Custom), profile.Theme);
            Assert.Equal(6, view.ThemePicker.Items.Count);
            var persisted = AppSettings.Parse(File.ReadAllText(AppSettings.FilePath));
            var reopened = new AppearanceSettings();
            reopened.RefreshSelection(persisted);
            Assert.Equal(6, reopened.ThemePicker.Items.Count);
            Assert.Equal(view.ThemePicker.SelectedIndex, reopened.ThemePicker.SelectedIndex);
            Assert.Equal(2, persisted.SavedCustomThemes.Length);
            await VerifyThemePopupAsync(view.ThemePicker, "Fixture Slate", "Fixture AMOLED");

            var active = Assert.IsType<ThemeDocument>(ThemeResources.ReadSaved(profile.CustomThemeJson));
            string duplicate = active.Name == "Fixture Slate" ? "Fixture AMOLED" : "Fixture Slate";
            Assert.NotNull(view.RenameTheme(active, duplicate));
            using (new FileStream(AppSettings.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.NotNull(view.RenameTheme(active, "Renamed Fixture"));
            Assert.Equal(active.Name, ThemeResources.ReadSaved(profile.CustomThemeJson)!.Name);
            Assert.Null(view.RenameTheme(active, "Renamed Fixture"));
            await VerifyThemePopupAsync(view.ThemePicker, "Renamed Fixture", duplicate);
            var renamedProfile = AppSettings.Parse(File.ReadAllText(AppSettings.FilePath));
            Assert.DoesNotContain(renamedProfile.SavedCustomThemes,
                json => ThemeResources.ReadSaved(json)?.Name == active.Name);
        }
        finally
        {
            AppSettings.SaveDisabled = saveDisabled;
            window.Close();
            fixture.Delete(recursive: true);
        }
    });

    private static async Task VerifyThemePopupAsync(ComboBox picker, params string[] names)
    {
        picker.ApplyTemplate();
        picker.IsDropDownOpen = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = Assert.IsType<Popup>(picker.Template.FindName("PART_Popup", picker));
            Assert.True(popup.IsOpen);
            var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            child.UpdateLayout();
            string[] labels = Labels(child).ToArray();
            foreach (string name in names) Assert.Contains(name + " (custom)", labels);
        }
        finally { picker.IsDropDownOpen = false; }

        static IEnumerable<string> Labels(DependencyObject element)
        {
            if (element is TextBlock text) yield return text.Text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                foreach (var label in Labels(VisualTreeHelper.GetChild(element, i))) yield return label;
        }
    }

    [OptInFact("QUICKCLIP_TEST_FRONTEND_WINDOWS")]
    public Task Hud_preview_uses_display_affinity_or_stays_hidden() => OnDispatcher(async () =>
    {
        var display = Assert.Single(ClipNotificationOverlay.Displays(), item => item.Primary);
        using var overlay = new ClipNotificationOverlay();
        overlay.ShowRecordingIndicator(display.Device, OverlayAnchor.TopRight, MicrophoneIndicatorState.Active);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        output.WriteLine($"visible={overlay.IsVisible}, opaque={overlay.IndicatorOpaque}, layeredWin32={overlay.IndicatorLayeredError}, status={overlay.IndicatorFailure ?? "excluded by Windows API"}");
        if (overlay.IsVisible) Assert.Null(overlay.IndicatorFailure);
        else Assert.False(string.IsNullOrWhiteSpace(overlay.IndicatorFailure));
        overlay.Hide();
        Assert.False(overlay.IsVisible);
    });

    private static async Task OnDispatcher(Func<Task> run)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await run(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}

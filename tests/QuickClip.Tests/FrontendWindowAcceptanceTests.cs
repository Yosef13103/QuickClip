using System.Windows;
using System.Windows.Threading;
using QuickClip.Shell;
using QuickClip.Themes;
using QuickClip.Views;
using Xunit.Abstractions;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class FrontendWindowAcceptanceTests(ITestOutputHelper output)
{
    [OptInFact("QUICKCLIP_TEST_FRONTEND_WINDOWS")]
    public Task Theme_import_switch_and_reopen_preserves_the_saved_picker_library() => OnDispatcher(async () =>
    {
        if (!AppProfile.IsPersonal) return;
        var app = new App();
        app.InitializeComponent();
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
                view.CustomEditor.ImportTheme(path);
                File.Delete(path);
            }
            Assert.Equal(6, view.ThemePicker.Items.Count);
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
        }
        finally
        {
            AppSettings.SaveDisabled = saveDisabled;
            window.Close();
            fixture.Delete(recursive: true);
        }
    });

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

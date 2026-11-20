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
                view.PaletteExpander.IsExpanded = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(view.CustomEditor.IsVisible);
                view.CustomEditor.ImportTheme(path);
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
            view.ThemePicker.SelectedIndex = 1; // AMOLED uses the Broadcast palette.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(nameof(ThemeChoice.Amoled), profile.Theme);
            Assert.Equal(nameof(ThemeChoice.Amoled), AppSettings.Parse(File.ReadAllText(AppSettings.FilePath)).Theme);
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

            const string inlineName = "New Inline Theme";
            var editor = view.CustomEditor;
            editor.LoadDraft(new ThemeDocument(inlineName, ThemePalette.Get(ThemeChoice.Dark)));
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await VerifyThemePopupAsync(view.ThemePicker, inlineName, "Renamed Fixture", duplicate);
            string exported = Path.Combine(fixture.FullName, "export.json");
            editor.ExportTheme(exported);
            using (var stream = File.OpenRead(exported))
                Assert.Equal(inlineName, ThemeDocument.Read(stream).Name);
            Assert.True(view.RemoveTheme(app, inlineName));
            Assert.Equal(nameof(ThemeChoice.Dark), profile.Theme);
            Assert.DoesNotContain(AppSettings.Parse(File.ReadAllText(AppSettings.FilePath)).SavedCustomThemes,
                json => ThemeResources.ReadSaved(json)?.Name == inlineName);
            await VerifyThemePopupAsync(view.ThemePicker, "Renamed Fixture", duplicate);
        }
        finally
        {
            AppSettings.SaveDisabled = saveDisabled;
            window.Close();
            fixture.Delete(recursive: true);
        }
    });

    [OptInFact("QUICKCLIP_TEST_FRONTEND_WINDOWS")]
    public Task Label_manager_keeps_same_overlay_for_several_committed_edits() => OnDispatcher(async () =>
    {
        if (!AppProfile.IsPersonal) return;
        var app = new ThemeTestApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Themes.Attach(app);
        var settings = AppSettings.Current;
        var originalFolder = settings.ClipsFolder;
        var originalIndex = settings.EnableLibraryIndex;
        var fixture = Directory.CreateTempSubdirectory("quickclip-label-window-");
        settings.ClipsFolder = fixture.FullName;
        settings.EnableLibraryIndex = true;
        app.Themes.Apply(app, settings);
        var view = new GalleryView();
        var root = new Grid();
        root.Children.Add(view);
        var window = new Window
        {
            Width = 1000, Height = 720, Left = -16000, Top = -16000, Content = root,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        Exception? error = null;
        try
        {
            window.Show();
            var loading = typeof(GalleryView).GetField("_loading", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(400);
            await Wait(() => !(bool)loading.GetValue(view)!);
            Assert.True(QuickClip.Library.PersonalLibrary.Enabled);
            Assert.True(await view.FlushAnnotationsAsync());
            var sort = new[] { view.FolderNameSort, view.FolderDateSort };
            foreach (var button in sort)
            {
                button.UpdateLayout();
                Assert.Equal(button.ActualWidth, button.ActualHeight, 2);
                Assert.True(button.ActualWidth >= 32);
                Assert.NotNull(button.ToolTip);
            }
            var manage = Visuals(view).OfType<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Manage labels");
            var interaction = window.Dispatcher.InvokeAsync(async () =>
            {
                Grid? overlay = null;
                try
                {
                    await Wait(() => root.Children.Count > 1);
                    overlay = Assert.IsType<Grid>(root.Children[1]);
                    var name = Find<TextBox>(overlay, "New label name");
                    var swatch = Find<Button>(overlay, "Change color: New label");
                    swatch.ApplyTemplate();
                    var surface = Assert.IsType<Border>(swatch.Template.FindName("Surface", swatch));
                    Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(surface.Background).Color);
                    Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(surface.BorderBrush).Color);
                    Assert.IsType<System.Windows.Shapes.Ellipse>(swatch.Content);
                    name.Text = "Fixture label";
                    Click(Find<Button>(overlay, "+ Add label", content: true));
                    await Wait(() => Visuals(overlay).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Rename Fixture label"));
                    Assert.Same(overlay, root.Children[1]);
                    Assert.Same(name, Find<TextBox>(overlay, "New label name"));
                    Click(Find<Button>(overlay, "Rename Fixture label"));
                    var rename = Find<TextBox>(overlay, "Rename Fixture label");
                    rename.Text = "Renamed label";
                    Click(Find<Button>(overlay, "Rename Fixture label"));
                    await Wait(() => Visuals(overlay).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Rename Renamed label"));
                    Assert.Same(overlay, root.Children[1]);
                    Assert.Same(name, Find<TextBox>(overlay, "New label name"));
                    var tags = await QuickClip.Library.PersonalLibrary.Current.ReadTagDefinitionsAsync(default);
                    Assert.Contains(tags, t => t.Name == "Renamed label");
                    var confirm = window.Dispatcher.InvokeAsync(() =>
                    {
                        var nested = Assert.IsType<Grid>(root.Children[2]);
                        Click(Find<Button>(nested, "Delete", content: true));
                    }, DispatcherPriority.ApplicationIdle);
                    Click(Find<Button>(overlay, "Delete Renamed label"));
                    await confirm.Task;
                    await Wait(() => !Visuals(overlay).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Rename Renamed label"));
                    Assert.Same(overlay, root.Children[1]);
                    Assert.DoesNotContain(await QuickClip.Library.PersonalLibrary.Current.ReadTagDefinitionsAsync(default), t => t.Name == "Renamed label");
                    Click(Find<Button>(overlay, "Close", content: true));
                }
                catch (Exception ex)
                {
                    error = ex;
                    window.Close();
                }
            }, DispatcherPriority.ApplicationIdle);
            Click(manage);
            await interaction.Task.Unwrap();
            Assert.Null(error);
            await Wait(() => root.Children.Count == 1);
        }
        finally
        {
            await view.CloseAsync();
            if (window.IsVisible) window.Close();
            settings.ClipsFolder = originalFolder;
            settings.EnableLibraryIndex = originalIndex;
            fixture.Delete(recursive: true);
        }

        static async Task Wait(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!ready())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("Label UI did not reach the expected state.");
                await Task.Delay(30);
            }
        }
        static T Find<T>(DependencyObject node, string label, bool content = false) where T : FrameworkElement =>
            Visuals(node).OfType<T>().First(e => content ? e is Button b && Equals(b.Content, label) :
                System.Windows.Automation.AutomationProperties.GetName(e) == label);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static IEnumerable<DependencyObject> Visuals(DependencyObject node)
        {
            yield return node;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var child in Visuals(VisualTreeHelper.GetChild(node, i))) yield return child;
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
    public Task Hud_preview_uses_a_layered_nonactivating_window_without_capture_affinity() => OnDispatcher(async () =>
    {
        var display = Assert.Single(ClipNotificationOverlay.Displays(), item => item.Primary);
        using var overlay = new ClipNotificationOverlay();
        overlay.ShowRecordingIndicator(display.Device, OverlayAnchor.TopRight, MicrophoneIndicatorState.Active);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(overlay.IsVisible, overlay.IndicatorFailure);
        var styles = OverlayWindowStyles(overlay.WindowHandle);
        const int required = 0x00080000 | 0x08000000 | 0x20; // layered, no-activate, transparent
        Assert.Equal(required, styles & required);
        Assert.True(ReadAffinity(overlay.WindowHandle, out uint affinity));
        Assert.Equal(0u, affinity);
        output.WriteLine("Layered, transparent input, no activation, ordinary capture affinity.");
        overlay.Hide();
        Assert.False(overlay.IsVisible);
    });

    [OptInFact("QUICKCLIP_TEST_SHARING_WINDOWS")]
    public Task Sharing_profile_modal_adds_a_default_and_uses_the_persisted_limit() => OnDispatcher(async () =>
    {
        var profile = AppSettings.Current;
        var oldProfiles = profile.ShareProfiles;
        var oldDefault = profile.DefaultShareProfile;
        double oldSize = profile.ShareSizeMB;
        bool oldSaveDisabled = AppSettings.SaveDisabled;
        var root = new Grid();
        var window = new Window
        {
            Content = root, Width = 630, Height = 480, Left = -16000, Top = -16000,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        new ThemeResources().Apply(window.Resources, ThemeChoice.Light, overrideFluent: false);
        try
        {
            AppSettings.SaveDisabled = false;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timeout.Tick += (_, _) => { timeout.Stop(); window.Close(); };
            timeout.Start();
            Exception? interactionError = null;
            var interaction = window.Dispatcher.InvokeAsync(() =>
            {
                var outer = Buttons(root);
                var save = Assert.Single(outer, button => Equals(button.Content, "Save"));
                var add = Assert.Single(outer, button => Equals(button.Content, "Add…"));
                var edit = window.Dispatcher.InvokeAsync(() =>
                {
                    var inner = root.Children.OfType<Grid>().Last();
                    try
                    {
                        var fields = Visuals(inner).OfType<TextBox>().ToArray();
                        Assert.Equal(2, fields.Length);
                        fields[0].Text = "Fixture destination";
                        fields[1].Text = "64";
                        Click(Assert.Single(Buttons(inner), button => Equals(button.Content, "Apply")));
                    }
                    catch (Exception ex)
                    {
                        interactionError = ex;
                        Click(Assert.Single(Buttons(inner), button => Equals(button.Content, "Cancel")));
                    }
                }, DispatcherPriority.ApplicationIdle);
                Click(add);
                edit.Task.GetAwaiter().GetResult();
                Click(Assert.Single(outer, button => Equals(button.Content, "Default")));
                Click(save);
            }, DispatcherPriority.ApplicationIdle);
            SharingDialogs.Manage(window);
            timeout.Stop();
            await interaction.Task;
            Assert.Null(interactionError);
            var persisted = AppSettings.Parse(File.ReadAllText(AppSettings.FilePath));
            Assert.Equal("Fixture destination", SharingProfiles.Default(persisted).Name);
            Assert.Equal(64, persisted.ShareSizeMB);
        }
        finally
        {
            profile.ShareProfiles = oldProfiles;
            profile.DefaultShareProfile = oldDefault;
            profile.ShareSizeMB = oldSize;
            profile.TrySave();
            AppSettings.SaveDisabled = oldSaveDisabled;
            window.Close();
        }

        static Button[] Buttons(DependencyObject root) => Visuals(root).OfType<Button>().ToArray();
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static IEnumerable<DependencyObject> Visuals(DependencyObject node)
        {
            yield return node;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var child in Visuals(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
    });

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    private static int OverlayWindowStyles(IntPtr hwnd) => GetWindowLong(hwnd, -20);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowDisplayAffinity")]
    private static extern bool ReadAffinity(IntPtr hwnd, out uint affinity);

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

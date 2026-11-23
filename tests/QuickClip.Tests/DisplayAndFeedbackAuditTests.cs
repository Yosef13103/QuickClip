using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickClip.Recording;
using QuickClip.Shell;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class DisplayAndFeedbackAuditTests
{
    private static readonly MonitorInfo A = new(@"\\.\DISPLAY1", "Fixture monitor", 0, 0, 1920, 1080) { CaptureIdentity = "physical-a" };
    private static readonly MonitorInfo B = new(@"\\.\DISPLAY2", "Fixture monitor", 1920, 0, 1920, 1080) { CaptureIdentity = "physical-b" };
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Start_requires_an_unambiguous_verified_target_permission_and_current_master_consent()
    {
        var settings = new AppSettings { RecordingEnabled = true };
        int revision = settings.RecordingDisableRevision;
        Assert.Same(A, AppHost.ResolveManualTarget(settings, [A]));
        Assert.False(AppHost.CanRequestManualDisplay(settings, A, revision));

        CaptureSourcePolicy.SetDisplayPermission(settings, A.CaptureIdentity!, true);
        Assert.True(AppHost.CanRequestManualDisplay(settings, AppHost.ResolveManualTarget(settings, [A]), revision));
        Assert.Null(AppHost.ResolveManualTarget(settings, [A, B]));
        Assert.Null(AppHost.ResolveManualTarget(settings, [A with { CaptureIdentity = null }]));
        Assert.Null(AppHost.ResolveManualTarget(settings, []));
        Assert.Equal("", settings.RecordMonitor);
        Assert.Null(settings.RecordMonitorIdentity);

        settings.RecordMonitorIdentity = A.CaptureIdentity;
        Assert.Null(AppHost.ResolveManualTarget(settings, [A])); // Partial saved preference.
        Assert.True(CaptureSourcePolicy.TrySelectManualTarget(settings, A, _ => true));
        Assert.Same(A, AppHost.ResolveManualTarget(settings, [B, A]));
        Assert.Null(AppHost.ResolveManualTarget(settings, [A with { CaptureIdentity = B.CaptureIdentity }]));
        Assert.Null(AppHost.ResolveManualTarget(settings, [B]));
        Assert.Null(AppHost.ResolveManualTarget(settings, [A with { DeviceName = B.DeviceName }]));
        settings.RecordMonitorIdentity = null;
        Assert.Null(AppHost.ResolveManualTarget(settings, [A])); // Legacy GDI name only.

        settings.RecordingEnabled = false;
        Assert.False(AppHost.CanRequestManualDisplay(settings, A, revision));
        settings.RecordingEnabled = true;
        Assert.False(AppHost.CanRequestManualDisplay(settings, A, revision));
        CaptureSourcePolicy.SetDisplayPermission(settings, A.CaptureIdentity!, false);
        settings.AllowRecordingDisplaysByDefault = true;
        Assert.False(AppHost.CanRequestManualDisplay(settings, A, settings.RecordingDisableRevision));
    }

    [Fact]
    public Task Refreshed_display_choices_enable_start_only_for_a_safe_target() => OnDispatcher(() =>
    {
        if (!AppProfile.IsPersonal) return Task.CompletedTask;
        var settings = AppSettings.Current;
        var before = DisplaySettings(settings);
        try
        {
            SetFixtureDisplays(settings);
            settings.RecordingEnabled = true;
            var view = new SettingsView();
            RefreshChoices(view, A);
            Assert.True(view.ManualDesktopButton.IsEnabled);
            Assert.False(view.ManualDesktopButton.IsChecked);
            RefreshChoices(view, A with { }); // New instances returned by Refresh displays.
            Assert.True(view.ManualDesktopButton.IsEnabled);
            Assert.Equal("", settings.RecordMonitor);
            Assert.Null(settings.RecordMonitorIdentity);

            RefreshChoices(view, A, B);
            Assert.False(view.ManualDesktopButton.IsEnabled);
            RefreshChoices(view, A with { CaptureIdentity = null });
            Assert.False(view.ManualDesktopButton.IsEnabled);
            RefreshChoices(view, B);
            Assert.False(view.ManualDesktopButton.IsEnabled);
            settings.RecordMonitor = A.DeviceName;
            settings.RecordMonitorIdentity = "stale";
            RefreshChoices(view, A);
            Assert.False(view.ManualDesktopButton.IsEnabled);
            Assert.Equal(Visibility.Visible, view.SelectSingleDisplayButton.Visibility);
            settings.RecordMonitorIdentity = A.CaptureIdentity;
            RefreshChoices(view, A, B);
            Assert.True(view.ManualDesktopButton.IsEnabled);
            settings.RecordingEnabled = false;
            RefreshChoices(view, A);
            Assert.False(view.ManualDesktopButton.IsEnabled);
        }
        finally { RestoreDisplaySettings(settings, before); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Choosing_B_changes_the_default_during_A_without_changing_its_session_or_permissions(bool allowB) =>
        OnDispatcher(() =>
        {
            if (!AppProfile.IsPersonal) return Task.CompletedTask;
            var settings = AppSettings.Current;
            var before = DisplaySettings(settings);
            try
            {
                SetFixtureDisplays(settings);
                settings.RecordingEnabled = true;
                CaptureSourcePolicy.SetDisplayPermission(settings, B.CaptureIdentity!, allowB);
                Assert.True(CaptureSourcePolicy.TrySelectManualTarget(settings, A, _ => true));
                var permissions = (settings.AllowedRecordingDisplays, settings.ExcludedGameDisplays);
                var starts = new List<CaptureActivation>();
                int stops = 0;
                var session = new RecordingLifecycle(activation => { starts.Add(activation); return null; }, () => stops++);
                void Reconcile() => session.Reconcile(settings.RecordingEnabled, A.CaptureIdentity, null,
                    [A, B], _ => null, id => CaptureSourcePolicy.AllowsRecordingDisplay(settings, id));
                Reconcile();

                var view = new SettingsView();
                RefreshChoices(view, A, B);
                var chooseB = CardControl<RadioButton>(view, B);
                chooseB.IsChecked = true;
                chooseB.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Reconcile();

                Assert.Equal(B.DeviceName, settings.RecordMonitor);
                Assert.Equal(B.CaptureIdentity, settings.RecordMonitorIdentity);
                // A separate crop-preview selection must not overwrite the
                // explicit, serialized manual default (including after reload).
                var persisted = AppSettings.Parse(settings.ToJson());
                Assert.Equal(B.DeviceName, persisted.RecordMonitor);
                Assert.Equal(B.CaptureIdentity, persisted.RecordMonitorIdentity);
                view.MonitorCombo.SelectedIndex = 0; // Preview display A, not default B.
                Assert.Equal(B.DeviceName, settings.RecordMonitor);
                Assert.Equal(B.CaptureIdentity, settings.RecordMonitorIdentity);
                RefreshChoices(view, A, B);
                Assert.True(CardControl<RadioButton>(view, B).IsChecked);
                Assert.Same(permissions.Item1, settings.AllowedRecordingDisplays);
                Assert.Same(permissions.Item2, settings.ExcludedGameDisplays);
                Assert.True(CaptureSourcePolicy.AllowsRecordingDisplay(settings, A.CaptureIdentity!));
                Assert.Equal(allowB, AppHost.CanRequestManualDisplay(settings,
                    AppHost.ResolveManualTarget(settings, [A, B]), settings.RecordingDisableRevision));
                Assert.Equal(allowB ? Visibility.Collapsed : Visibility.Visible, view.AllowTargetButton.Visibility);
                Assert.Equal(new CaptureDisplay(A.DeviceName, A.CaptureIdentity!), Assert.Single(starts).Display);
                Assert.Equal(0, stops);
                Assert.Equal(RecordingMode.ManualDesktop, session.Mode);

                var state = new RecorderState(true, null, "Desktop recording · Fixture monitor", [], [])
                    { Mode = RecordingMode.ManualDesktop, ReadyEpoch = 7 };
                var ready = new ReadyCapture(7, RecordingMode.ManualDesktop, A.DeviceName, state.Summary, 0, []);
                Assert.Contains("Display 1", SettingsView.ManualSessionHint(state, ready));
                Assert.DoesNotContain("Display 2", SettingsView.ManualSessionHint(state, ready));
            }
            finally { RestoreDisplaySettings(settings, before); }
            return Task.CompletedTask;
        });

    [OptInFact("QUICKCLIP_TEST_FRONTEND_WINDOWS")]
    public Task Space_and_permission_failure_preserve_keyboard_position_across_display_rebuilds() => OnDispatcher(async () =>
    {
        if (!AppProfile.IsPersonal) return;
        var settings = AppSettings.Current;
        var before = DisplaySettings(settings);
        var view = new SettingsView();
        var window = new Window
        {
            ShowInTaskbar = false, ShowActivated = true, Width = 1100, Height = 750,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000,
            Content = view,
        };
        try
        {
            SetFixtureDisplays(settings); // Keep master off, including through the real Space handler.
            RefreshChoices(view, A, B);
            window.Show();
            window.Activate();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var permission = CardControl<CheckBox>(view, A);
            Assert.True(permission.Focus());
            Assert.Same(permission, Keyboard.FocusedElement);
            Assert.Equal("Allow recording on display 1", UIElementAutomationPeer.CreatePeerForElement(permission).GetName());
            var source = PresentationSource.FromVisual(permission)!;
            permission.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyDownEvent });
            permission.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyUpEvent });
            await WaitForPermissions(view);
            Assert.False(CaptureSourcePolicy.AllowsRecordingDisplay(settings, A.CaptureIdentity!));
            permission = CardControl<CheckBox>(view, A);
            Assert.Same(permission, Keyboard.FocusedElement);
            Assert.False(permission.IsChecked);

            var permissionSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            permission.IsChecked = true;
            var update = SettingsView.SnapshotDisplayPermissionUpdate(permission);
            var pending = view.UpdateDisplayPermissionsAsync(update, change => Task.Run(async () =>
            {
                await permissionSave.Task;
                return Recorder.TryUpdateDisplayPermission(settings, change, _ => false);
            }));
            RecorderRefresh(view);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            permissionSave.SetResult(false);
            Assert.False(await pending);
            permission = CardControl<CheckBox>(view, A);
            Assert.Same(permission, Keyboard.FocusedElement);
            Assert.False(permission.IsChecked);

            RecorderRefresh(view);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            permission = CardControl<CheckBox>(view, A);
            Assert.Same(permission, Keyboard.FocusedElement);
            Assert.True(permission.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            var defaultTarget = CardControl<RadioButton>(view, A);
            Assert.Same(defaultTarget, Keyboard.FocusedElement);
            Assert.Equal("Default for manual capture, display 1", UIElementAutomationPeer.CreatePeerForElement(defaultTarget).GetName());
            RefreshChoices(view, B, A with { DeviceName = @"\\.\DISPLAY3" });
            var preferred = CardControl<RadioButton>(view, A);
            Assert.Same(preferred, Keyboard.FocusedElement);
            Assert.True(preferred.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
            Assert.Same(CardControl<CheckBox>(view, A), Keyboard.FocusedElement);
        }
        finally
        {
            window.Close();
            RestoreDisplaySettings(settings, before);
        }
    });

    [Fact]
    public Task Guided_feedback_has_one_focusable_preview_per_behavior_and_settings_retains_its_actions() => OnDispatcher(() =>
    {
        if (!AppProfile.IsPersonal) return Task.CompletedTask;
        var settings = AppSettings.Current;
        var before = (settings.BasicSettings, settings.GuidedSetupStep);
        try
        {
            var view = new SettingsView();
            settings.GuidedSetupStep = 7;
            view.BeginGuidedSetup();
            var guide = VisibleButtons(view.NotificationsCard);
            AssertPreview(guide, "Preview clip saved");
            AssertPreview(guide, "Preview recording icons");
            AssertPreview(guide, "Preview status banner");
            Assert.DoesNotContain(guide, button => Equals(button.Content, "Preview saved notification"));
            Assert.Equal(Visibility.Visible, view.FeedbackVisualGuide.Visibility);
            Assert.Equal(Visibility.Visible, view.GameIndicatorStatus.Visibility);
            Assert.Equal("Finishing setup enables game capture.", view.GuideCompletionNotice.Text);
            view.GameIndicatorCheck.IsChecked = false;
            Call(view, "UpdateFeedbackVisibility");
            Assert.Equal(Visibility.Collapsed, view.FeedbackRecordingStatusExample.Visibility);
            Assert.DoesNotContain("Status icons use", view.FeedbackVisualGuideHint.Text);
            view.GameIndicatorCheck.IsChecked = true;
            Call(view, "UpdateFeedbackVisibility");
            Assert.Equal(Visibility.Visible, view.FeedbackRecordingStatusExample.Visibility);
            Assert.Contains("Status icons use", view.FeedbackVisualGuideHint.Text);
            Assert.Equal(2, view.FeedbackIndicatorIcons.Children.Count);
            view.MicrophoneIndicatorCheck.IsChecked = false;
            Call(view, "UpdateFeedbackVisibility");
            Assert.Equal(Visibility.Collapsed, view.FeedbackIndicatorIcons.Children[1].Visibility);
            Assert.Contains("hidden", view.FeedbackMicLegend.Text);
            view.MicrophoneIndicatorCheck.IsChecked = true;
            Call(view, "UpdateFeedbackVisibility");
            Assert.Equal(Visibility.Visible, view.FeedbackIndicatorIcons.Children[1].Visibility);
            Assert.Contains("mic red", view.FeedbackMicLegend.Text);

            Call(view, "ExitGuide");
            view.NotificationsSection.IsChecked = true;
            settings.BasicSettings = true;
            Call(view, "UpdateSettingsExperience");
            var basic = VisibleButtons(view.NotificationsCard);
            AssertPreview(basic, "Preview clip saved");
            AssertPreview(basic, "Preview recording icons");
            AssertPreview(basic, "Preview status banner");
            Assert.Equal(Visibility.Collapsed, view.FeedbackVisualGuide.Visibility);
            settings.BasicSettings = false;
            Call(view, "UpdateSettingsExperience");
            var advanced = VisibleButtons(view.NotificationsCard);
            AssertPreview(advanced, "Preview recording icons");
            Assert.DoesNotContain(advanced, button => Equals(button.Content, "Preview clip saved"));
        }
        finally { (settings.BasicSettings, settings.GuidedSetupStep) = before; }
        return Task.CompletedTask;
    });

    private static void SetFixtureDisplays(AppSettings settings)
    {
        settings.RecordingEnabled = false;
        settings.AllowRecordingDisplaysByDefault = false;
        settings.AllowedRecordingDisplays = [A.CaptureIdentity!];
        settings.ExcludedGameDisplays = [];
        settings.RecordMonitor = "";
        settings.RecordMonitorIdentity = null;
    }

    private static (bool, bool, string[], string[], string, string?) DisplaySettings(AppSettings settings) =>
        (settings.RecordingEnabled, settings.AllowRecordingDisplaysByDefault,
            settings.AllowedRecordingDisplays, settings.ExcludedGameDisplays, settings.RecordMonitor, settings.RecordMonitorIdentity);

    private static void RestoreDisplaySettings(AppSettings settings, (bool, bool, string[], string[], string, string?) prior) =>
        (settings.RecordingEnabled, settings.AllowRecordingDisplaysByDefault, settings.AllowedRecordingDisplays,
            settings.ExcludedGameDisplays, settings.RecordMonitor, settings.RecordMonitorIdentity) = prior;

    private static void RefreshChoices(SettingsView view, params MonitorInfo[] monitors)
    {
        view.LoadDisplayChoices(AppSettings.Current, monitors);
        Call(view, "ShowGameWindowState");
    }

    private static T CardControl<T>(SettingsView view, MonitorInfo monitor) where T : ToggleButton =>
        view.DisplayCardsPanel.Children.OfType<Border>().SelectMany(card => ((StackPanel)card.Child).Children.OfType<T>())
            .Single(control => Equals(control.Tag, monitor.CaptureIdentity));

    private static void RecorderRefresh(SettingsView view) =>
        Call(view, "OnRecorderState", new RecorderState(false, null, "Unrelated update", [], []));

    private static async Task WaitForPermissions(SettingsView view)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!view.CapturePermissionCard.IsEnabled)
            await Task.Delay(10, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static Button[] VisibleButtons(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible)
            .SelectMany(child => child is Button button ? new[] { button } : VisibleButtons(child))
            .ToArray();

    private static void AssertPreview(Button[] buttons, string label)
    {
        var button = Assert.Single(buttons, item => Equals(label, item.Content));
        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);
        Assert.Equal(label, UIElementAutomationPeer.CreatePeerForElement(button).GetName());
    }

    private static void Call(SettingsView view, string name, params object[] arguments) =>
        typeof(SettingsView).GetMethod(name, InstancePrivate)!.Invoke(view, arguments);

    private static async Task OnDispatcher(Func<Task> run)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await run();
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

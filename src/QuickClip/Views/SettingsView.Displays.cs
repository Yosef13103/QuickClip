using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class SettingsView
{
    private bool _refreshingDisplays;
    private FrameworkElement? _feedbackMicExample;

    private void InitializeFeedbackIndicatorExample()
    {
        // Same glyph builder and geometry as the actual on-screen HUD; no overlay window,
        // microphone probe, screen read, or recording is started.
        FeedbackIndicatorIcons.Children.Add(ClipNotificationOverlay.StatusGlyph(
            ClipNotificationOverlay.CameraGlyphGeometry, out _));
        var mic = ClipNotificationOverlay.StatusGlyph(ClipNotificationOverlay.MicrophoneGlyphGeometry, out var stroke);
        stroke.Stroke = Brushes.Red; // Before the selected microphone is verified.
        _feedbackMicExample = mic;
        FeedbackIndicatorIcons.Children.Add(mic);
    }

    // Shared by Settings and onboarding; each card has independent capture permission
    // and a separate manual target. Only existing, verified physical identities are grantable.
    private void RenderDisplayCards()
    {
        if (!AppProfile.IsPersonal || DisplayCardsPanel == null) return;
        var settings = AppSettings.Current;
        var focus = FocusedDisplayControl();
        DisplayCardsPanel.Children.Clear();
        RequireDisplayApprovalButton.Visibility = settings.AllowRecordingDisplaysByDefault
            ? Visibility.Visible : Visibility.Collapsed;

        var selected = AppHost.ResolveManualTarget(settings, _monitors);
        for (int index = 0; index < _monitors.Count; index++)
        {
            var monitor = _monitors[index];
            string number = DisplayNumber(monitor.DeviceName, index + 1);
            bool identifiable = !string.IsNullOrWhiteSpace(monitor.CaptureIdentity);
            bool allowed = identifiable && CaptureSourcePolicy.AllowsRecordingDisplay(settings, monitor.CaptureIdentity!);
            bool isTarget = selected != null && string.Equals(selected.DeviceName, monitor.DeviceName,
                StringComparison.OrdinalIgnoreCase);
            var card = new Border
            {
                Width = 270, Padding = new Thickness(12), Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(isTarget ? 2 : 1),
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            card.SetResourceReference(Border.BorderBrushProperty, isTarget ? "AccentBrush" : "DividerBrush");
            var content = new StackPanel();
            card.Child = content;

            var header = new TextBlock
            {
                Text = $"Display {number} · {(string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Monitor" : monitor.FriendlyName)}",
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            content.Children.Add(header);
            content.Children.Add(new TextBlock
            {
                Text = $"{monitor.Width} × {monitor.Height}{(monitor.IsPrimary ? "  ·  Main display" : "")}",
                Margin = new Thickness(0, 3, 0, 8), TextWrapping = TextWrapping.Wrap,
                Style = (Style)FindResource("Hint")
            });
            content.Children.Add(BuildScreenIllustration(monitor));

            var permission = new CheckBox
            {
                Content = new TextBlock { Text = "Allow recording here", TextWrapping = TextWrapping.Wrap },
                IsChecked = allowed,
                Tag = monitor.CaptureIdentity, IsEnabled = identifiable && !_refreshingDisplays,
                Margin = new Thickness(0, 9, 0, 4), Style = (Style)FindResource("SwitchCheckBox"),
                ToolTip = identifiable
                    ? "Allows game-triggered or deliberate desktop capture on this physical display."
                    : "Windows could not verify this physical display. Recording remains blocked."
            };
            AutomationProperties.SetName(permission, $"Allow recording on display {number}");
            permission.Click += DisplayCardPermission_Click;
            content.Children.Add(permission);

            if (_monitors.Count > 1)
            {
                var target = new RadioButton
                {
                    Content = new TextBlock { Text = "Default for manual capture", TextWrapping = TextWrapping.Wrap },
                    GroupName = "QuickClipManualDisplay",
                    Tag = monitor.CaptureIdentity, IsChecked = isTarget,
                    IsEnabled = identifiable && !_refreshingDisplays,
                    Margin = new Thickness(0, 5, 0, 0),
                    ToolTip = "Only sets a preferred screen for a future manual session. Does not enable capture or grant permission."
                };
                AutomationProperties.SetName(target, $"Default for manual capture, display {number}");
                target.Click += DisplayCardTarget_Click;
                content.Children.Add(target);
            }
            else if (isTarget)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "Default for manual capture.",
                    Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
                    Style = (Style)FindResource("Hint")
                });
            }
            else if (!identifiable)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "Identity unavailable — reconnect this display.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)(TryFindResource("WarningBrush") ?? Brushes.Goldenrod)
                });
            }
            DisplayCardsPanel.Children.Add(card);
        }
        ResizeDisplayCards();

        if (_monitors.Count == 0)
        {
            DisplayCardsPanel.Children.Add(new TextBlock
            {
                Text = "No recording-capable displays found. Reconnect a monitor, then refresh.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)(TryFindResource("WarningBrush") ?? Brushes.Goldenrod)
            });
        }

        DisplayPrivacyHint.Text = _monitors.Count > 1
            ? "Only gaming display? Leave other screens off to avoid capturing chats or unrelated windows. Allow both if games move between screens."
            : "An allowed screen may include messages and other windows. Choose what you are comfortable recording.";
        bool hasStoredTarget = AppHost.HasManualTargetPreference(settings);
        bool stale = hasStoredTarget && selected == null;
        bool conflict = selected?.CaptureIdentity is { } id && !CaptureSourcePolicy.AllowsRecordingDisplay(settings, id);
        DisplayTargetConflict.Text = stale
            ? "Saved manual target needs confirmation or is disconnected. Select its card again to reconnect safely."
            : conflict
                    ? "Manual target is blocked. Choose an allowed display or explicitly allow this one before starting manual capture."
                    : "";
        AllowTargetButton.Visibility = conflict && hasStoredTarget ? Visibility.Visible : Visibility.Collapsed;
        SelectSingleDisplayButton.Visibility = _monitors.Count == 1 && stale &&
            _monitors[0].CaptureIdentity != null ? Visibility.Visible : Visibility.Collapsed;
        NoManualDefaultButton.Visibility = _monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NoManualDefaultButton.IsChecked = !hasStoredTarget;
        RestoreDisplayFocus(focus);
    }

    private Control? FocusedDisplayControl() =>
        Keyboard.FocusedElement is Control focused && CapturePermissionCard.IsAncestorOf(focused) ? focused : null;

    // WPF can move focus to the window after disabling a focused control.
    // Preserve any new focus the user chose while the operation was pending.
    private bool CanRestoreDisplayFocus(IInputElement? focusWhileDisabled) =>
        Keyboard.FocusedElement == null ||
        ReferenceEquals(Keyboard.FocusedElement, focusWhileDisabled) ||
        ReferenceEquals(Keyboard.FocusedElement, Window.GetWindow(this));

    private void RestoreDisplayFocus(Control? previous)
    {
        if (previous == null || Window.GetWindow(this)?.IsActive != true) return;
        Control? target = previous;
        if (!CapturePermissionCard.IsAncestorOf(previous))
        {
            target = DisplayCardsPanel.Children.OfType<Border>()
                .SelectMany(card => ((StackPanel)card.Child).Children.OfType<ToggleButton>())
                .FirstOrDefault(control => control.GetType() == previous.GetType() &&
                    control.Tag is string identity && previous.Tag is string priorIdentity &&
                    string.Equals(identity, priorIdentity, StringComparison.OrdinalIgnoreCase));
        }
        if (target is { IsVisible: true, IsEnabled: true, Focusable: true })
            target.Focus();
        else if (RefreshDisplaysButton is { IsVisible: true, IsEnabled: true })
            RefreshDisplaysButton.Focus();
    }

    private void ResizeDisplayCards()
    {
        double available = VisualDisplayPanel.ActualWidth;
        if (available <= 0) return;
        foreach (Border card in DisplayCardsPanel.Children.OfType<Border>())
            card.Width = Math.Min(270, Math.Max(155, available - 14));
    }

    internal static string ManualSessionHint(RecorderState state, ReadyCapture? ready)
    {
        const string stop = "Turn it off to return to automatic game capture.";
        if (!state.Running || state.Mode != RecordingMode.ManualDesktop)
            return "Manual capture is requested. " + stop;
        string display = ready is { Mode: RecordingMode.ManualDesktop } && ready.Epoch == state.ReadyEpoch
            ? Recorder.CaptureStatusLabel(state.Mode, null, ready.Device, state.Summary)
            : state.Summary;
        return $"{display}. {stop}";
    }

    private static string DisplayNumber(string device, int fallback)
    {
        var match = System.Text.RegularExpressions.Regex.Match(device, @"DISPLAY(\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : fallback.ToString();
    }

    // Scale each native pixel rectangle into a common 220 × 88 illustration area,
    // without rounding the actual aspect ratio to a guessed 16:9 or 21:9 preset.
    internal static Size ScreenIllustrationSize(MonitorInfo monitor)
    {
        double width = Math.Max(1, monitor.Width);
        double height = Math.Max(1, monitor.Height);
        double scale = Math.Min(220 / width, 88 / height);
        return new Size(width * scale, height * scale);
    }

    private FrameworkElement BuildScreenIllustration(MonitorInfo monitor)
    {
        // Pure illustration, not a screenshot or a simulated desktop: exact native
        // proportions, no capture, no images of private windows, no recurring work.
        var dimensions = ScreenIllustrationSize(monitor);
        var art = new Grid { Width = 230, Height = 112 };
        var screen = new Border
        {
            Width = dimensions.Width, Height = dimensions.Height,
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
        };
        screen.SetResourceReference(Border.BackgroundProperty, "ElevatedSurfaceBrush");
        screen.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        if (dimensions.Width >= 110)
        {
            var resolution = new TextBlock
            {
                Text = $"{monitor.Width} × {monitor.Height}",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            resolution.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            screen.Child = resolution;
        }
        art.Children.Add(screen);
        var stem = new Border
        {
            Width = 5, Height = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10)
        };
        stem.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        art.Children.Add(stem);
        var foot = new Border
        {
            Width = 36, Height = 4, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 7)
        };
        foot.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        art.Children.Add(foot);

        var content = new StackPanel();
        content.Children.Add(new Viewbox
        {
            MaxWidth = 236, Height = 112, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = art
        });
        return content;
    }

    // Snapshot WPF-owned state on the UI thread. The recorder executes the returned
    // mutation on a worker thread, which must never dereference a WPF control.
    internal static Action<AppSettings> SnapshotDisplayPermissionUpdate(CheckBox check)
    {
        check.Dispatcher.VerifyAccess();
        if (check.Tag is not string identity || string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("The display must have a verified identity.", nameof(check));
        bool allow = check.IsChecked == true;
        return settings => CaptureSourcePolicy.SetDisplayPermission(settings, identity, allow);
    }

    private async void DisplayCardPermission_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string identity } check ||
            string.IsNullOrWhiteSpace(identity) || _refreshingDisplays) return;
        await ApplyDisplayPermissionAsync(SnapshotDisplayPermissionUpdate(check));
    }

    private void DisplayCardTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string identity } || _refreshingDisplays) return;
        var display = _monitors.FirstOrDefault(m => string.Equals(m.CaptureIdentity, identity,
            StringComparison.OrdinalIgnoreCase));
        if (display == null) return;
        var settings = AppSettings.Current;
        if (!CaptureSourcePolicy.TrySelectManualTarget(settings, display, s => s.TrySave()))
        {
            GuideError.Text = "Could not save manual display selection.";
            RenderDisplayCards();
            DisplayTargetConflict.Text = "Could not save the default for manual capture. Your previous choice is unchanged.";
            return;
        }
        GuideError.Text = "";
        bool previousLoading = _loading;
        _loading = true;
        try { Select(MonitorCombo, display.DeviceName); }
        finally { _loading = previousLoading; }
        ShowGameWindowState();
        UpdateCropPreview();
    }

    private void NoManualDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingDisplays || _loading || _monitors.Count <= 1)
        {
            RenderDisplayCards();
            return;
        }
        if (!CaptureSourcePolicy.TryClearManualTarget(AppSettings.Current, s => s.TrySave()))
        {
            RenderDisplayCards();
            DisplayTargetConflict.Text = "Could not save the manual capture default.";
            return;
        }
        GuideError.Text = "";
        bool previousLoading = _loading;
        _loading = true;
        try { MonitorCombo.SelectedIndex = -1; }
        finally { _loading = previousLoading; }
        ShowGameWindowState();
        UpdateCropPreview();
    }

    private void SelectSingleDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_monitors.Count == 1 && _monitors[0].CaptureIdentity is { } id)
            DisplayCardTarget_Click(new RadioButton { Tag = id }, e);
    }

    private async void AllowSelectedDisplay_Click(object sender, RoutedEventArgs e)
    {
        var selected = CaptureSourcePolicy.StoredManualTarget(AppSettings.Current, _monitors);
        if (selected?.CaptureIdentity is { } identity)
            await ApplyDisplayPermissionAsync(s => CaptureSourcePolicy.SetDisplayPermission(s, identity, true));
    }

    private async void RequireDisplayApproval_Click(object sender, RoutedEventArgs e)
    {
        var connected = _monitors.Where(m => m.CaptureIdentity != null &&
            CaptureSourcePolicy.AllowsRecordingDisplay(AppSettings.Current, m.CaptureIdentity)).Select(m => m.CaptureIdentity!).ToArray();
        await ApplyDisplayPermissionAsync(s =>
        {
            s.AllowedRecordingDisplays = [.. connected.Distinct(StringComparer.OrdinalIgnoreCase)];
            s.AllowRecordingDisplaysByDefault = false;
        });
    }

    private async void RefreshDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingDisplays || _loading) return;
        _refreshingDisplays = true;
        var focus = FocusedDisplayControl();
        RefreshDisplaysButton.IsEnabled = false;
        var focusWhileDisabled = Keyboard.FocusedElement;
        try
        {
            LoadDisplayChoices(AppSettings.Current, await Task.Run(Monitors.List));
            RefreshAreas(AppSettings.Current.RecordAspect);
            UpdateCropPreview();
        }
        finally
        {
            _refreshingDisplays = false;
            RefreshDisplaysButton.IsEnabled = true;
            bool restoreFocus = CanRestoreDisplayFocus(focusWhileDisabled);
            ShowGameWindowState();
            if (_guideActive) UpdateGuideHeader();
            if (restoreFocus) RestoreDisplayFocus(focus);
        }
    }

    internal void LoadDisplayChoices(AppSettings settings, IReadOnlyList<MonitorInfo> monitors)
    {
        _monitors = monitors;
        var selected = AppProfile.IsPersonal
            ? AppHost.ResolveManualTarget(settings, monitors)
            : CaptureSourcePolicy.SelectMonitor(monitors, settings.RecordMonitor, requireSelected: false);
        // The crop preview can still use the main display when no preference was saved.
        if (selected == null && !AppHost.HasManualTargetPreference(settings))
            selected = CaptureSourcePolicy.SelectMonitor(monitors, "", requireSelected: false);
        bool old = _loading;
        _loading = true;
        try
        {
            MonitorCombo.ItemsSource = monitors.Select(m => new Choice(m.Label, m.DeviceName)).ToList();
            if (selected != null) Select(MonitorCombo, selected.DeviceName);
            else MonitorCombo.SelectedIndex = -1;
        }
        finally { _loading = old; }
    }
}

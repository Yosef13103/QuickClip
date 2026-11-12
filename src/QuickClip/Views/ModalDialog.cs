using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickClip.Views;

internal static class ModalDialog
{
    internal static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        if (owner is not { IsVisible: true, Content: Panel })
            return MessageBox.Show(message, title, buttons, image, defaultResult);
        bool confirm = buttons != MessageBoxButton.OK;
        bool ok = Run(owner, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? "Yes" : "OK",
            confirm ? buttons == MessageBoxButton.YesNo ? "No" : "Cancel" : null,
            safeDefault: confirm && defaultResult is not (MessageBoxResult.Yes or MessageBoxResult.OK));
        return buttons switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => ok ? MessageBoxResult.Yes : MessageBoxResult.No,
            MessageBoxButton.OKCancel => ok ? MessageBoxResult.OK : MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
    }

    internal static bool ConfirmDelete(Window? owner, string title, string paths)
        => ConfirmAction(owner, paths, title, "Delete", "Cancel", destructive: true);

    internal static bool ConfirmAction(Window? owner, string message, string title, string accept, string cancel, bool destructive)
    {
        if (owner is not { IsVisible: true, Content: Panel })
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        return Run(owner, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            accept, cancel, safeDefault: true, destructive: destructive);
    }

    internal static string? Ask(Window? owner, string prompt, string initial, string action, bool allowEmpty)
    {
        if (owner is not { IsVisible: true, Content: Panel }) return null;
        var input = new TextBox { Text = initial, MinWidth = 240, Padding = new Thickness(8) };
        AutomationProperties.SetName(input, prompt);
        input.SelectAll();
        bool ok = Run(owner, prompt, input, action, "Cancel", initialFocus: input);
        string text = input.Text.Trim();
        return ok && (allowEmpty || text.Length > 0) ? text : null;
    }

    private static bool Run(Window owner, string title, FrameworkElement content, string accept, string? cancel,
        bool safeDefault = false, bool destructive = false, IInputElement? initialFocus = null)
    {
        var host = (Panel)owner.Content;
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) };
        Panel.SetZIndex(overlay, int.MaxValue);
        if (host is Grid grid)
        {
            Grid.SetColumnSpan(overlay, Math.Max(1, grid.ColumnDefinitions.Count));
            Grid.SetRowSpan(overlay, Math.Max(1, grid.RowDefinitions.Count));
        }
        KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(overlay, KeyboardNavigationMode.Cycle);

        var box = new Border
        {
            Width = Math.Min(480, Math.Max(280, owner.ActualWidth - 64)),
            MaxHeight = Math.Max(220, owner.ActualHeight - 40),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(12), Padding = new Thickness(22),
            BorderThickness = new Thickness(1),
            BorderBrush = owner.TryFindResource("PanelBrush") as Brush ?? Brushes.Gray,
            Background = owner.TryFindResource("CardBrush") as Brush ?? SystemColors.WindowBrush,
        };
        AutomationProperties.SetName(box, title);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(70, box.MaxHeight - 120), Margin = new Thickness(0, 12, 0, 20),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var primary = new Button { Content = accept, MinWidth = 84, Padding = new Thickness(12, 6, 12, 6) };
        var secondary = new Button { Content = cancel ?? "", MinWidth = 84, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        if (destructive && owner.TryFindResource("DangerButtonStyle") is Style danger)
            primary.Style = danger;
        if (cancel != null) buttons.Children.Add(secondary);
        buttons.Children.Add(primary);
        body.Children.Add(buttons);
        box.Child = body;
        overlay.Children.Add(box);

        var frame = new DispatcherFrame();
        bool accepted = false;
        primary.Click += (_, _) => { accepted = true; frame.Continue = false; };
        secondary.Click += (_, _) => frame.Continue = false;
        void Keys(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape)
            {
                frame.Continue = false;
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && Keyboard.FocusedElement is not Button)
            {
                accepted = !safeDefault;
                frame.Continue = false;
                args.Handled = true;
            }
        }

        var priorFocus = Keyboard.FocusedElement;
        // HWND children sit above WPF visuals. Hide them while a dialog owns the surface.
        var airspace = FindNativeHosts(host).Where(native => native.IsVisible).ToArray();
        foreach (var native in airspace) native.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
        host.Children.Add(overlay);
        owner.PreviewKeyDown += Keys;
        EventHandler close = (_, _) => frame.Continue = false;
        owner.Closed += close;
        try
        {
            Keyboard.Focus(initialFocus ?? (safeDefault && cancel != null ? secondary : primary));
            Dispatcher.PushFrame(frame);
            return accepted;
        }
        finally
        {
            owner.Closed -= close;
            owner.PreviewKeyDown -= Keys;
            host.Children.Remove(overlay);
            foreach (var native in airspace) native.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            if (priorFocus != null && owner.IsVisible) Keyboard.Focus(priorFocus);
        }
    }

    private static IEnumerable<HwndHost> FindNativeHosts(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is HwndHost native) yield return native;
            else
                foreach (var nested in FindNativeHosts(child)) yield return nested;
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using QuickClip.Shell;

namespace QuickClip;

public partial class MainWindow
{
    private BackgroundActivity.Entry[] _visibleActivities = [];

    private static string ActivityLabel(BackgroundActivity.Entry entry) =>
        entry.Fraction is double value ? $"{entry.Label} · {value:P0}" : entry.Label;

    private void RefreshBackgroundActivity()
    {
        if (!AppProfile.IsPersonal || ActivityButton == null) return;
        var tasks = BackgroundActivity.Snapshot();
        bool visible = tasks.Length > 0;
        // No ongoing busy animation when idle, minimized, hidden or fullscreen.
        bool animate = visible && IsVisible && WindowState != WindowState.Minimized &&
            StatusBar.Visibility == Visibility.Visible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        ActivityButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ActivityProgress.IsIndeterminate = animate && tasks[0].Fraction == null;
        ActivityProgress.Visibility = visible && (tasks[0].Fraction.HasValue || animate)
            ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            ActivityButton.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            _visibleActivities = [];
            return;
        }
        ActivityProgress.Value = tasks[0].Fraction ?? 0;
        if (_visibleActivities.SequenceEqual(tasks)) return;
        _visibleActivities = tasks;
        ActivityText.Text = ActivityLabel(tasks[0]) + (tasks.Length > 1 ? $"  +{tasks.Length - 1} more" : "");
        ActivityButton.ToolTip = string.Join("\n", tasks.Select(ActivityLabel));
        System.Windows.Automation.AutomationProperties.SetName(ActivityButton, "Background activity: " + ActivityText.Text);
        if (ActivityButton.ContextMenu is { IsOpen: true } menu) PopulateActivityMenu(menu);
    }

    private void PopulateActivityMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var task in _visibleActivities)
            menu.Items.Add(new MenuItem { Header = ActivityLabel(task), IsEnabled = false });
    }

    private void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleActivities.Length == 0) return;
        var menu = ActivityButton.ContextMenu ??= new ContextMenu { PlacementTarget = ActivityButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        PopulateActivityMenu(menu);
        menu.IsOpen = true;
    }
}

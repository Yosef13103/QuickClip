using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickClip.Views;

public partial class SettingsView
{
    private bool _updatingScale;

    private void InitializeInterfaceSettings()
    {
        if (!AppProfile.IsPersonal)
        {
            InterfaceSettings.Visibility = Visibility.Collapsed;
            return;
        }

        SizeChanged += (_, _) => UpdateSectionsWidth();
        ShowScale((AppSettings.Current.InterfaceScale ?? new()).Normalize());
    }

    private void UpdateSectionsWidth()
    {
        var scale = (AppSettings.Current.InterfaceScale ?? new()).Normalize();
        double expanded = 176 * Math.Max(scale.Text, scale.Controls) / 100d;
        bool compact = ActualWidth < 625 + expanded;
        var width = new GridLength(compact ? 64 * scale.Controls / 100d : expanded);
        if (SectionsColumn.Width != width) SectionsColumn.Width = width;
        SettingsSections.Width = width.Value - 16;
        if (!Equals(SettingsSections.Tag, compact)) SettingsSections.Tag = compact;
    }

    private void SectionKeys(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not RadioButton current || current.Parent != SettingsSections) return;
        int index = SettingsSections.Children.IndexOf(current);
        int last = SettingsSections.Children.Count - 1;
        int next = e.Key switch
        {
            Key.Up or Key.Left => index == 0 ? last : index - 1,
            Key.Down or Key.Right => index == last ? 0 : index + 1,
            Key.Home => 0,
            Key.End => last,
            _ => -1,
        };
        if (next < 0) return;
        var section = (RadioButton)SettingsSections.Children[next];
        section.Focus();
        section.IsChecked = true;
        section.BringIntoView();
        e.Handled = true;
    }

    private void Scale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScale || ControlScale == null || sender is not ComboBox { SelectedItem: int value }) return;
        var scale = (AppSettings.Current.InterfaceScale ?? new()).Normalize();
        SaveScale(sender == CombinedScale ? new(value, value) :
            sender == TextScale ? scale with { Text = value } : scale with { Controls = value });
    }

    private void ResetScale_Click(object sender, RoutedEventArgs e) => SaveScale(new());

    private void SaveScale(InterfaceScale scale)
    {
        AppSettings.Current.InterfaceScale = scale;
        if (Application.Current is { } app) scale.Apply(app.Resources);
        AppSettings.Current.Save();
        ShowScale(scale);
        UpdateSectionsWidth();
    }

    private void ShowScale(InterfaceScale scale)
    {
        _updatingScale = true;
        foreach (var picker in new[] { CombinedScale, TextScale, ControlScale })
            picker.ItemsSource ??= new[] { 90, 100, 110, 115, 125 };
        CombinedScale.SelectedItem = scale.Text == scale.Controls ? scale.Text : null;
        TextScale.SelectedItem = scale.Text;
        ControlScale.SelectedItem = scale.Controls;
        _updatingScale = false;
    }
}

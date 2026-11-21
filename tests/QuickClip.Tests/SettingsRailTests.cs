using System.Windows;
using System.Windows.Controls;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class SettingsRailTests
{
    [Fact]
    public async Task Child_navigation_has_all_sections_and_icon_above_label()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new SettingsView();
                view.Measure(new Size(1050, 720));
                view.Arrange(new Rect(new Size(1050, 720)));
                view.UpdateLayout();

                var sections = view.SettingsSections.Children.OfType<RadioButton>().ToArray();
                Assert.Equal(9, sections.Length);
                Assert.Single(sections, section => Equals(section.Content, "Notifications"));
                Assert.Equal("Recording", sections[0].Content);
                Assert.Equal("Diagnostics", sections[^1].Content);
                view.GamesSection.IsChecked = true;
                Assert.Equal("Sources", view.GamesSection.Content);
                Assert.Equal(Visibility.Visible, view.GamesPage.Visibility);
                Assert.Equal(Visibility.Collapsed, view.GeneralPage.Visibility);
                Assert.Equal(Visibility.Collapsed, view.CompanionsCard.Visibility);
                Assert.Single(view.GamesPage.Applications.Items);
                Assert.Equal(AppSettings.Current.DiscordCaptureEnabled,
                    (bool)view.GamesPage.Applications.Items[0]!.GetType().GetProperty("Enabled")!.GetValue(view.GamesPage.Applications.Items[0])!);
                if (Environment.OSVersion.Version.Build < 20348)
                {
                    Assert.Equal(Visibility.Visible, view.DiscordBorderNotice.Visibility);
                    Assert.NotNull(view.DiscordBorderNotice.GetValue(System.Windows.Controls.TextBlock.ForegroundProperty));
                }
                else
                {
                    Assert.Equal(Visibility.Collapsed, view.DiscordBorderNotice.Visibility);
                    Assert.Equal(Visibility.Collapsed, view.DiscordMirrorHint.Visibility);
                    Assert.Equal(Visibility.Collapsed, view.DiscordMirrorCheck.Visibility);
                }
                Assert.Equal(Visibility.Collapsed, view.GeneralCard.Visibility);
                Assert.Equal(AppSettings.Current.DiscordCaptureEnabled, view.DiscordEnableCheck.IsChecked == true);
                Assert.Equal(AppSettings.Current.DiscordCaptureMode.ToString(),
                    ((ComboBoxItem)view.DiscordModeCombo.SelectedItem).Tag?.ToString());
                view.NotificationsSection.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, view.CompanionsCard.Visibility);
                view.NotificationsSection.IsChecked = true;
                Assert.Equal(Visibility.Visible, view.NotificationsCard.Visibility);
                Assert.Equal(Visibility.Collapsed, view.GeneralCard.Visibility);
                view.GeneralSection.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, view.NotificationsCard.Visibility);
                Assert.Equal(Visibility.Visible, view.GeneralCard.Visibility);
                view.GamesSection.IsChecked = true;
                Assert.Equal(Visibility.Visible, view.GamesPage.Visibility);
                Assert.Equal(Visibility.Collapsed, view.CompanionsCard.Visibility);
                view.RecordingSection.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, view.GamesPage.Visibility);

                foreach (var section in sections)
                {
                    section.ApplyTemplate();
                    Assert.IsType<TextBlock>(section.Template.FindName("SectionGlyph", section));
                    Assert.IsType<TextBlock>(section.Template.FindName("SectionLabel", section));
                    Assert.True(section.IsEnabled);
                }
                Assert.True(view.SectionsColumn.Width.Value > 0);
                Assert.Equal(new Thickness(0), view.SettingsSidebar.BorderThickness);
                var toggle = new CheckBox { Style = (Style)view.FindResource("SwitchCheckBox"), Content = "Test" };
                toggle.ApplyTemplate();
                var track = Assert.IsType<QuickClip.Controls.SwitchVisual>(toggle.Template.FindName("SwitchTrack", toggle));
                Assert.True(toggle.Focusable);
                // Icon-only buttons must not turn context-menu text into boxes.
                var menuStyle = Assert.IsType<Style>(view.FindResource(typeof(ContextMenu)));
                Assert.Contains(menuStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == Control.FontFamilyProperty &&
                        Equals(setter.Value, new System.Windows.Media.FontFamily("Segoe UI")));
                var tooltipStyle = Assert.IsType<Style>(view.FindResource(typeof(ToolTip)));
                Assert.Contains(tooltipStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == Control.FontFamilyProperty &&
                        Equals(setter.Value, new System.Windows.Media.FontFamily("Segoe UI")));
                Assert.Equal(false, track.IsOn);
                toggle.IsChecked = true;
                Assert.Equal(true, track.IsOn);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

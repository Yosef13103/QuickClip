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
                Assert.Equal(8, sections.Length);
                Assert.Equal("Recording", sections[0].Content);
                Assert.Equal("Diagnostics", sections[^1].Content);

                foreach (var section in sections)
                {
                    section.ApplyTemplate();
                    Assert.IsType<TextBlock>(section.Template.FindName("SectionGlyph", section));
                    Assert.IsType<TextBlock>(section.Template.FindName("SectionLabel", section));
                    Assert.True(section.IsEnabled);
                }
                Assert.True(view.SectionsColumn.Width.Value > 0);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

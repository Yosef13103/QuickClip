using System.Windows;

namespace QuickClip.Tests;

public sealed class InterfaceScaleTests
{
    [Fact]
    public void Scaling_preferences_round_trip_and_size_resources_stay_independent()
    {
        var settings = AppSettings.Parse(new AppSettings { InterfaceScale = new(115, 90) }.ToJson());
        var resources = new ResourceDictionary();
        settings.InterfaceScale.Apply(resources);
        Assert.Equal(13.8, Assert.IsType<double>(resources[ScaledSizeExtension.Key(12, false)]), 5);
        Assert.Equal(36, Assert.IsType<double>(resources[ScaledSizeExtension.Key(40, true)]), 5);
        Assert.Equal(new InterfaceScale(), AppSettings.Parse("{}").InterfaceScale);
        Assert.Equal(new InterfaceScale(90, 125), new InterfaceScale(-100, 200).Normalize());
    }
}

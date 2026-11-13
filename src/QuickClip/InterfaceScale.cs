using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace QuickClip;

internal sealed record InterfaceScale(int Text = 100, int Controls = 100)
{
    public const int Minimum = 90;
    public const int Maximum = 125;

    public InterfaceScale Normalize() => new(Math.Clamp(Text, Minimum, Maximum), Math.Clamp(Controls, Minimum, Maximum));

    public void Apply(ResourceDictionary resources)
    {
        var scale = Normalize();
        foreach (double size in new double[] { 10, 11, 11.5, 12, 12.5, 13, 14, 15, 16, 18, 20, 21, 22, 24 })
            resources[ScaledSizeExtension.Key(size, false)] = size * scale.Text / 100;
        foreach (double size in new double[] { 10, 11, 12, 15, 16, 19, 20, 30, 32, 36, 40, 64, 80, 116 })
            resources[ScaledSizeExtension.Key(size, true)] = size * scale.Controls / 100;
        resources["PrimaryRailWidth"] = new GridLength(Math.Max(80 * scale.Text / 100d, 80 * scale.Controls / 100d));
        resources["SaveClipPadding"] = new Thickness(14 * scale.Controls / 100d, 6 * scale.Controls / 100d,
            14 * scale.Controls / 100d, 6 * scale.Controls / 100d);
    }
}

/// <summary>Uses layout properties and live resource lookup, including inside WPF templates.</summary>
public sealed class ScaledSizeExtension(double size) : MarkupExtension
{
    public bool Visual { get; set; }

    internal static string Key(double size, bool visual) =>
        (visual ? "QuickClipVisual" : "QuickClipText") + size.ToString(CultureInfo.InvariantCulture);

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (!AppProfile.IsPersonal || Application.Current == null) return size;
        return new DynamicResourceExtension(Key(size, Visual)).ProvideValue(serviceProvider);
    }
}

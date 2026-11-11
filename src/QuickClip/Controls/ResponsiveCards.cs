using System.Windows;
using System.Windows.Controls;

namespace QuickClip.Controls;

/// <summary>Uses a second column only when the available client width accommodates both cards.</summary>
public sealed class ResponsiveCards : Panel
{
    protected override Size MeasureOverride(Size constraint) =>
        LayoutCards(double.IsFinite(constraint.Width) ? constraint.Width : 760, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutCards(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size LayoutCards(double width, bool arrange)
    {
        bool twoColumns = width >= 1120;
        double cardWidth = twoColumns ? width / 2 : width;
        double left = 0, right = 0;
        foreach (UIElement card in InternalChildren)
        {
            if (card.Visibility == Visibility.Collapsed) continue;
            if (!arrange) card.Measure(new Size(cardWidth, double.PositiveInfinity));

            bool useRight = twoColumns && right < left;
            double top = useRight ? right : left;
            if (arrange) card.Arrange(new Rect(useRight ? cardWidth : 0, top, cardWidth, card.DesiredSize.Height));
            if (useRight) right += card.DesiredSize.Height;
            else left += card.DesiredSize.Height;
        }
        return new Size(width, Math.Max(left, right));
    }
}

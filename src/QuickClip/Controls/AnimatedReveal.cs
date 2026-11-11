using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace QuickClip.Controls;

/// <summary>Reveals content by changing the height reserved for it in the parent layout.</summary>
public sealed class AnimatedReveal : Decorator
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(AnimatedReveal),
        new PropertyMetadata(false, (sender, _) => ((AnimatedReveal)sender).UpdateExpansion()));

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(AnimatedReveal),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private double Progress => (double)GetValue(ProgressProperty);

    public AnimatedReveal()
    {
        ClipToBounds = true;
        UpdateExpansion();
        Unloaded += (_, _) =>
        {
            BeginAnimation(ProgressProperty, null);
            SetValue(ProgressProperty, IsExpanded ? 1d : 0d);
        };
    }

    private void UpdateExpansion()
    {
        double target = IsExpanded ? 1 : 0;
        double current = Progress;
        IsHitTestVisible = IsExpanded;
        KeyboardNavigation.SetTabNavigation(this, IsExpanded ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        SetValue(ProgressProperty, target);
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation || Math.Abs(target - current) < 0.001)
        {
            BeginAnimation(ProgressProperty, null);
            return;
        }

        BeginAnimation(ProgressProperty, new DoubleAnimation(current, target,
            TimeSpan.FromMilliseconds(180 * Math.Abs(target - current)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        }, HandoffBehavior.SnapshotAndReplace);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child == null || (!IsExpanded && Progress <= 0)) return new Size();
        Child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Progress);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(arrangeSize.Height, Child.DesiredSize.Height)));
        return arrangeSize;
    }
}

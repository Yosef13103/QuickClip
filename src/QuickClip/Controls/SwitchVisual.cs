using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuickClip.Controls;

/// <summary>A resolution-independent pill switch. The owning ToggleButton retains all input and UI automation.</summary>
public sealed class SwitchVisual : FrameworkElement
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(nameof(IsOn), typeof(bool?),
        typeof(SwitchVisual), new FrameworkPropertyMetadata((bool?)false, OnToggle));
    public bool? IsOn { get => (bool?)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush),
        typeof(SwitchVisual), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(nameof(OutlineBrush), typeof(Brush),
        typeof(SwitchVisual), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush OutlineBrush { get => (Brush)GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }
    public static readonly DependencyProperty ThumbBrushProperty = DependencyProperty.Register(nameof(ThumbBrush), typeof(Brush),
        typeof(SwitchVisual), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush ThumbBrush { get => (Brush)GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }

    private static readonly DependencyProperty PositionProperty = DependencyProperty.Register("Position", typeof(double),
        typeof(SwitchVisual), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnToggle(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var visual = (SwitchVisual)sender;
        double current = (double)visual.GetValue(PositionProperty);
        double next = args.NewValue is true ? 1 : 0;
        visual.BeginAnimation(PositionProperty, null);
        visual.SetValue(PositionProperty, next);
        if (!visual.IsLoaded || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || current == next) return;
        visual.BeginAnimation(PositionProperty, new DoubleAnimation(current, next, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        }, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        if (width < 2 || height < 2) return;
        double radius = (height - 1) / 2;
        var pen = new Pen(OutlineBrush, 1);
        dc.DrawRoundedRectangle(TrackBrush, pen, new Rect(.5, .5, width - 1, height - 1), radius, radius);
        double thumb = height * .325;
        double start = height / 2;
        double x = start + Math.Max(0, width - height) * (double)GetValue(PositionProperty);
        dc.DrawEllipse(ThumbBrush, null, new Point(x, height / 2), thumb, thumb);
    }
}

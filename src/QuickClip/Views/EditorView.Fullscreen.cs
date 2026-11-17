using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickClip.Views;

public partial class EditorView
{
    private Popup? _fullscreenOverlay;
    private Slider? _fullscreenSeek;
    private Button? _fullscreenPlay;

    private void OpenFullscreenOverlay()
    {
        if (_fullscreenOverlay == null)
        {
            var play = FullscreenAction("Play or pause", PlayGlyph, TogglePlay);
            var exit = FullscreenAction("Exit fullscreen (Esc or F11)", "\uE73F", ExitFullscreen);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            buttons.Children.Add(play);
            buttons.Children.Add(exit);
            var seek = new Slider
            {
                Minimum = 0, IsMoveToPointEnabled = true, Height = 16, MinHeight = 0,
                Margin = new Thickness(0, 3, 0, 0),
            };
            AutomationProperties.SetName(seek, "Fullscreen playback position");
            seek.ValueChanged += (_, e) =>
            {
                if (!_updatingViewSeek && _fullscreen && SourceReady) Seek(e.NewValue);
            };
            var panel = new StackPanel();
            panel.Children.Add(buttons);
            panel.Children.Add(seek);
            panel.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.F11 or Key.Escape)
                {
                    e.Handled = true;
                    if (!e.IsRepeat) ExitFullscreen();
                }
            };
            var card = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 4, 8, 3), Child = panel };
            card.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
            card.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            _fullscreenPlay = play;
            _fullscreenSeek = seek;
            _fullscreenOverlay = new Popup
            {
                PlacementTarget = Root, Placement = PlacementMode.Custom, AllowsTransparency = true,
                StaysOpen = true, Child = card, CustomPopupPlacementCallback = Controls.FullscreenPopup.Place,
            };
        }

        if (_fullscreenOverlay.Child is FrameworkElement content)
            Controls.FullscreenPopup.UpdateWidth(_fullscreenOverlay, content, Root.ActualWidth);
        UpdateViewSeek();
        _fullscreenOverlay.IsOpen = true;
    }

    private Button FullscreenAction(string label, string glyph, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = label, MinWidth = 36, Margin = new Thickness(3, 0, 3, 0) };
        if (TryFindResource("IconButton") is Style style) button.Style = style;
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    internal void DismissFullscreenControls()
    {
        _fullscreenControlsTimer.Stop();
        HideFullscreenOverlay();
    }

    private void HideFullscreenOverlay()
    {
        if (_fullscreenOverlay == null) return;
        _fullscreenOverlay.IsOpen = false;
    }
}

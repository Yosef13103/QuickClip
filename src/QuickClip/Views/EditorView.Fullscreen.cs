using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QuickClip.Controls;

namespace QuickClip.Views;

public partial class EditorView
{
    private Popup? _fullscreenOverlay;
    private Slider? _fullscreenSeek;
    private Button? _fullscreenPlay;
    private TextBlock? _fullscreenTime, _fullscreenDuration;
    private SeekMarkerOverlay? _fullscreenMarkers;
    private bool _fullscreenControlsHidden;
    private (int X, int Y) _fullscreenLastMouse = (-1, -1);

    // Same transport layout as PlayerView.ControlBar, but using the current editor
    // decoder, selection/loop and audio preview; entering fullscreen does not open another player.
    private void OpenFullscreenOverlay()
    {
        if (_fullscreenOverlay == null)
        {
            var panel = new StackPanel();
            panel.SetResourceReference(Panel.BackgroundProperty, "PanelBrush");
            panel.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            var seekRow = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            var seek = new Slider { Minimum = 0, IsMoveToPointEnabled = true, Focusable = false };
            AutomationProperties.SetName(seek, "Fullscreen playback position");
            if (TryFindResource("ThemedMediaSlider") is Style sliderStyle) seek.Style = sliderStyle;
            seek.ValueChanged += (_, e) =>
            {
                if (!_updatingViewSeek && _fullscreen && SourceReady) Seek(e.NewValue);
            };
            var markers = new SeekMarkerOverlay { IsHitTestVisible = false };
            seek.PreviewMouseMove += (_, e) => seek.ToolTip = markers.TooltipAt(e.GetPosition(markers));
            seekRow.Children.Add(seek);
            seekRow.Children.Add(markers);
            panel.Children.Add(seekRow);

            var actions = new DockPanel { Margin = new Thickness(4, 5, 4, 0) };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var play = FullscreenAction("Play / pause (Space)", PlayGlyph, TogglePlay);
            play.Padding = new Thickness(16, 6, 16, 6);
            left.Children.Add(play);
            var time = Clock("0:00", "QuickClipText14", new Thickness(10, 0, 0, 0));
            var duration = Clock(" / 0:00", "QuickClipText13", new Thickness(0));
            duration.SetResourceReference(TextBlock.ForegroundProperty, "SubtleText");
            left.Children.Add(time);
            left.Children.Add(duration);
            DockPanel.SetDock(left, Dock.Left);
            actions.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(new AppVolumeControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var exit = FullscreenAction("Exit fullscreen (Esc or F11)", "\uE73F", ExitFullscreen);
            exit.Margin = new Thickness(0);
            right.Children.Add(exit);
            DockPanel.SetDock(right, Dock.Right);
            actions.Children.Add(right);
            actions.Children.Add(new Grid());
            panel.Children.Add(actions);

            panel.MouseMove += (_, _) => RevealFullscreenControls();
            panel.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.F11 or Key.Escape)
                {
                    e.Handled = true;
                    if (!e.IsRepeat) ExitFullscreen();
                }
                else if (e.Key == Key.Space && Keyboard.FocusedElement is not Button)
                {
                    e.Handled = true;
                    if (!e.IsRepeat) TogglePlay();
                }
            };
            _fullscreenPlay = play;
            _fullscreenSeek = seek;
            _fullscreenTime = time;
            _fullscreenDuration = duration;
            _fullscreenMarkers = markers;
            var surface = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Child = panel };
            surface.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            surface.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
            panel.Background = Brushes.Transparent;
            _fullscreenOverlay = new Popup
            {
                PlacementTarget = Root, Placement = PlacementMode.Custom, AllowsTransparency = true,
                StaysOpen = true, Child = surface, CustomPopupPlacementCallback = FullscreenPopup.Place,
            };
        }

        if (_fullscreenOverlay.Child is FrameworkElement content)
            FullscreenPopup.UpdateWidth(_fullscreenOverlay, content, Root.ActualWidth);
        _fullscreenMarkers!.Show(MetadataEditor.AnnotationTarget?.Details.Bookmarks ?? [], _timeline.Duration);
        UpdateViewSeek();
        _fullscreenControlsHidden = false;
        _fullscreenOverlay.IsOpen = true;
        Video.RefreshCursor();

        static TextBlock Clock(string text, string size, Thickness margin)
        {
            var block = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center, Margin = margin };
            block.SetResourceReference(TextBlock.FontSizeProperty, size);
            return block;
        }
    }

    private Button FullscreenAction(string label, string glyph, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = label };
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
        if (_fullscreenOverlay != null) _fullscreenOverlay.IsOpen = false;
    }

    private void ReleaseFullscreenOverlay()
    {
        HideFullscreenOverlay();
        if (_fullscreenOverlay is { } popup) popup.Child = null;
        _fullscreenOverlay = null;
        _fullscreenPlay = null;
        _fullscreenSeek = null;
        _fullscreenTime = _fullscreenDuration = null;
        _fullscreenMarkers = null;
        _fullscreenControlsHidden = false;
        _fullscreenLastMouse = (-1, -1);
        Video.RefreshCursor();
    }
}

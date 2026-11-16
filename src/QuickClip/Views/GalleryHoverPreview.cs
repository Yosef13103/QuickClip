using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickClip.Library;
using QuickClip.Gallery;

namespace QuickClip.Views;

/// <summary>One muted decoder for the visible gallery, opened only while a poster is hovered.</summary>
internal sealed class GalleryHoverPreview
{
    private Border? _surface;
    private Grid? _poster;
    private CancellationTokenSource? _pending;
    private ClipDraftRange? _range;
    private DispatcherTimer? _trimTimer;

    internal bool IsActive => _poster != null;

    public void Start(Grid poster, ClipItem item) => _ = StartAsync(poster, item, requireHover: true);

    private async Task StartAsync(Grid poster, ClipItem item, bool requireHover)
    {
        Stop();
        _poster = poster;
        poster.DataContextChanged += PosterChanged;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), pending.Token);
            if (!IsCurrentPoster(poster, item, requireHover))
            {
                Stop(poster);
                return;
            }
            var surface = _surface ??= CreateSurface();
            var media = (MediaElement)surface.Child;
            if (item.EditRange is { } candidate)
            {
                var state = await Task.Run(() => EditorSource.Check(item.Path, candidate.Source, out _), pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                if (!IsCurrentPoster(poster, item, requireHover))
                {
                    Stop(poster);
                    return;
                }
                if (state != EditorSourceState.Ready)
                {
                    if (item.EditRange == candidate) item.ApplyEditRange(null);
                    Stop(poster);
                    return;
                }
                _range = candidate;
            }
            poster.Children.Insert(2, surface);
            media.Source = new Uri(item.Path);
            media.Pause();
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Gallery hover playback failed", ex);
            Stop(poster);
        }
        finally
        {
            if (_pending == pending) _pending = null;
        }
    }

    public void Stop(Grid? poster = null)
    {
        if (_poster is not { } active || (poster != null && active != poster)) return;
        _poster = null;
        _range = null;
        _trimTimer?.Stop();
        active.DataContextChanged -= PosterChanged;
        _pending?.Cancel();
        _pending = null;
        if (_surface is not { Child: MediaElement media } surface || !active.Children.Contains(surface)) return;
        media.Close();
        media.Source = null;
        surface.Opacity = 0;
        active.Children.Remove(surface);
    }

    internal Border CreateSurface()
    {
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.Uniform,
        };
        var surface = new Border
        {
            Background = Brushes.Black,
            Child = media,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        media.MediaOpened += (_, _) =>
        {
            if (_poster == null) return;
            if (_range is { } range && IsTrimmed(range, media.NaturalDuration))
            {
                media.Position = TimeSpan.FromSeconds(range.Start);
                _trimTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Render,
                    (_, _) =>
                    {
                        if (_range is not { } bounds || media.Position.TotalSeconds >= bounds.End)
                            Stop();
                        else if (surface.Opacity == 0 && media.Position.TotalSeconds >= bounds.Start)
                            surface.Opacity = 1;
                    }, media.Dispatcher) { IsEnabled = false };
                _trimTimer.Start();
            }
            else surface.Opacity = 1;
            media.Play();
        };
        media.MediaEnded += (_, _) => Stop();
        media.MediaFailed += (_, _) => Stop();
        return surface;
    }

    internal static bool IsTrimmed(ClipDraftRange range, Duration duration) =>
        range.Start > 0 || !duration.HasTimeSpan || range.End < duration.TimeSpan.TotalSeconds - 0.001;

    private bool IsCurrentPoster(Grid poster, ClipItem item, bool requireHover) =>
        poster == _poster && poster.IsVisible && poster.DataContext == item &&
        (!requireHover || poster.IsMouseOver && Window.GetWindow(poster)?.IsActive == true);

#if DEBUG
    internal Task StartForRenderAsync(Grid poster, ClipItem item) => StartAsync(poster, item, requireHover: false);
    internal double? VisiblePositionForRender => _surface is { Opacity: 1, Child: MediaElement player }
        ? player.Position.TotalSeconds : null;
#endif

    private void PosterChanged(object sender, DependencyPropertyChangedEventArgs e) => Stop((Grid)sender);
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickClip.Gallery;

namespace QuickClip.Views;

/// <summary>One muted decoder for the visible gallery, opened only while a poster is hovered.</summary>
internal sealed class GalleryHoverPreview
{
    private MediaElement? _media;
    private Grid? _poster;
    private CancellationTokenSource? _pending;

    internal bool IsActive => _poster != null;

    public async void Start(Grid poster, ClipItem item)
    {
        Stop();
        _poster = poster;
        poster.DataContextChanged += PosterChanged;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), pending.Token);
            if (!poster.IsVisible || !poster.IsMouseOver || poster.DataContext != item ||
                Window.GetWindow(poster)?.IsActive != true)
            {
                Stop(poster);
                return;
            }
            var media = _media ??= CreateMedia();
            poster.Children.Insert(2, media);
            media.Source = new Uri(item.Path);
            media.Play();
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Write("Gallery hover playback failed: " + ex.GetType().Name);
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
        active.DataContextChanged -= PosterChanged;
        _pending?.Cancel();
        _pending = null;
        if (_media is not { } media || !active.Children.Contains(media)) return;
        media.Close();
        media.Source = null;
        media.Opacity = 0;
        active.Children.Remove(media);
    }

    private MediaElement CreateMedia()
    {
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        media.MediaOpened += (_, _) => { if (_poster != null) media.Opacity = 1; };
        media.MediaEnded += (_, _) => Stop();
        media.MediaFailed += (_, _) => Stop();
        return media;
    }

    private void PosterChanged(object sender, DependencyPropertyChangedEventArgs e) => Stop((Grid)sender);
}

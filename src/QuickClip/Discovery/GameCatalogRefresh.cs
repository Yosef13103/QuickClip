namespace QuickClip.Discovery;

/// <summary>Refreshes the same approved request at most once per interval after a successful scan.</summary>
internal sealed class GameCatalogRefresh
{
    private readonly CatalogRequest _request;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CatalogResult? _latest;
    private long _completed;

    public GameCatalogRefresh(CatalogRequest request, TimeSpan interval, TimeProvider? clock = null)
    {
        if (interval < TimeSpan.FromMinutes(1) || interval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _request = request;
        _interval = interval;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<CatalogResult> RefreshIfDueAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_latest != null && _clock.GetElapsedTime(_completed) < _interval) return _latest;
            var result = await GameCatalogService.ScanAsync(_request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _completed = _clock.GetTimestamp();
            return _latest = result;
        }
        finally { _gate.Release(); }
    }
}

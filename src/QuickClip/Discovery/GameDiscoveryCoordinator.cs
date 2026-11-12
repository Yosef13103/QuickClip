namespace QuickClip.Discovery;

/// <summary>Owns one in-process discovery queue. Call from the host's settings/UI context.</summary>
internal sealed class GameDiscoveryCoordinator : IDisposable
{
    private readonly Func<GameCatalogSettings> _read;
    private readonly Action<GameCatalogSettings, bool> _publish;
    private readonly Func<CatalogRequest, GameCatalogService.Scan> _create;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _clock;
    private readonly int _segmentsPerSlice;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private CancellationTokenSource? _activeCancellation;
    private GameCatalogService.Scan? _scan;
    private CatalogRequest? _scanRequest;
    private Task _running = Task.CompletedTask;
    private bool _started, _disposed, _paused;
    private long? _completedAt;

    internal GameDiscoveryCoordinator(Func<GameCatalogSettings> read, Action<GameCatalogSettings, bool> publish,
        Func<CatalogRequest, GameCatalogService.Scan>? create = null, TimeProvider? clock = null, int segmentsPerSlice = 12)
    {
        if (segmentsPerSlice < 1) throw new ArgumentOutOfRangeException(nameof(segmentsPerSlice));
        _read = read;
        _publish = publish;
        _create = create ?? (request => GameCatalogService.CreateScan(request));
        _clock = clock ?? TimeProvider.System;
        _segmentsPerSlice = segmentsPerSlice;
    }

    public event Action? Changed;
    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "";

    /// <summary>Starts background discovery independently of the Games view.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _ = RunTimerAsync(_lifetime.Token);
    }

    private async Task RunTimerAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RequestAsync();
                await Task.Delay(RefreshInterval, _clock, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public Task RequestAsync(bool manual = false)
    {
        if (_disposed) return Task.CompletedTask;
        if (manual) _paused = false;
        if (IsRunning) return _running;
        var state = _read();
        if (!manual && (!state.AutoScan || _paused ||
            _completedAt is { } last && _clock.GetElapsedTime(last) < RefreshInterval))
            return Task.CompletedTask;

        var request = ScanRequest(state);
        if (!request.DiscoverSteam && request.GameRoots.IsEmpty && request.SteamLibraryRoots.IsEmpty)
        {
            Status = "Add a game folder or turn on Steam discovery.";
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _activeCancellation = cancellation;
        IsRunning = true;
        Status = "Scanning game libraries…";
        _running = RunAsync(request, cancellation);
        Changed?.Invoke();
        return _running;
    }

    /// <summary>Pauses at the next slice boundary. Scan Now resumes queued work.</summary>
    public void Pause()
    {
        if (!IsRunning) return;
        _paused = true;
        Status = "Pausing after the current scan batch…";
        Changed?.Invoke();
    }

    public async Task SettingsChangedAsync()
    {
        if (_disposed) return;
        if (_scanRequest != ScanRequest(_read()))
        {
            _activeCancellation?.Cancel();
            _scan = null;
            _scanRequest = null;
            _completedAt = null;
            _paused = false;
            await _running;
        }
        if (_started) await RequestAsync();
    }

    private async Task RunAsync(CatalogRequest request, CancellationTokenSource cancellation)
    {
        // Allow the caller to publish the shared task before any observer is notified.
        await Task.Yield();
        var token = cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (_scanRequest != request || _scan == null)
            {
                _scan = _create(request);
                _scanRequest = request;
            }
            var scan = _scan;
            do
            {
                var result = await scan.ContinueAsync(_segmentsPerSlice, TimeSpan.FromSeconds(1), token);
                token.ThrowIfCancellationRequested();
                var current = _read();
                if (ScanRequest(current) != request)
                {
                    _scan = null;
                    _scanRequest = null;
                    return;
                }
                _publish(GameCatalogRuntime.WithScan(current, result), !scan.HasMore);
                Status = $"Found {result.Entries.Length:N0} games."
                    + (scan.HasMore ? " More installs are queued."
                        : result.LimitReached || !result.Issues.IsEmpty ? " Some sources could not be fully scanned." : "");
                Changed?.Invoke();
                if (scan.HasMore && !_paused)
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, token);
            }
            while (scan.HasMore && !_paused);

            if (scan.HasMore)
            {
                _publish(_read(), true);
                Status += " Paused. Select Scan Now to resume.";
            }
            else
            {
                _scan = null;
                _completedAt = _clock.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = "Scan canceled. Published results kept.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _scan = null;
            _scanRequest = null;
            Status = "Scan could not finish. Published results kept.";
            Log.Write("Game scan failed: " + ex.GetType().Name);
        }
        finally
        {
            _activeCancellation = null;
            cancellation.Dispose();
            IsRunning = false;
            if (!_disposed) Changed?.Invoke();
        }
    }

    private static CatalogRequest ScanRequest(GameCatalogSettings state) =>
        state.Request with { DiscoverSteam = state.AutoScan, Overrides = [], ManualEntries = [] };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

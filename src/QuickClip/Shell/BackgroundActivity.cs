namespace QuickClip.Shell;

/// <summary>Short-lived UI task leases. No timer, worker, or history; polling is owned by the existing status bar.</summary>
internal sealed class BackgroundActivity : IDisposable
{
    internal sealed record Entry(long Id, string Label, double? Fraction, long Started);
    private static readonly object Gate = new();
    private static readonly Dictionary<long, Entry> Active = [];
    private static long _next;
    private readonly long _id;
    private long _lastReport;

    private BackgroundActivity(long id) => _id = id;

    internal static BackgroundActivity Begin(string label)
    {
        if (!AppProfile.IsPersonal) return new(0);
        lock (Gate)
        {
            long id = ++_next;
            Active.Add(id, new(id, label, null, Environment.TickCount64));
            return new(id);
        }
    }

    internal void Report(string label, double? fraction = null)
    {
        if (_id == 0) return;
        lock (Gate)
        {
            if (!Active.TryGetValue(_id, out var old)) return;
            double? value = fraction is double f && double.IsFinite(f) ? Math.Clamp(f, 0, 1) : null;
            long now = Environment.TickCount64;
            // Bound frequent progress callbacks; stage changes remain visible.
            if (old.Label == label && old.Fraction == value) return;
            if (old.Fraction.HasValue && value is > 0 and < 1 && old.Label == label && now - _lastReport < 100) return;
            _lastReport = now;
            Active[_id] = old with { Label = label, Fraction = value };
        }
    }

    internal static Entry[] Snapshot(long minimumAgeMs = 350)
    {
        lock (Gate)
        {
            if (Active.Count == 0) return [];
            long now = Environment.TickCount64;
            return Active.Values.Where(entry => now - entry.Started >= minimumAgeMs)
                .OrderBy(entry => entry.Id).ToArray();
        }
    }

    public void Dispose()
    {
        if (_id == 0) return;
        lock (Gate) Active.Remove(_id);
    }
}

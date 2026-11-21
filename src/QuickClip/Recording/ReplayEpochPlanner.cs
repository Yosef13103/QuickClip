namespace QuickClip.Recording;

/// <summary>
/// Experimental, side-effect-free planning model for a replay saved across encoded
/// capture graphs. Not connected to recording, authorization, saving or the native ring.
/// All times are in the native packet-ring microsecond timeline; ranges are half-open.
/// </summary>
internal sealed record ReplayEncodedEpoch(
    Guid SessionId,
    string CaptureScope,
    Guid EpochId,
    long StartUs,
    long EndUs,
    int Width,
    int Height,
    string StreamFingerprint);

internal sealed record ReplayEpochSlice(ReplayEncodedEpoch Epoch, long StartUs, long EndUs);
internal sealed record ReplayEpochGap(long StartUs, long EndUs);
internal sealed record ReplayEpochExportPlan(
    IReadOnlyList<ReplayEpochSlice> Slices,
    IReadOnlyList<ReplayEpochGap> Gaps,
    int Width,
    int Height,
    bool MixedVideoGeometry,
    bool MixedStreamConfiguration)
{
    public bool HasDiscontinuities => Gaps.Count > 0;
    // Even a uniform codec/fingerprint is only necessary, not sufficient, for lossless remux.
    public bool RequiresVideoNormalization => MixedVideoGeometry || MixedStreamConfiguration;
}

/// <summary>
/// Selects only recorded epochs intersecting the requested replay interval.
/// Never infers authorization from a matching title, treats a gap as footage,
/// or assumes re-encoding is safely avoidable across graph boundaries.
/// </summary>
internal static class ReplayEpochPlanner
{
    internal static ReplayEpochExportPlan Select(
        IEnumerable<ReplayEncodedEpoch> epochs, Guid sessionId, string captureScope,
        long fromUs, long toUs, long maxCanvasPixels)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(captureScope) ||
            fromUs < 0 || toUs <= fromUs || maxCanvasPixels <= 0)
            throw new ArgumentException("Invalid replay interval, session, scope, or output budget.");

        var selected = epochs.Where(epoch => epoch.EndUs > fromUs && epoch.StartUs < toUs)
            .OrderBy(epoch => epoch.StartUs).ThenBy(epoch => epoch.EndUs).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("No encoded replay epochs in the requested interval.");

        var slices = new List<ReplayEpochSlice>(selected.Length);
        var gaps = new List<ReplayEpochGap>();
        long cursor = fromUs;
        int width = 0, height = 0;
        long area = 0;
        string? fingerprint = null;
        bool mixedGeometry = false, mixedStream = false;
        foreach (var epoch in selected)
        {
            if (epoch.SessionId != sessionId || !StringComparer.Ordinal.Equals(epoch.CaptureScope, captureScope))
                throw new InvalidOperationException("Replay interval includes a different recording session or authorized capture scope.");
            if (epoch.EpochId == Guid.Empty || epoch.StartUs < 0 || epoch.EndUs <= epoch.StartUs ||
                epoch.Width <= 0 || epoch.Height <= 0 || string.IsNullOrWhiteSpace(epoch.StreamFingerprint))
                throw new InvalidOperationException("Replay epoch metadata is invalid.");

            long start = Math.Max(epoch.StartUs, fromUs);
            long end = Math.Min(epoch.EndUs, toUs);
            if (start < cursor)
                throw new InvalidOperationException("Overlapping encoded epochs require explicit timeline reconciliation.");
            if (start > cursor) gaps.Add(new ReplayEpochGap(cursor, start));
            cursor = end;
            slices.Add(new ReplayEpochSlice(epoch, start, end));

            long pixels = checked((long)epoch.Width * epoch.Height);
            if (pixels > maxCanvasPixels)
                throw new InvalidOperationException("Replay epoch exceeds the configured output pixel budget.");
            if (area > 0 && (width != epoch.Width || height != epoch.Height)) mixedGeometry = true;
            if (fingerprint != null && !StringComparer.Ordinal.Equals(fingerprint, epoch.StreamFingerprint))
                mixedStream = true;
            fingerprint ??= epoch.StreamFingerprint;
            if (pixels > area || pixels == area && (epoch.Width > width ||
                epoch.Width == width && epoch.Height > height))
            {
                area = pixels;
                width = epoch.Width;
                height = epoch.Height;
            }
        }
        if (cursor < toUs) gaps.Add(new ReplayEpochGap(cursor, toUs));
        return new ReplayEpochExportPlan(slices, gaps, width, height, mixedGeometry, mixedStream);
    }
}

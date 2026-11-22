namespace QuickClip.Recording;

/// <summary>
/// Explicit-save adapter over independently encoded native game epochs.
/// It does not own capture acquisition or make authorization decisions.
/// Call only after the Recorder has revalidated game ownership and grants.
/// No disk activity happens while merely collecting a rolling replay.
/// </summary>
internal static class ReplayEpochLiveSaver
{
    internal sealed record SelectedEpoch(ulong NativeId, Guid MediaId);
    internal sealed record SelectedPlan(ReplayEpochExportPlan Plan,
        IReadOnlyList<SelectedEpoch> Archived, Guid ActiveMediaId,
        long ActiveFirstGlobalUs);

    internal sealed record Merged(string Path, TimeSpan Duration, TimeSpan Omitted,
        int Width, int Height, int EpochCount, long FirstGlobalUs);

    internal static SelectedPlan? Select(
        CaptureEngine.EpochDescriptor[] retained,
        Guid logicalSession, string scope,
        long graphStartUs, long requestedAtUs, int seconds,
        CaptureEngine.ReplaySpan active, CaptureEngine.ReplayVideo video, int fps)
    {
        if (logicalSession == Guid.Empty || string.IsNullOrWhiteSpace(scope) ||
            graphStartUs <= 0 || requestedAtUs < 0 || seconds <= 0 ||
            video.Width < 16 || video.Height < 16 || fps is < 1 or > 240 ||
            active.FirstUs < 0 || active.LastUs < active.FirstUs)
            throw new InvalidOperationException("Invalid cross-epoch replay clock or capture metadata.");

        long first = checked(graphStartUs + active.FirstUs);
        long currentDuration = active.DurationUs > 0
            ? active.DurationUs : checked(active.LastUs - active.FirstUs + 1_000_000 / fps);
        long end = checked(first + currentDuration);
        long requestedStart = Math.Max(0, checked(graphStartUs + requestedAtUs -
            (long)seconds * 1_000_000));
        var relevant = retained.Where(epoch => epoch.LastUs > requestedStart &&
            epoch.FirstUs < first).OrderBy(epoch => epoch.FirstUs).ToArray();
        if (relevant.Length == 0) return null;
        if (relevant.Length >= 32 || relevant.Any(epoch =>
            epoch.EpochId == 0 || epoch.FirstUs < 0 || epoch.LastUs <= epoch.FirstUs ||
            epoch.LastUs >= first || epoch.Width < 16 || epoch.Height < 16))
            throw new InvalidOperationException("Retained epoch timing/geometry cannot be safely joined.");

        var epochs = new List<ReplayEncodedEpoch>(relevant.Length + 1);
        var mappings = new List<SelectedEpoch>(relevant.Length);
        foreach (var source in relevant)
        {
            Guid id = Guid.NewGuid();
            epochs.Add(new(logicalSession, scope, id, source.FirstUs, source.LastUs,
                source.Width, source.Height, "separate-encoded-graph"));
            mappings.Add(new(source.EpochId, id));
        }
        Guid activeId = Guid.NewGuid();
        epochs.Add(new(logicalSession, scope, activeId, first, end,
            video.Width, video.Height, "active-encoded-graph"));
        long from = Math.Max(requestedStart, relevant[0].FirstUs);
        var plan = ReplayEpochPlanner.Select(epochs, logicalSession, scope,
            from, end, 34_000_000);
        return new(plan, mappings, activeId, first);
    }

    internal static async Task<Merged?> MergeAsync(
        Guid logicalSession, ReplayEpochGameProof proof,
        CaptureEngine.EpochDescriptor[] retained, long graphStartUs,
        CaptureEngine.ReplayPosition requestPosition,
        int seconds, CaptureEngine.ReplaySpan active, CaptureEngine.ReplayVideo video,
        int fps, string activeMediaPath, string outputDirectory,
        string ffmpeg, string ffprobe, CancellationToken cancellationToken,
        Func<ulong, string, CancellationToken, Task>? syntheticEpochWriter = null)
    {
        var selected = Select(retained, logicalSession, CaptureEngine.EpochScope(proof),
            graphStartUs, requestPosition.EventUs, seconds, active, video, fps);
        if (selected == null) return null;
        cancellationToken.ThrowIfCancellationRequested();

        // Temporary compressed MKVs and the normalized MP4 coexist with the
        // already-staged active clip. Reserve headroom before touching disk.
        long archivedBytes = 0;
        foreach (var entry in selected.Archived)
        {
            var descriptor = retained.Single(d => d.EpochId == entry.NativeId);
            if (descriptor.CompressedBytes <= 0)
                throw new IOException("The archived footage has no valid compressed-size accounting.");
            archivedBytes = checked(archivedBytes + descriptor.CompressedBytes);
        }
        long activeBytes = new FileInfo(activeMediaPath).Length;
        long estimatedScratch = checked((archivedBytes + activeBytes) * 3 +
            512L * 1024 * 1024);
        string root = Path.GetPathRoot(Path.GetFullPath(outputDirectory)) ??
            throw new IOException("Cannot determine free space for temporary epoch media.");
        long free = new DriveInfo(root).AvailableFreeSpace;
        if (free < estimatedScratch)
            throw new IOException("Not enough free space to safely normalize this replay. Free space or save a shorter clip.");

        string directory = Path.Combine(outputDirectory, ".qc-e-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
        // The Gallery recurses clip folders; never index temporary MKV stems.
        File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        try
        {
            var files = new List<ReplayEpochNormalizer.Media>(selected.Archived.Count + 1);
            for (int i = 0; i < selected.Archived.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = selected.Archived[i];
                string path = Path.Combine(directory, i.ToString("D2") + ".mkv");
                // Only an explicit save causes the native compressed-packet remux.
                // I/O is performed on a separate low-priority thread, never video.
                if (syntheticEpochWriter == null)
                    await WriteArchiveAsync(logicalSession, proof, entry.NativeId, path)
                        .ConfigureAwait(false);
                else
                    await syntheticEpochWriter(entry.NativeId, path, cancellationToken)
                        .ConfigureAwait(false);
                files.Add(new(entry.MediaId, path));
            }
            files.Add(new(selected.ActiveMediaId, activeMediaPath));
            cancellationToken.ThrowIfCancellationRequested();
            string output = Path.Combine(outputDirectory,
                ".quickclip-tmp-joined-" + Guid.NewGuid().ToString("N") + ".mp4");
            var normalized = await ReplayEpochNormalizer.ExportAsync(selected.Plan,
                files, output, ffmpeg, ffprobe, fps,
                ReplayEpochGapPolicy.OmitMissingFootage, cancellationToken)
                .ConfigureAwait(false);
            return new(normalized.Path, normalized.Duration,
                normalized.OmittedMissingFootage, normalized.Width, normalized.Height,
                selected.Plan.Slices.Count, selected.Plan.Slices[0].StartUs);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException ex)
            {
                Log.Essential(DiagnosticCategory.Capture, "Temporary epoch media cleanup failed", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Essential(DiagnosticCategory.Capture, "Temporary epoch media cleanup denied", ex);
            }
        }
    }

    private static Task WriteArchiveAsync(Guid session, ReplayEpochGameProof proof,
        ulong epochId, string path)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                string? error = CaptureEngine.WriteGameEpoch(session, proof, epochId, path);
                if (error != null) completion.TrySetException(new IOException(error));
                else completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "QuickClip epoch remux" };
        thread.Start();
        return completion.Task;
    }
}

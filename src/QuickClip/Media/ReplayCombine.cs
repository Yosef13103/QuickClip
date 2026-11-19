using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Media;

/// <summary>Non-destructive packet union; all input leases remain held until output publication and metadata commit.</summary>
internal static class ReplayCombine
{
    internal const int MaxSources = 32;
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromHours(1);

    internal static (Guid Id, Task<MediaJobView> Completion) Submit(ReplayCluster cluster, string destination,
        TimeSpan limit, IReadOnlyList<ClipDetails> details, MediaJobs? jobs = null, ReplayProvenanceStore? store = null,
        ReplayReplacementService? replacement = null, bool automatic = false, Func<bool>? automaticAllowed = null)
    {
        var plan = ReplayPlanner.Subset(cluster, 0, cluster.Clips.Count - 1, limit);
        if (plan.Clips.Count > MaxSources || limit > MaxDuration || limit <= TimeSpan.Zero ||
            details.Count != plan.Clips.Count)
            throw new ExportException("Choose a smaller group of overlapping clips.");
        destination = Path.GetFullPath(destination);
        var sources = plan.Clips.Select(c => c.Media).ToArray();
        if (!Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            sources.Any(s => string.Equals(s.Path, destination, StringComparison.OrdinalIgnoreCase)) ||
            sources.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ExportException("Choose a distinct MP4 destination and distinct original clips.");
        details = details.ToArray();
        var combinedDetails = MergeDetails(plan, details);
        bool profileStore = store == null;
        if (Path.Exists(destination)) throw new ExportException("Choose a new combined recording filename.");
        MediaSource? published = null;
        var transactionId = Guid.NewGuid();
        store ??= ReplayProvenanceStore.Current;
        var provenance = store;
        var request = new MediaJobRequest(Path.GetFileName(destination), false, sources, destination,
            async (job, ct) =>
            {
                if (automaticAllowed?.Invoke() == false) throw new ExportException("Automatic replay settings changed.");
                string directory = Path.GetDirectoryName(destination)!;
                MediaProcessing.RequireSafeDirectory(directory);
                MediaProcessing.CheckFreeSpace(directory, checked(sources.Sum(s => s.Stamp.Bytes)));
                var manifests = new List<MediaStaging>();
                using var staged = MediaStaging.Reserve(directory, ".mp4");
                using var outputProof = MediaStaging.Reserve(directory, ".proof");
                try
                {
                    foreach (var source in plan.Clips)
                    {
                        ct.ThrowIfCancellationRequested();
                        var proof = MediaStaging.Reserve(directory, ".proof");
                        manifests.Add(proof);
                        File.WriteAllBytes(proof.Path, provenance.ReadProof(source));
                    }
                    ReplayNative.Join(sources.Select(s => s.Path).ToArray(), manifests.Select(p => p.Path).ToArray(),
                        staged.Path, outputProof.Path, limit, job, ct);
                    job.Validating();
                    var info = await MediaInfo.ProbeAsync(staged.Path, ct, localOnly: true);
                    if (info.Video == null || !double.IsFinite(info.Duration) || info.Duration <= 0 ||
                        info.Duration > limit.TotalSeconds)
                        throw new ExportException("The validated combined recording exceeds the duration limit or has no video.");
                    if (new FileInfo(outputProof.Path).Length > ReplayProvenanceStore.MaximumProofBytes)
                        throw new ExportException("The combined replay proof exceeds the metadata limit.");
                    byte[] proofBytes = File.ReadAllBytes(outputProof.Path);
                    ct.ThrowIfCancellationRequested();
                    var snapshot = MediaSource.Capture(staged.Path) with { Path = destination };
                    void Publish()
                    {
                        if (automaticAllowed?.Invoke() == false) throw new ExportException("Automatic replay settings changed.");
                        File.Move(staged.Path, destination); // never overwrite an existing file
                        try
                        {
                            using var outputHandle = snapshot.OpenVerified();
                            ClipDetailsStore.Write(destination, combinedDetails);
                            provenance.Add(snapshot, plan.Clips[0].Session, plan.FirstUs, plan.EndUs, proofBytes);
                            published = snapshot;
                        }
                        catch (Exception ex)
                        {
                            Log.Failure(DiagnosticCategory.Library, "Combined replay metadata could not be saved", ex);
                            throw new ExportException("The combined video was saved, but its metadata could not be committed. All originals were kept.");
                        }
                    }
                    if (profileStore) await PersonalLibrary.Current.PublishReplayAsync(Publish, ct);
                    else Publish();
                    return destination;
                }
                finally { foreach (var manifest in manifests) manifest.Dispose(); }
            });
        if (replacement != null)
            request = request with
            {
                Mutations = sources.Select(source => source.Path).ToArray(),
                AdditionalDestinations = replacement.ReservedPaths(transactionId, sources),
                ProtectEditorSources = true,
                Replace = (_, ct) => replacement.CommitAsync(transactionId,
                    published ?? throw new ExportException("The combined recording is not validated."),
                    sources, details, automatic, ct, automaticAllowed),
            };
        return (jobs ?? MediaJobs.Current).Submit(request);
    }

    internal static ClipDetails MergeDetails(ReplayCluster plan, IReadOnlyList<ClipDetails> details)
    {
        if (details.Count != plan.Clips.Count || details.Count == 0)
            throw new ExportException("The selected clip details are incomplete.");
        if (details.Any(d => !string.Equals(d.Game, details[0].Game, StringComparison.OrdinalIgnoreCase)))
            throw new ExportException("The selected clips have different game names.");
        var tags = details.SelectMany(d => d.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var bookmarks = details.SelectMany((d, i) => d.Bookmarks.Select(b => b with
        {
            Seconds = b.Seconds + (plan.Clips[i].FirstUs - plan.FirstUs) / 1_000_000d,
        })).Where(b => double.IsFinite(b.Seconds) && b.Seconds >= 0 && b.Seconds < plan.UnionUs / 1_000_000d)
            .OrderBy(b => b.Seconds).DistinctBy(b => (Math.Round(b.Seconds, 6), b.Label, b.SessionId)).ToArray();
        if (tags.Length > ClipDetails.MaxTags || bookmarks.Length > ClipDetails.MaxBookmarks ||
            bookmarks.Select(b => b.Seconds).Distinct().Count() != bookmarks.Length)
            throw new ExportException("The combined annotations exceed the supported limits. Choose fewer clips.");
        return new(details[0].CapturedAt, details[0].Game, details.Select(d => d.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "Combined replay")
        {
            Tags = tags, Bookmarks = bookmarks, IsFavorite = details.Any(d => d.IsFavorite),
        };
    }
}
using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Recording;

/// <summary>Admit immutable proof validation after successful save feedback, using the existing bounded queue.</summary>
internal static class ReplayProofJobs
{
    internal static Task<MediaJobView>? TryQueue(MediaSource media, MediaSource proof, MediaStaging pending, Guid session, CaptureEngine.ReplaySpan span,
        MediaJobs? jobs = null, ReplayProvenanceStore? store = null)
    {
        try
        {
            if (session == Guid.Empty || span.FirstUs < 0 || span.DurationUs <= 0 ||
                span.FirstSource <= 0 || span.FirstSource != span.LastSource ||
                span.LastUs >= checked(span.FirstUs + span.DurationUs) ||
                proof.Stamp.Bytes <= 0 || !string.Equals(proof.Path, pending.Path, StringComparison.OrdinalIgnoreCase))
                return null;

            long endUs = checked(span.FirstUs + span.DurationUs);
            var submission = (jobs ?? MediaJobs.Current).Submit(new("Validate replay provenance", false,
                [media, proof], pending.Path, (job, ct) =>
                {
                    (store ?? ReplayProvenanceStore.Current).FinalizeAndAdd(media, proof, session, span.FirstUs, endUs, job, ct);
                    return Task.FromResult(media.Path);
                })
            {
                Visible = false,
                MetadataOnly = true,
            });
            return SettleAsync(submission.Completion, pending);
        }
        catch (Exception ex)
        {
            // A proof failure never invalidates the successfully published clip.
            Log.Failure(DiagnosticCategory.Library, "Could not queue replay provenance", ex);
            return null;
        }
    }

    private static async Task<MediaJobView> SettleAsync(Task<MediaJobView> completion, MediaStaging pending)
    {
        try { return await completion.ConfigureAwait(false); }
        finally { pending.Dispose(); }
    }
}
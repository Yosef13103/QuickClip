using QuickClip.Library;

namespace QuickClip.Media;

internal sealed record ReplayCluster(IReadOnlyList<ReplayProvenance> Clips, long FirstUs, long EndUs, long IndividualUs)
{
    internal long UnionUs => EndUs - FirstUs;
    internal long DuplicateUs => IndividualUs - UnionUs;
}

/// <summary>Chronological interval components, using verified encoded coverage rather than file dates.</summary>
internal static class ReplayPlanner
{
    internal static ReplayCluster[] Find(IEnumerable<ReplayProvenance> candidates)
    {
        var result = new List<ReplayCluster>();
        foreach (var group in candidates.Where(c => c.FirstUs >= 0 && c.EndUs > c.FirstUs).GroupBy(c => c.Compatibility))
        {
            var current = new List<ReplayProvenance>();
            long end = -1;
            foreach (var clip in group.OrderBy(c => c.FirstUs).ThenByDescending(c => c.EndUs)
                .ThenBy(c => c.Media.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id))
            {
                if (current.Count > 0 && clip.FirstUs >= end)
                {
                    if (current.Count > 1) result.Add(Build(current));
                    current.Clear();
                }
                current.Add(clip); end = Math.Max(current.Count == 1 ? -1 : end, clip.EndUs);
            }
            if (current.Count > 1) result.Add(Build(current));
        }
        return result.OrderBy(c => c.Clips[0].StartedUtcUs).ThenBy(c => c.FirstUs)
            .ThenBy(c => c.Clips[0].Id).ToArray();
    }

    internal static ReplayCluster Subset(ReplayCluster cluster, int first, int last, TimeSpan maximum)
    {
        if (first < 0 || last >= cluster.Clips.Count || last <= first)
            throw new ExportException("Select at least two consecutive clips within one cluster.");
        var selected = cluster.Clips.Skip(first).Take(last - first + 1).ToArray();
        long end = selected[0].EndUs;
        foreach (var clip in selected.Skip(1))
        {
            if (clip.Compatibility != selected[0].Compatibility || clip.FirstUs >= end)
                throw new ExportException("The selected clips do not form one proven overlapping interval.");
            end = Math.Max(end, clip.EndUs);
        }
        var plan = Build(selected);
        if (plan.UnionUs > maximum.Ticks / 10) throw new ExportException("The combined replay exceeds the configured maximum duration.");
        return plan;
    }

    private static ReplayCluster Build(IReadOnlyList<ReplayProvenance> clips) =>
        new(clips.ToArray(), clips[0].FirstUs, clips.Max(c => c.EndUs), checked(clips.Sum(c => c.DurationUs)));
}

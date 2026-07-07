using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal static class ArchiveStatusMerger
{
    // Pre-count for the RecordCount delta: partitions are REPLACED, so a re-materialized
    // month must adjust by (written − previousRows), never accumulate.
    public static async Task<long> CountDataRows(string partitionPath, CancellationToken ct = default)
    {
        if (!File.Exists(partitionPath))
            return 0;
        var lines = await File.ReadAllLinesAsync(partitionPath, ct);
        return Math.Max(0, lines.Length - 1);
    }

    // Archive data has fixed slots — any delta > 1×interval is a genuine source hole.
    // Unlike the streaming path (FeedCollectorBase.DetectGap, configurable multiplier),
    // archive months are complete-or-missing; sub-threshold jitter does not occur.
    public static List<DataGap> DetectGaps(List<(long Ts, string[] Row)> parsed, long intervalMs)
    {
        var gaps = new List<DataGap>();
        for (var i = 1; i < parsed.Count; i++)
        {
            var prev = parsed[i - 1].Ts;
            var curr = parsed[i].Ts;
            if (curr - prev > intervalMs)
                gaps.Add(new DataGap { FromMs = prev, ToMs = curr });
        }
        return gaps;
    }

    public static async Task MergeStatus(
        IFeedStatusStore feedStatusStore,
        string assetDir, string feedName, string interval,
        long monthFirst, long monthLast, long recordCountDelta,
        IReadOnlyList<DataGap> newGaps, CancellationToken ct = default)
    {
        var existing = await feedStatusStore.Load(assetDir, feedName, interval, ct);

        var firstTs = existing?.FirstTimestamp.HasValue == true
            ? Math.Min(existing.FirstTimestamp.Value, monthFirst)
            : monthFirst;
        var lastTs = existing?.LastTimestamp.HasValue == true
            ? Math.Max(existing.LastTimestamp.Value, monthLast)
            : monthLast;
        var recordCount = Math.Max(0, (existing?.RecordCount ?? 0) + recordCountDelta);

        IReadOnlyList<DataGap> existingGaps = existing?.Gaps ?? [];
        var dedupedNew = newGaps
            .Where(g => !existingGaps.Any(e => e.FromMs == g.FromMs && e.ToMs == g.ToMs))
            .ToList();
        IReadOnlyList<DataGap> mergedGaps = [.. existingGaps, .. dedupedNew];
        var health = mergedGaps.Count == 0 ? CollectionHealth.Healthy : CollectionHealth.Degraded;

        await feedStatusStore.Save(assetDir, feedName, interval, new FeedStatus
        {
            FeedName = feedName,
            Interval = interval,
            FirstTimestamp = firstTs,
            LastTimestamp = lastTs,
            LastRunUtc = DateTimeOffset.UtcNow,
            RecordCount = recordCount,
            Gaps = mergedGaps,
            Health = health
        }, ct);
    }
}

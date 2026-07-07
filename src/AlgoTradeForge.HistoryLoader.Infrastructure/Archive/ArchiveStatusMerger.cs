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
        // Stream line-by-line: tick partitions run to millions of rows; never materialize as string[].
        long lines = 0;
        using var reader = new StreamReader(partitionPath);
        while (await reader.ReadLineAsync(ct) is not null)
            lines++;
        return Math.Max(0, lines - 1);
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

        // The archive rewrote [monthFirst, monthLast] atomically, so its authoritative gaps are
        // newGaps. Drop stale gaps fully inside the touched month — a since-filled streaming gap
        // would otherwise be double-counted (its slots credited AND present as actual rows).
        var retainedGaps = (existing?.Gaps ?? [])
            .Where(g => !(g.FromMs >= monthFirst && g.ToMs <= monthLast))
            .ToList();
        var dedupedNew = newGaps
            .Where(g => !retainedGaps.Any(e => e.FromMs == g.FromMs && e.ToMs == g.ToMs))
            .ToList();
        IReadOnlyList<DataGap> mergedGaps = [.. retainedGaps, .. dedupedNew];
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
            Health = health,
            // Carry the interval-less coverage marker through the rebuild — only MarkCompleteMonth
            // adds to it; dropping it here wipes prior months on every per-month merge.
            CompleteMonths = existing?.CompleteMonths ?? []
        }, ct);
    }

    // Records one "yyyy-MM" month as completely materialized from a monthly archive zip
    // (coverage marker for interval-less feeds). Idempotent; keeps the list ordinal-sorted.
    public static async Task MarkCompleteMonth(
        IFeedStatusStore feedStatusStore, string assetDir, string feedName, string interval,
        string monthKey, CancellationToken ct = default)
    {
        var status = await feedStatusStore.Load(assetDir, feedName, interval, ct)
            ?? new FeedStatus { FeedName = feedName, Interval = interval };
        if (status.CompleteMonths.Contains(monthKey))
            return;

        var months = new List<string>(status.CompleteMonths) { monthKey };
        months.Sort(StringComparer.Ordinal);

        await feedStatusStore.Save(assetDir, feedName, interval, new FeedStatus
        {
            FeedName = status.FeedName,
            Interval = status.Interval,
            FirstTimestamp = status.FirstTimestamp,
            LastTimestamp = status.LastTimestamp,
            LastRunUtc = status.LastRunUtc,
            RecordCount = status.RecordCount,
            Gaps = status.Gaps,
            Health = status.Health,
            CompleteMonths = months
        }, ct);
    }
}

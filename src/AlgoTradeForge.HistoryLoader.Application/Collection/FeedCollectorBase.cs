using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public abstract class FeedCollectorBase(
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger logger) : IFeedCollector
{
    protected IFeedWriter FeedWriter { get; } = feedWriter;
    protected ISchemaManager SchemaManager { get; } = schemaManager;
    protected IFeedStatusStore FeedStatusStore { get; } = feedStatusStore;
    protected ILogger Logger { get; } = logger;

    public abstract string FeedName { get; }
    public virtual bool SupportsSpot => false;

    public abstract Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct);

    protected void AdjustFromMs(string assetDir, string feedName, string interval, ref long fromMs)
    {
        var resumeTs = FeedWriter.ResumeFrom(assetDir, feedName, interval);
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;
    }

    protected static long ComputeExpectedMs(string interval) =>
        string.IsNullOrEmpty(interval)
            ? 0
            : (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

    protected static bool DetectGap(long currentTs, long previousTs, long expectedMs, double multiplier, List<DataGap> gaps)
    {
        if (previousTs > 0 && expectedMs > 0 && currentTs - previousTs > expectedMs * multiplier)
        {
            gaps.Add(new DataGap { FromMs = previousTs, ToMs = currentTs });
            return true;
        }
        return false;
    }

    protected void UpdateFeedStatus(
        string assetDir,
        string feedName,
        string interval,
        long? firstTs,
        long lastTs,
        long recordCount,
        CollectionHealth health = CollectionHealth.Healthy,
        List<DataGap>? newGaps = null)
    {
        var existing = FeedStatusStore.Load(assetDir, feedName, interval);

        List<DataGap> mergedGaps = existing?.Gaps ?? [];
        if (newGaps is { Count: > 0 })
        {
            mergedGaps = [.. mergedGaps, .. newGaps];
        }

        var status = new FeedStatus
        {
            FeedName = feedName,
            Interval = interval,
            FirstTimestamp = existing?.FirstTimestamp ?? firstTs,
            LastTimestamp = lastTs,
            LastRunUtc = DateTimeOffset.UtcNow,
            RecordCount = (existing?.RecordCount ?? 0) + recordCount,
            Gaps = mergedGaps,
            Health = health
        };

        FeedStatusStore.Save(assetDir, feedName, interval, status);
    }
}

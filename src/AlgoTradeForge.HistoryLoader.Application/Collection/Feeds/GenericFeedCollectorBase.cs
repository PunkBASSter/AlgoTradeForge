using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public abstract class GenericFeedCollectorBase(
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    protected abstract string[] Columns { get; }

    protected abstract IAsyncEnumerable<FeedRecord> FetchAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    public override async Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var interval = feedConfig.Interval;

        AdjustFromMs(assetDir, FeedName, interval, ref fromMs);
        SchemaManager.EnsureSchema(assetDir, FeedName, interval, Columns);

        // Resume from last written timestamp — skip records already stored.
        var resumeTs = FeedWriter.ResumeFrom(assetDir, FeedName, interval);

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();
        long expectedMs = ComputeExpectedMs(interval);

        await foreach (var record in FetchAsync(assetConfig.Symbol, interval, fromMs, toMs, ct))
        {
            if (resumeTs.HasValue && record.TimestampMs <= resumeTs.Value)
                continue;

            try
            {
                FeedWriter.Write(assetDir, FeedName, interval, Columns, record);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedName, assetDir);
                UpdateFeedStatus(assetDir, FeedName, interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
                throw;
            }

            DetectGap(record.TimestampMs, previousTs, expectedMs, feedConfig.GapThresholdMultiplier, gaps);
            previousTs = record.TimestampMs;

            firstTs ??= record.TimestampMs;
            lastTs = record.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, FeedName, interval, firstTs, lastTs, recordCount,
                newGaps: gaps);

        Logger.LogInformation(
            "Collected {Count} {Feed} records for {AssetDir}/{Interval}",
            recordCount, FeedName, assetDir, interval);
    }
}

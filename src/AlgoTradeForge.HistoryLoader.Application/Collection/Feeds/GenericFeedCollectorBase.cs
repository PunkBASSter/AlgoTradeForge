using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public abstract class GenericFeedCollectorBase(
    IFeedFetcherFactory feedFetcherFactory,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    protected abstract string[] Columns { get; }

    /// <summary>
    /// Override to attach auto-apply metadata (e.g. funding rate) to the feed schema.
    /// </summary>
    protected virtual AutoApplySpec? GetAutoApplySpec() => null;

    /// <summary>
    /// Override to supply a fixed expected interval for gap detection
    /// when the feed does not use a standard interval string.
    /// </summary>
    protected virtual long GetExpectedIntervalMs(string interval) => ComputeExpectedMs(interval);

    /// <summary>
    /// Override to force a specific exchange key (e.g. always-futures for funding rates).
    /// </summary>
    protected virtual string ResolveExchangeKey(CollectionAsset asset) =>
        ExchangeKeys.Resolve(asset);

    protected IAsyncEnumerable<FeedRecord> FetchAsync(
        CollectionAsset asset,
        string symbol, string? interval, long fromMs, long toMs, CancellationToken ct)
    {
        var fetcher = feedFetcherFactory.Create(ResolveExchangeKey(asset), FeedName);
        return fetcher.FetchAsync(symbol, interval, fromMs, toMs, ct);
    }

    public override async Task Collect(
        CollectionAsset asset,
        CollectionFeed feed,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct = default)
    {
        var interval = feed.Interval;

        var (resumeTs, adjustedFromMs) = await ResolveFromMs(assetDir, FeedName, interval, fromMs, ct);
        fromMs = adjustedFromMs;
        await SchemaManager.EnsureSchema(assetDir, FeedName, interval, Columns, GetAutoApplySpec(), ct);

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();
        long expectedMs = GetExpectedIntervalMs(interval);

        await foreach (var record in FetchAsync(asset, asset.Venue.ApiSymbol, interval, fromMs, toMs, ct))
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
                await UpdateFeedStatus(assetDir, FeedName, interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps, ct);
                throw;
            }

            DetectGap(record.TimestampMs, previousTs, expectedMs, options.CurrentValue.GapThresholdMultiplier, gaps);
            previousTs = record.TimestampMs;

            firstTs ??= record.TimestampMs;
            lastTs = record.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            await UpdateFeedStatus(assetDir, FeedName, interval, firstTs, lastTs, recordCount,
                newGaps: gaps, ct: ct);

        if (recordCount > 0)
            Logger.LogInformation(
                "Collected {Count} {Feed} records for {AssetDir}/{Interval}",
                recordCount, FeedName, assetDir, interval);
        else
            Logger.LogDebug(
                "Collected 0 {Feed} records for {AssetDir}/{Interval}",
                FeedName, assetDir, interval);
    }
}

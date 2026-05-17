using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

/// <summary>
/// Collects Binance Spot and USDT-M Futures aggregate trades into daily-partitioned CSVs.
/// Routes to the spot or futures HTTP client based on <see cref="AssetCollectionConfig.Type"/>
/// via <see cref="ExchangeKeys.Resolve"/>.
/// </summary>
/// <remarks>
/// Does NOT extend <see cref="GenericFeedCollectorBase"/>: that base assumes monthly partitions
/// and timestamp-based resume. Ticks partition daily and resume by <c>(agg_id, ts)</c>, with the
/// writer dedupping by <c>agg_id</c> (multiple trades regularly share a millisecond).
/// </remarks>
public sealed class AggTradeFeedCollector(
    IFeedFetcherFactory feedFetcherFactory,
    ITickFeedWriter tickWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<AggTradeFeedCollector> logger) : IFeedCollector
{
    private static readonly string[] TickColumns =
        ["price", "qty", "is_buyer_maker", "agg_id"];

    public string FeedName => FeedNames.Ticks;

    public bool SupportsSpot => true;

    public async Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Schema first so concurrent readers see the feed entry even before the first row lands.
        schemaManager.EnsureSchema(assetDir, FeedNames.Ticks, "", TickColumns, autoApply: null);

        // Re-fetch the boundary ms (inclusive) and let the writer's agg_id dedup drop
        // already-persisted records — ts+1 advancement would silently skip ticks that share a
        // millisecond.
        var resume = await tickWriter.ResumeFrom(assetDir, ct);
        if (resume is { } r && r.LastTsMs >= fromMs)
            fromMs = r.LastTsMs;

        var exchangeKey = ExchangeKeys.Resolve(assetConfig);
        var fetcher = feedFetcherFactory.Create(exchangeKey, FeedNames.Ticks);

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;

        try
        {
            await foreach (var record in fetcher.FetchAsync(assetConfig.Symbol, interval: "", fromMs, toMs, ct))
            {
                tickWriter.Write(assetDir, record);

                firstTs ??= record.TimestampMs;
                lastTs = record.TimestampMs;
                recordCount++;
            }
        }
        catch (IOException ex)
        {
            logger.LogCritical(ex, "Disk I/O error writing ticks for {AssetDir}", assetDir);
            await UpdateFeedStatus(assetDir, firstTs, lastTs, recordCount, CollectionHealth.Error, ct);
            throw;
        }

        if (recordCount > 0)
        {
            await UpdateFeedStatus(assetDir, firstTs, lastTs, recordCount, ct: ct);
            logger.LogInformation(
                "Collected {Count} tick records for {AssetDir}", recordCount, assetDir);
        }
        else
        {
            logger.LogDebug("Collected 0 tick records for {AssetDir}", assetDir);
        }
    }

    private async Task UpdateFeedStatus(
        string assetDir, long? firstTs, long lastTs, long recordCount,
        CollectionHealth health = CollectionHealth.Healthy,
        CancellationToken ct = default)
    {
        var existing = await feedStatusStore.Load(assetDir, FeedNames.Ticks, "", ct);
        var status = new FeedStatus
        {
            FeedName = FeedNames.Ticks,
            Interval = "",
            FirstTimestamp = existing?.FirstTimestamp ?? firstTs,
            LastTimestamp = lastTs,
            LastRunUtc = DateTimeOffset.UtcNow,
            RecordCount = (existing?.RecordCount ?? 0) + recordCount,
            Gaps = existing?.Gaps ?? [],
            Health = health,
        };
        await feedStatusStore.Save(assetDir, FeedNames.Ticks, "", status, ct);
    }
}

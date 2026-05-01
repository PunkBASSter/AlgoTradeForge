using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

/// <summary>
/// Collects Binance USDT-M Futures aggregate trades into daily-partitioned CSVs (TRD §3.5).
/// </summary>
/// <remarks>
/// Does <strong>not</strong> extend <see cref="GenericFeedCollectorBase"/> because that base
/// assumes monthly partitions and timestamp-based resume. Tick collection partitions daily
/// and resumes by <c>(agg_id, ts)</c>, with the writer dedupping by <c>agg_id</c> rather
/// than <c>ts</c> (multiple aggregated trades regularly share a millisecond).
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

    // Perp-only for Phase 2a. Spot tick ingestion is a Phase 2a follow-up.
    public bool SupportsSpot => false;

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

        // Resume protocol: re-fetch the boundary millisecond (inclusive) and let the writer's
        // agg_id dedup drop already-persisted records. This handles the case of multiple ticks
        // sharing a single millisecond, where ts+1 advancement would silently skip records.
        var resume = tickWriter.ResumeFrom(assetDir);
        if (resume is { } r && r.LastTsMs >= fromMs)
            fromMs = r.LastTsMs;

        var exchangeKey = ExchangeKeys.Futures(assetConfig.Exchange);
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
            UpdateFeedStatus(assetDir, firstTs, lastTs, recordCount, CollectionHealth.Error);
            throw;
        }

        if (recordCount > 0)
        {
            UpdateFeedStatus(assetDir, firstTs, lastTs, recordCount);
            logger.LogInformation(
                "Collected {Count} tick records for {AssetDir}", recordCount, assetDir);
        }
        else
        {
            logger.LogDebug("Collected 0 tick records for {AssetDir}", assetDir);
        }
    }

    private void UpdateFeedStatus(
        string assetDir, long? firstTs, long lastTs, long recordCount,
        CollectionHealth health = CollectionHealth.Healthy)
    {
        var existing = feedStatusStore.Load(assetDir, FeedNames.Ticks, "");
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
        feedStatusStore.Save(assetDir, FeedNames.Ticks, "", status);
    }
}

using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class SymbolCollector(
    IFuturesDataFetcher futuresClient,
    ISpotDataFetcher? spotClient,
    ICandleWriter candleWriter,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<SymbolCollector> logger)
{
    private static readonly string[] CandleExtColumns =
        ["quote_vol", "trade_count", "taker_buy_vol", "taker_buy_quote_vol"];

    private static readonly string[] FundingRateColumns = ["rate", "mark_price"];

    private static readonly string[] OiColumns = ["oi", "oi_usd"];

    private static readonly string[] LsRatioColumns = ["long_pct", "short_pct", "ratio"];

    private static readonly string[] TakerVolumeColumns = ["buy_vol_usd", "sell_vol_usd", "ratio"];

    private static readonly string[] MarkPriceColumns = ["o", "h", "l", "c"];

    private static readonly string[] PositionRatioColumns = ["long_pct", "short_pct", "ratio"];

    private static readonly string[] LiquidationColumns = ["side", "price", "qty", "notional_usd"];

    public async Task CollectFeedAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var feedName = feedConfig.Name;
        var interval = feedConfig.Interval;

        // Spot assets only support the "candles" feed.
        if (assetConfig.Type == "spot" && feedName != "candles")
        {
            logger.LogWarning(
                "Spot assets only support candles feed, skipping {Feed} for {Symbol}",
                feedName, assetConfig.Symbol);
            return;
        }

        logger.LogInformation(
            "Collecting {Feed}/{Interval} for {Symbol} from {From} to {To}",
            feedName, interval, assetConfig.Symbol, fromMs, toMs);

        // Guard: HTTP 400 means the symbol may be delisted — skip gracefully.
        try
        {
            switch (feedName)
            {
                case "candles":
                    await CollectCandlesAsync(assetConfig, assetDir, interval, fromMs, toMs, ct);
                    break;
                case "funding-rate":
                    await CollectFundingRateAsync(assetConfig, assetDir, fromMs, toMs, ct);
                    break;
                case "open-interest":
                    await CollectGenericFeedAsync(assetDir, "open-interest", interval, OiColumns,
                        futuresClient.FetchOpenInterestAsync(assetConfig.Symbol, interval, fromMs, toMs, ct), ct);
                    break;
                case "ls-ratio-global":
                    await CollectGenericFeedAsync(assetDir, "ls-ratio-global", interval, LsRatioColumns,
                        futuresClient.FetchGlobalLongShortRatioAsync(assetConfig.Symbol, interval, fromMs, toMs, ct), ct);
                    break;
                case "ls-ratio-top-accounts":
                    await CollectGenericFeedAsync(assetDir, "ls-ratio-top-accounts", interval, LsRatioColumns,
                        futuresClient.FetchTopAccountRatioAsync(assetConfig.Symbol, interval, fromMs, toMs, ct), ct);
                    break;
                case "taker-volume":
                    await CollectGenericFeedAsync(assetDir, "taker-volume", interval, TakerVolumeColumns,
                        futuresClient.FetchTakerVolumeAsync(assetConfig.Symbol, interval, fromMs, toMs, ct), ct);
                    break;
                case "mark-price":
                    await CollectMarkPriceAsync(assetConfig, assetDir, interval, fromMs, toMs, ct);
                    break;
                case "ls-ratio-top-positions":
                    await CollectGenericFeedAsync(assetDir, "ls-ratio-top-positions", interval, PositionRatioColumns,
                        futuresClient.FetchTopPositionRatioAsync(assetConfig.Symbol, interval, fromMs, toMs, ct), ct);
                    break;
                case "liquidations":
                    await CollectGenericFeedAsync(assetDir, "liquidations", interval, LiquidationColumns,
                        futuresClient.FetchLiquidationsAsync(assetConfig.Symbol, fromMs, toMs, ct), ct);
                    break;
                default:
                    logger.LogWarning("Unknown feed: {Feed} for {Symbol}", feedName, assetConfig.Symbol);
                    break;
            }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("400"))
        {
            logger.LogWarning("Symbol may be delisted, skipping: {Symbol}", assetConfig.Symbol);
        }
    }

    private async Task CollectCandlesAsync(
        AssetCollectionConfig assetConfig,
        string assetDir,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Ensure feeds.json has candle config
        schemaManager.EnsureCandleConfig(assetDir, assetConfig.DecimalDigits, interval);

        // Resume from last written timestamp
        var resumeTs = candleWriter.ResumeFrom(assetDir, interval);
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        bool isSpot = assetConfig.Type == "spot";

        // Ensure candle-ext schema only for non-spot assets.
        if (!isSpot)
        {
            schemaManager.EnsureSchema(assetDir, "candle-ext", interval, CandleExtColumns);
        }

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();

        // Gap detection: expected interval in milliseconds (non-empty interval only).
        long expectedMs = string.IsNullOrEmpty(interval)
            ? 0
            : (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

        // Route to spot or futures client based on asset type.
        IAsyncEnumerable<KlineRecord> klineSource = isSpot
            ? spotClient!.FetchKlinesAsync(assetConfig.Symbol, interval, fromMs, toMs, ct)
            : futuresClient.FetchKlinesAsync(assetConfig.Symbol, interval, fromMs, toMs, ct);

        await foreach (var kline in klineSource)
        {
            try
            {
                // Write int64 OHLCV candle
                candleWriter.Write(assetDir, interval, kline, assetConfig.DecimalDigits);
            }
            catch (IOException ex)
            {
                logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", "candles", assetDir);
                UpdateFeedStatus(assetDir, "candles", interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
                throw;
            }

            // Write extended fields as double feed (futures only).
            if (!isSpot)
            {
                var extRecord = new FeedRecord(kline.TimestampMs,
                [
                    (double)kline.QuoteVolume,
                    kline.TradeCount,
                    (double)kline.TakerBuyVolume,
                    (double)kline.TakerBuyQuoteVolume
                ]);
                try
                {
                    feedWriter.Write(assetDir, "candle-ext", interval, CandleExtColumns, extRecord);
                }
                catch (IOException ex)
                {
                    logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", "candle-ext", assetDir);
                    UpdateFeedStatus(assetDir, "candle-ext", interval, firstTs, lastTs, recordCount,
                        CollectionHealth.Error, gaps);
                    throw;
                }
            }

            // Gap detection: if timestamp jump exceeds 2x expected interval
            if (previousTs > 0 && expectedMs > 0)
            {
                if (kline.TimestampMs - previousTs > expectedMs * 2)
                {
                    gaps.Add(new DataGap { FromMs = previousTs, ToMs = kline.TimestampMs });
                }
            }
            previousTs = kline.TimestampMs;

            firstTs ??= kline.TimestampMs;
            lastTs = kline.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
        {
            UpdateFeedStatus(assetDir, "candles", interval, firstTs, lastTs, recordCount,
                newGaps: gaps);
            if (!isSpot)
                UpdateFeedStatus(assetDir, "candle-ext", interval, firstTs, lastTs, recordCount,
                    newGaps: gaps);
        }

        logger.LogInformation(
            "Collected {Count} candle records for {Symbol}/{Interval}",
            recordCount, assetConfig.Symbol, interval);
    }

    private async Task CollectFundingRateAsync(
        AssetCollectionConfig assetConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Ensure feeds.json schema with auto-apply config
        schemaManager.EnsureSchema(assetDir, "funding-rate", "", FundingRateColumns,
            new AutoApplySpec("FundingRate", "rate"));

        // Resume from last written timestamp
        var resumeTs = feedWriter.ResumeFrom(assetDir, "funding-rate", "");
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;

        await foreach (var record in futuresClient.FetchFundingRatesAsync(
            assetConfig.Symbol, fromMs, toMs, ct))
        {
            try
            {
                feedWriter.Write(assetDir, "funding-rate", "", FundingRateColumns, record);
            }
            catch (IOException ex)
            {
                logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", "funding-rate", assetDir);
                UpdateFeedStatus(assetDir, "funding-rate", "", firstTs, lastTs, recordCount,
                    CollectionHealth.Error);
                throw;
            }

            firstTs ??= record.TimestampMs;
            lastTs = record.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, "funding-rate", "", firstTs, lastTs, recordCount);

        logger.LogInformation(
            "Collected {Count} funding rate records for {Symbol}",
            recordCount, assetConfig.Symbol);
    }

    private async Task CollectMarkPriceAsync(
        AssetCollectionConfig assetConfig,
        string assetDir,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Ensure feeds.json schema for "mark-price" feed.
        schemaManager.EnsureSchema(assetDir, "mark-price", interval, MarkPriceColumns);

        // Resume from last written timestamp.
        var resumeTs = feedWriter.ResumeFrom(assetDir, "mark-price", interval);
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();

        // Gap detection: expected interval in milliseconds.
        long expectedMs = string.IsNullOrEmpty(interval)
            ? 0
            : (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

        await foreach (var kline in futuresClient.FetchMarkPriceKlinesAsync(
            assetConfig.Symbol, interval, fromMs, toMs, ct))
        {
            var feedRecord = new FeedRecord(kline.TimestampMs,
            [
                (double)kline.Open,
                (double)kline.High,
                (double)kline.Low,
                (double)kline.Close
            ]);

            try
            {
                feedWriter.Write(assetDir, "mark-price", interval, MarkPriceColumns, feedRecord);
            }
            catch (IOException ex)
            {
                logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", "mark-price", assetDir);
                UpdateFeedStatus(assetDir, "mark-price", interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
                throw;
            }

            // Gap detection: if timestamp jump exceeds 2x expected interval
            if (previousTs > 0 && expectedMs > 0)
            {
                if (kline.TimestampMs - previousTs > expectedMs * 2)
                {
                    gaps.Add(new DataGap { FromMs = previousTs, ToMs = kline.TimestampMs });
                }
            }
            previousTs = kline.TimestampMs;

            firstTs ??= kline.TimestampMs;
            lastTs = kline.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, "mark-price", interval, firstTs, lastTs, recordCount,
                newGaps: gaps);

        logger.LogInformation(
            "Collected {Count} mark-price records for {Symbol}/{Interval}",
            recordCount, assetConfig.Symbol, interval);
    }

    private async Task CollectGenericFeedAsync(
        string assetDir,
        string feedName,
        string interval,
        string[] columns,
        IAsyncEnumerable<FeedRecord> records,
        CancellationToken ct)
    {
        // Ensure feeds.json schema
        schemaManager.EnsureSchema(assetDir, feedName, interval, columns);

        // Resume from last written timestamp — skip records already stored.
        var resumeTs = feedWriter.ResumeFrom(assetDir, feedName, interval);

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();

        // Gap detection: expected interval in milliseconds (event-based feeds have empty interval).
        long expectedMs = string.IsNullOrEmpty(interval)
            ? 0
            : (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

        await foreach (var record in records)
        {
            if (resumeTs.HasValue && record.TimestampMs <= resumeTs.Value)
                continue;

            try
            {
                feedWriter.Write(assetDir, feedName, interval, columns, record);
            }
            catch (IOException ex)
            {
                logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", feedName, assetDir);
                UpdateFeedStatus(assetDir, feedName, interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
                throw;
            }

            // Gap detection: if timestamp jump exceeds 2x expected interval
            if (previousTs > 0 && expectedMs > 0)
            {
                if (record.TimestampMs - previousTs > expectedMs * 2)
                {
                    gaps.Add(new DataGap { FromMs = previousTs, ToMs = record.TimestampMs });
                }
            }
            previousTs = record.TimestampMs;

            firstTs ??= record.TimestampMs;
            lastTs = record.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, feedName, interval, firstTs, lastTs, recordCount,
                newGaps: gaps);

        logger.LogInformation(
            "Collected {Count} {Feed} records for {AssetDir}/{Interval}",
            recordCount, feedName, assetDir, interval);
    }

    private void UpdateFeedStatus(
        string assetDir,
        string feedName,
        string interval,
        long? firstTs,
        long lastTs,
        long recordCount,
        CollectionHealth health = CollectionHealth.Healthy,
        List<DataGap>? newGaps = null)
    {
        var existing = feedStatusStore.Load(assetDir, feedName);

        // Merge new gaps with any existing ones.
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

        feedStatusStore.Save(assetDir, feedName, status);
    }
}

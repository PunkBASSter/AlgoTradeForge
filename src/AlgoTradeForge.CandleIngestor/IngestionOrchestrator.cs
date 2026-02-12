using AlgoTradeForge.CandleIngestor.State;
using AlgoTradeForge.CandleIngestor.Storage;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.CandleIngestor;

public sealed class IngestionOrchestrator(
    IServiceProvider serviceProvider,
    CsvCandleWriter writer,
    IngestionStateManager stateManager,
    IOptions<CandleIngestorOptions> options,
    ILogger<IngestionOrchestrator> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var config = options.Value;
        logger.LogInformation("IngestionStarted: {AssetCount} assets configured, DataRoot={DataRoot}",
            config.Assets.Count, config.DataRoot);

        foreach (var assetConfig in config.Assets)
        {
            ct.ThrowIfCancellationRequested();

            if (assetConfig.DecimalDigits < 0 || assetConfig.DecimalDigits > 18)
            {
                logger.LogError("Invalid DecimalDigits {DecimalDigits} for {Symbol}, skipping",
                    assetConfig.DecimalDigits, assetConfig.Symbol);
                continue;
            }

            try
            {
                await IngestAssetAsync(assetConfig, config, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AssetIngestionFailed: {Symbol} on {Exchange}", assetConfig.Symbol, assetConfig.Exchange);
            }
        }

        var heartbeatPath = Path.Combine(config.DataRoot, "candle-ingestor-heartbeat.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeatPath)!);
        await File.WriteAllTextAsync(heartbeatPath, DateTimeOffset.UtcNow.ToString("O"), ct);

        logger.LogInformation("IngestionCompleted");
    }

    private async Task IngestAssetAsync(IngestorAssetConfig assetConfig, CandleIngestorOptions config, CancellationToken ct)
    {
        logger.LogInformation("AssetIngestionStarted: {Symbol} on {Exchange}", assetConfig.Symbol, assetConfig.Exchange);

        writer.Reset();
        var adapter = serviceProvider.GetRequiredKeyedService<IDataAdapter>(assetConfig.Exchange);

        // Phase 0 — Load state
        var state = stateManager.Load(assetConfig.Exchange, assetConfig.Symbol);
        if (state is null)
        {
            var lastTimestamp = writer.GetLastTimestamp(assetConfig.Exchange, assetConfig.Symbol);
            if (lastTimestamp.HasValue)
            {
                state = stateManager.Bootstrap(assetConfig.Exchange, assetConfig.Symbol, writer);
            }
            else
            {
                state = new IngestionState();
            }
        }

        // Phase 1 — Close gaps
        await CloseGapsAsync(state, assetConfig, adapter, ct);

        // Phase 2 — Extend forward
        await ExtendForwardAsync(state, assetConfig, adapter, ct);

        // Phase 3 — Save state
        state.LastRunUtc = DateTimeOffset.UtcNow;
        stateManager.Save(assetConfig.Exchange, assetConfig.Symbol, state);

        logger.LogInformation("AssetIngestionCompleted: {Symbol} on {Exchange}", assetConfig.Symbol, assetConfig.Exchange);
    }

    private async Task CloseGapsAsync(
        IngestionState state,
        IngestorAssetConfig assetConfig,
        IDataAdapter adapter,
        CancellationToken ct)
    {
        if (state.Gaps.Count == 0)
            return;

        logger.LogInformation("ClosingGaps: {Symbol}, {GapCount} gaps to close",
            assetConfig.Symbol, state.Gaps.Count);

        var interval = assetConfig.SmallestInterval;
        var remainingGaps = new List<IngestionGap>();

        foreach (var gap in state.Gaps)
        {
            ct.ThrowIfCancellationRequested();

            var fetchFrom = gap.From - interval;
            var fetchTo = gap.To + interval;

            logger.LogDebug("ClosingGap: {Symbol}, from={From} to={To}", assetConfig.Symbol, gap.From, gap.To);

            DateTimeOffset? previousTimestamp = null;
            var candleCount = 0L;
            var subGaps = new List<IngestionGap>();

            await foreach (var candle in adapter.FetchCandlesAsync(
                assetConfig.Symbol, interval, fetchFrom, fetchTo, ct))
            {
                if (previousTimestamp.HasValue && candle.Timestamp <= previousTimestamp.Value)
                    continue;

                if (previousTimestamp.HasValue && (candle.Timestamp - previousTimestamp.Value) > interval)
                {
                    subGaps.Add(new IngestionGap
                    {
                        From = previousTimestamp.Value,
                        To = candle.Timestamp
                    });
                }

                writer.WriteCandle(candle, assetConfig.Exchange, assetConfig.Symbol, assetConfig.DecimalDigits);
                previousTimestamp = candle.Timestamp;
                candleCount++;
            }

            if (candleCount == 0)
            {
                remainingGaps.Add(gap);
            }
            else
            {
                remainingGaps.AddRange(subGaps);
            }

            writer.Flush();
        }

        state.Gaps = remainingGaps;

        if (remainingGaps.Count > 0)
            logger.LogWarning("GapsRemaining: {Symbol}, {Count} gaps still open", assetConfig.Symbol, remainingGaps.Count);
        else
            logger.LogInformation("AllGapsClosed: {Symbol}", assetConfig.Symbol);
    }

    private async Task ExtendForwardAsync(
        IngestionState state,
        IngestorAssetConfig assetConfig,
        IDataAdapter adapter,
        CancellationToken ct)
    {
        var interval = assetConfig.SmallestInterval;
        DateTimeOffset fetchFrom;

        if (state.LastTimestamp.HasValue)
        {
            fetchFrom = state.LastTimestamp.Value + interval;

            if (fetchFrom >= DateTimeOffset.UtcNow)
            {
                logger.LogInformation("AssetUpToDate: {Symbol}, last={LastTimestamp}",
                    assetConfig.Symbol, state.LastTimestamp.Value);
                return;
            }
        }
        else
        {
            fetchFrom = new DateTimeOffset(assetConfig.HistoryStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        var fetchTo = DateTimeOffset.UtcNow;
        var batchCount = 0L;
        var previousTimestamp = state.LastTimestamp;

        await foreach (var candle in adapter.FetchCandlesAsync(
            assetConfig.Symbol, interval, fetchFrom, fetchTo, ct))
        {
            // Skip non-monotonic candles (source data may contain overlapping segments)
            if (previousTimestamp.HasValue && candle.Timestamp <= previousTimestamp.Value)
                continue;

            if (previousTimestamp.HasValue && (candle.Timestamp - previousTimestamp.Value) > interval)
            {
                state.Gaps.Add(new IngestionGap
                {
                    From = previousTimestamp.Value,
                    To = candle.Timestamp
                });

                logger.LogWarning("GapDetected: {Symbol}, from={From} to={To}",
                    assetConfig.Symbol, previousTimestamp.Value, candle.Timestamp);
            }

            writer.WriteCandle(candle, assetConfig.Exchange, assetConfig.Symbol, assetConfig.DecimalDigits);
            previousTimestamp = candle.Timestamp;
            batchCount++;

            if (state.FirstTimestamp is null)
                state.FirstTimestamp = candle.Timestamp;

            if (batchCount % 10000 == 0)
            {
                writer.Flush();
                logger.LogInformation("BatchFetched: {Symbol}, {Count} candles processed",
                    assetConfig.Symbol, batchCount);
            }
        }

        writer.Flush();

        if (previousTimestamp.HasValue)
            state.LastTimestamp = previousTimestamp;

        logger.LogInformation("ExtendForwardCompleted: {Symbol}, {TotalCandles} candles fetched",
            assetConfig.Symbol, batchCount);
    }
}

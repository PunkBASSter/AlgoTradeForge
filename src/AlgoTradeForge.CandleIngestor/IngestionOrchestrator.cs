using AlgoTradeForge.CandleIngestor.Storage;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.CandleIngestor;

public sealed class IngestionOrchestrator(
    IServiceProvider serviceProvider,
    CsvCandleWriter writer,
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

        var adapter = serviceProvider.GetRequiredKeyedService<IDataAdapter>(assetConfig.Exchange);

        var lastTimestamp = writer.GetLastTimestamp(assetConfig.Exchange, assetConfig.Symbol);
        DateTimeOffset fetchFrom;

        if (lastTimestamp.HasValue)
        {
            fetchFrom = lastTimestamp.Value + assetConfig.SmallestInterval;

            if (fetchFrom >= DateTimeOffset.UtcNow)
            {
                logger.LogInformation("AssetUpToDate: {Symbol}, last={LastTimestamp}", assetConfig.Symbol, lastTimestamp.Value);
                return;
            }

            var gap = DateTimeOffset.UtcNow - fetchFrom;
            if (gap > assetConfig.SmallestInterval * 2)
            {
                logger.LogWarning("GapDetected: {Symbol}, last={LastTimestamp}, gap={Gap}",
                    assetConfig.Symbol, lastTimestamp.Value, gap);
            }
        }
        else
        {
            fetchFrom = new DateTimeOffset(assetConfig.HistoryStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        var fetchTo = DateTimeOffset.UtcNow;
        var batchCount = 0L;

        await foreach (var candle in adapter.FetchCandlesAsync(
            assetConfig.Symbol,
            assetConfig.SmallestInterval,
            fetchFrom,
            fetchTo,
            ct))
        {
            writer.WriteCandle(candle, assetConfig.Exchange, assetConfig.Symbol, assetConfig.DecimalDigits);
            batchCount++;

            if (batchCount % 10000 == 0)
            {
                writer.Flush();
                logger.LogInformation("BatchFetched: {Symbol}, {Count} candles processed", assetConfig.Symbol, batchCount);
            }
        }

        writer.Flush();
        logger.LogInformation("AssetIngestionCompleted: {Symbol}, {TotalCandles} candles total", assetConfig.Symbol, batchCount);
    }
}

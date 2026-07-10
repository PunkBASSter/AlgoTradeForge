using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexWorkProcessor(
    IHistoryIndex index,
    IFeedMonthScanner scanner,
    ISchemaManager schemaManager,
    IFeedStatusStore statusStore,
    IIndexRebuilder rebuilder,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<IndexWorkProcessor> logger)
{
    public Task Process(IndexWork work, CancellationToken ct = default) => work switch
    {
        IndexWork.FeedTouched f => ProcessFeed(f, ct),
        IndexWork.ManifestTouched m => ProcessManifest(m, ct),
        IndexWork.Rebuild r => rebuilder.Run(r.JobId, ct),
        _ => Task.CompletedTask,
    };

    private async Task ProcessFeed(IndexWork.FeedTouched f, CancellationToken ct)
    {
        var key = AssetDirKey.FromPath(options.CurrentValue.DataRoot, f.AssetDir);
        if (key is null)
        {
            logger.LogWarning("Index: asset dir outside DataRoot, skipped: {AssetDir}", f.AssetDir);
            return;
        }
        var (exchange, dir) = key.Value;

        var status = await statusStore.Load(f.AssetDir, f.FeedName, f.Interval, ct);
        if (status is not null)
        {
            await index.UpsertFeedStatus(new FeedStatusIndexRow(
                exchange, dir, f.FeedName, f.Interval,
                status.FirstTimestamp, status.LastTimestamp, status.RecordCount,
                status.Health.ToString(),
                JsonSerializer.Serialize(status.Gaps, ManifestJson.Options),
                JsonSerializer.Serialize(status.CompleteMonths, ManifestJson.Options)), ct);
        }

        if (string.IsNullOrEmpty(f.Interval)) return;

        var known = (await index.GetMonths(exchange, dir, f.FeedName, f.Interval, ct))
            .ToDictionary(m => m.Month);
        var months = await scanner.Scan(Path.Combine(f.AssetDir, f.FeedName), f.Interval, known, ct);
        await index.ReplaceMonths(exchange, dir, f.FeedName, f.Interval, months, ct);
    }

    private async Task ProcessManifest(IndexWork.ManifestTouched m, CancellationToken ct)
    {
        var key = AssetDirKey.FromPath(options.CurrentValue.DataRoot, m.AssetDir);
        if (key is null) return;
        var (exchange, dir) = key.Value;

        var manifest = await schemaManager.Load(m.AssetDir, ct);
        if (manifest is null)
        {
            await index.RemoveAsset(exchange, dir, ct);
            return;
        }

        var (symbol, type) = AssetDirectoryClassifier.Classify(exchange, dir);
        await index.UpsertAsset(new AssetIndexRow(
            exchange, dir, symbol, type,
            JsonSerializer.Serialize(manifest, ManifestJson.Options)), ct);
    }
}

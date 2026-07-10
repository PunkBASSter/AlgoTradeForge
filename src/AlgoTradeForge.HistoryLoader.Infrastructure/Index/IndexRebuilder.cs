using System.Text.Json;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

public sealed class IndexRebuilder(
    IFileStorage storage,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ISchemaManager schemaManager,
    IFeedStatusStore statusStore,
    IFeedMonthScanner scanner,
    IHistoryIndex index,
    ILogger<IndexRebuilder> logger) : IIndexRebuilder
{
    public async Task Run(string jobId, CancellationToken ct = default)
    {
        try
        {
            var dataRoot = options.CurrentValue.DataRoot;
            var dirs = await ScanAssetDirs(dataRoot, ct);
            var keepAssets = new List<(string, string)>();

            for (var i = 0; i < dirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (exchange, dir) = dirs[i];
                var assetDir = Path.Combine(dataRoot, exchange, dir);
                var manifest = await schemaManager.Load(assetDir, ct);
                if (manifest is null) continue;

                keepAssets.Add((exchange, dir));
                var (symbol, type) = AssetDirectoryClassifier.Classify(exchange, dir);
                await index.UpsertAsset(new AssetIndexRow(exchange, dir, symbol, type,
                    JsonSerializer.Serialize(manifest, ManifestJson.Options)), ct);

                var keepFeeds = KeepFeeds.Derive(manifest);

                foreach (var (feedName, interval) in keepFeeds)
                {
                    var status = await statusStore.Load(assetDir, feedName, interval, ct);
                    if (status is not null)
                        await index.UpsertFeedStatus(new FeedStatusIndexRow(
                            exchange, dir, feedName, interval,
                            status.FirstTimestamp, status.LastTimestamp, status.RecordCount,
                            status.Health.ToString(),
                            JsonSerializer.Serialize(status.Gaps, ManifestJson.Options),
                            JsonSerializer.Serialize(status.CompleteMonths, ManifestJson.Options)), ct);

                    if (string.IsNullOrEmpty(interval)) continue;
                    var known = (await index.GetMonths(exchange, dir, feedName, interval, ct))
                        .ToDictionary(m => m.Month);
                    var months = await scanner.Scan(Path.Combine(assetDir, feedName), interval, known, ct);
                    await index.ReplaceMonths(exchange, dir, feedName, interval, months, ct);
                }
                await index.PruneFeedData(exchange, dir, keepFeeds, ct);

                if (i % 50 == 0)
                    await index.UpdateJob(jobId, "running",
                        progressJson: JsonSerializer.Serialize(new { assets_done = i + 1, assets_total = dirs.Count }),
                        ct: ct);
            }

            await index.PruneAssetsNotIn(keepAssets, ct);
            await index.UpdateJob(jobId, "completed",
                progressJson: JsonSerializer.Serialize(new { assets_done = dirs.Count, assets_total = dirs.Count }),
                ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await index.UpdateJob(jobId, "interrupted", ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Index rebuild {JobId} failed", jobId);
            await index.UpdateJob(jobId, "failed", error: ex.Message, ct: CancellationToken.None);
        }
    }

    // Same scan as FeedCatalog.ScanAssetDirs (FeedCatalog.cs:124-149): ListKeys over
    // DataRoot with suffix feeds.json, recursive; trailing-separator guard included.
    // Copied here verbatim; FeedCatalog loses it in Task 7.
    private async Task<List<(string Exchange, string Dir)>> ScanAssetDirs(string dataRoot, CancellationToken ct)
    {
        // Trailing separator forces directory semantics in ListKeys: a missing DataRoot scans
        // as empty instead of falling back to a recursive parent-directory walk ("dir/name*"
        // prefix match), which hits unreadable siblings (e.g. /tmp/systemd-private-*).
        var rootPrefix = string.IsNullOrEmpty(dataRoot) || Path.EndsInDirectorySeparator(dataRoot)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;
        var seen = new HashSet<(string, string)>();
        var result = new List<(string, string)>();
        await foreach (var key in storage.ListKeys(rootPrefix, suffix: "feeds.json", recursive: true, ct))
        {
            var segments = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3) continue; // …/{exchange}/{dir}/feeds.json
            var exchange = segments[^3];
            var dir = segments[^2];
            if (seen.Add((exchange, dir))) result.Add((exchange, dir));
        }
        result.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }
}

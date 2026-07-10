using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Index;

/// <summary>
/// Cheap periodic reconciliation of index vs disk (spec §3.3): stat-only comparison, re-scan
/// enqueued just for mismatched feeds. Catches manual file edits without a full rebuild.
/// </summary>
internal sealed class DriftSweepService(
    IHistoryIndex index,
    IIndexMaintenance maintenance,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<DriftSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.Index.DriftSweepMinutes));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await Sweep(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Drift sweep failed");
            }
        }
    }

    private async Task Sweep(CancellationToken ct)
    {
        var dataRoot = options.CurrentValue.DataRoot;
        foreach (var asset in await index.ListAssets(ct: ct))
        {
            ct.ThrowIfCancellationRequested();
            var assetDir = Path.Combine(dataRoot, asset.Exchange, asset.Dir);
            // ListFeedKeys, not GetFeedStatuses: feeds indexed only as month rows (static
            // equity, no status_*.json) must still be swept for drift.
            foreach (var (feedName, interval) in await index.ListFeedKeys(asset.Exchange, asset.Dir, ct))
            {
                if (string.IsNullOrEmpty(interval)) continue;
                var known = await index.GetMonths(asset.Exchange, asset.Dir, feedName, interval, ct);
                if (HasDrift(Path.Combine(assetDir, feedName), interval, known))
                    maintenance.Enqueue(new IndexWork.FeedTouched(assetDir, feedName, interval));
            }
        }
    }

    private static bool HasDrift(string feedDir, string interval, IReadOnlyList<MonthPartitionRow> known)
    {
        var onDisk = new Dictionary<string, (long Len, string Mtime)>();
        if (Directory.Exists(feedDir))
            foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{interval}.csv"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var underscore = name.IndexOf('_');
                if (underscore != 7) continue;
                var fi = new FileInfo(file);
                onDisk[name[..underscore]] = (fi.Length, fi.LastWriteTimeUtc.ToString("O"));
            }

        if (onDisk.Count != known.Count) return true;
        foreach (var k in known)
            if (!onDisk.TryGetValue(k.Month, out var d) || d.Len != k.FileLen || d.Mtime != k.FileMtimeUtc)
                return true;
        return false;
    }
}

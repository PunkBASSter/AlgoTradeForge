using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Jobs;

/// <summary>
/// Boot-time crash reconciliation (§S8). A load/aggregation job killed mid-month leaves a
/// non-terminal row (marked 'interrupted' by <c>HistoryIndexInitializer</c>'s startup sweep), a
/// <c>touched_json</c> breadcrumb naming the in-flight (feedKey, month), possibly a stale
/// month_partitions row for a month whose CSV never landed, and an orphan <c>.tmp-*</c> from the
/// aborted atomic write. This runs as an AWAITED <see cref="IHostedService.StartAsync"/> BEFORE
/// <c>DesiredStateService</c>'s first convergence so an incomplete month is never read as complete
/// (which would suppress re-collection). It does NOT re-enqueue — the 3a kick owns re-collection.
/// </summary>
internal sealed class InterruptedJobSweeper(
    IHistoryIndex index,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<InterruptedJobSweeper> logger) : IHostedService
{
    private static readonly JsonSerializerOptions TouchedJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reconciled = await SweepOnceForTest(cancellationToken);
            if (reconciled > 0)
                logger.LogInformation("InterruptedJobSweeper reconciled {Count} in-flight month(s)", reconciled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // host shutting down during boot
        }
        catch (Exception ex)
        {
            // A failed sweep must not abort host startup — convergence self-heals via drift sweeps.
            logger.LogError(ex, "InterruptedJobSweeper failed; continuing startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Single sweep pass; returns the number of (feedKey, month) breadcrumbs reconciled.
    internal async Task<int> SweepOnceForTest(CancellationToken ct)
    {
        var dataRoot = options.CurrentValue.DataRoot;
        var reconciled = 0;

        foreach (var job in await index.ListInterruptedJobs(ct))
        {
            foreach (var (feedKey, month) in ParseTouched(job.TouchedJson))
            {
                // §S9: exchange is segment 0 so month_partitions / feed_status lookups never cross venues.
                var parts = feedKey.Split('|');
                if (parts.Length != 4)
                {
                    logger.LogWarning("Interrupted job {JobId}: unparseable feed_key '{FeedKey}'", job.Id, feedKey);
                    continue;
                }

                await ReconcileMonth(dataRoot, parts[0], parts[1], parts[2], parts[3], month, ct);
                reconciled++;
            }
        }

        return reconciled;
    }

    private async Task ReconcileMonth(
        string dataRoot, string exchange, string dir, string feedName, string interval, string month, CancellationToken ct)
    {
        var feedDir = Path.Combine(dataRoot, exchange, dir, feedName);
        var fileName = string.IsNullOrEmpty(interval) ? $"{month}.csv" : $"{month}_{interval}.csv";
        var partitionPath = Path.Combine(feedDir, fileName);

        // A touched month was mid-flight at crash time — never trust its completeness flag.
        await index.RemoveCompleteMonth(exchange, dir, feedName, interval, month, ct);

        if (File.Exists(partitionPath))
            return;

        // CSV never landed: drop the stale coverage row and sweep the aborted atomic-write tmp.
        await index.DeleteMonthPartition(exchange, dir, feedName, interval, month, ct);
        RemoveOrphanTmps(feedDir, fileName);
    }

    private void RemoveOrphanTmps(string feedDir, string fileName)
    {
        if (!Directory.Exists(feedDir)) return;
        foreach (var tmp in Directory.EnumerateFiles(feedDir, $"{fileName}.tmp-*"))
        {
            try { File.Delete(tmp); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete orphan tmp {Tmp}", tmp); }
        }
    }

    private IEnumerable<(string FeedKey, string Month)> ParseTouched(string touchedJson)
    {
        if (string.IsNullOrWhiteSpace(touchedJson) || touchedJson == "[]")
            return [];

        TouchedEntry[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<TouchedEntry[]>(touchedJson, TouchedJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unparseable touched_json '{Json}'", touchedJson);
            return [];
        }

        return (entries ?? [])
            .Where(e => !string.IsNullOrEmpty(e.FeedKey) && !string.IsNullOrEmpty(e.Month))
            .Select(e => (e.FeedKey!, e.Month!));
    }

    private sealed record TouchedEntry(string? FeedKey, string? Month);
}

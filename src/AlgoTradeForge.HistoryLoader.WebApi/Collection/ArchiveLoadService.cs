using System.Text.Json;
using System.Threading.Channels;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class ArchiveLoadService(
    IBackfillOrchestrator orchestrator,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<ArchiveLoadService> logger) : IArchiveLoadService
{
    private static bool IsTrueShutdown(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException oce && ct.IsCancellationRequested && oce.CancellationToken == ct;

    public async Task<bool> Run(ArchiveLoadRequest req, IJobProgressSink sink, CancellationToken ct = default)
    {
        // Ordered, single-consumer progress drain. Report enqueues synchronously (the sync
        // IProgress<T> contract); one consumer awaits sink.Report sequentially. Every exit
        // path completes the channel and awaits the consumer BEFORE the terminal sink call —
        // guaranteeing all progress seq < terminal seq (no SSE tail mis-order) and that the
        // terminal UpdateJob runs last (no late Report reverting complete→running, wedging
        // the feed_key). A faulted Report surfaces via the awaited consumer instead of being
        // silently swallowed by fire-and-forget.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var json in channel.Reader.ReadAllAsync(ct))
                    await sink.Report(json, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }, ct);

        async Task Flush()
        {
            channel.Writer.TryComplete();
            await consumer;
        }

        // F1 guard: interval-based feeds must carry a parseable interval string.
        // Feeds where UsesMonthlyCompleteness = true (ticks, funding-rate) carry no interval
        // and must bypass IntervalParser entirely — the phase-3a crash vector.
        if (!FeedNames.UsesMonthlyCompleteness(req.FeedName))
        {
            var valid = !string.IsNullOrEmpty(req.Interval);
            if (valid)
            {
                try { IntervalParser.ToTimeSpan(req.Interval); }
                catch (ArgumentException) { valid = false; }
            }
            if (!valid)
            {
                await Flush();
                await sink.Fail("invalid_interval",
                    $"Feed '{req.FeedName}' requires a valid interval string; got '{req.Interval}'.", ct);
                return false;
            }
        }

        await sink.Started(
            JsonSerializer.Serialize(new { feedName = req.FeedName, from = req.From.ToString("yyyy-MM-dd"), to = req.To.ToString("yyyy-MM-dd") }),
            ct);

        try
        {
            var asset = req.Asset;
            var hasEntry = asset.Feeds.Any(f => f.FeedName == req.FeedName && f.Interval == req.Interval);
            if (!hasEntry)
                asset = asset with
                {
                    Feeds = [..asset.Feeds, new CollectionFeed(req.FeedName, req.Interval, "on-demand", "csv", req.From)],
                };

            var assetDir = Path.Combine(options.CurrentValue.DataRoot, asset.Exchange, asset.Venue.Dir);
            var ok = await orchestrator.TryRunSingle(
                asset, assetDir, feedFilter: [req.FeedName], fromDate: req.From, toDate: req.To,
                progress: new SinkProgress(channel.Writer), ct: ct);

            await Flush();

            if (ok)
            {
                await sink.Complete("""{"ok":true}""", ct);
                return true;
            }

            await sink.Fail("symbol_busy", "Another backfill holds the symbol lock; retry later.", ct);
            return false;
        }
        catch (ArchiveIntegrityException ex)
        {
            await Flush();
            await sink.Fail("checksum_mismatch", ex.Message, ct);
            return false;
        }
        catch (Exception ex) when (!IsTrueShutdown(ex, ct))
        {
            await Flush();
            logger.LogError(ex, "Load job for feed {FeedName} failed", req.FeedName);
            await sink.Fail("load_failed", ex.Message, ct);
            return false;
        }
    }

    private sealed class SinkProgress(ChannelWriter<string> writer) : IProgress<ArchiveProgress>
    {
        // IProgress<T>.Report is synchronous; enqueue non-blocking and in order. The single
        // consumer in Run awaits sink.Report so writes complete before any terminal call.
        public void Report(ArchiveProgress value) =>
            writer.TryWrite(
                JsonSerializer.Serialize(new { done = value.MonthsDone, total = value.MonthsTotal, phase = value.CurrentMonth }));
    }
}

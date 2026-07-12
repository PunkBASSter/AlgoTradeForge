using System.Text.Json;
using System.Threading.Channels;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.WebApi.Aggregation;

internal sealed class AggregationService(
    IServiceScopeFactory scopeFactory,
    ILogger<AggregationService> logger) : IAggregationService
{
    // Mirrors ArchiveLoadService: true-shutdown = OCE caused by the token passed to Run,
    // not by an internal source (e.g. a downstream call with its own ct).
    private static bool IsTrueShutdown(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException oce && ct.IsCancellationRequested && oce.CancellationToken == ct;

    public async Task Run(AggregationRunRequest req, IJobProgressSink sink, CancellationToken ct = default)
    {
        var job = req.Job;

        // Ordered single-consumer progress drain. The pipeline's onProgress callback is
        // synchronous; sink.Report is async. TryWrite enqueues in order; one consumer awaits
        // sink.Report sequentially. Flush() MUST complete before every terminal sink call so
        // all progress seq < terminal seq (no SSE tail mis-order, no state regression
        // complete→running in the durable store).
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var json in channel.Reader.ReadAllAsync(ct))
                {
                    try { await sink.Report(json, ct); }
                    catch (Exception ex) when (!IsTrueShutdown(ex, ct))
                    {
                        logger.LogWarning(ex, "Progress report dropped for job {JobId}", job.JobId);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }, ct);

        async Task Flush()
        {
            channel.Writer.TryComplete();
            await consumer;
        }

        try
        {
            await sink.Started(
                JsonSerializer.Serialize(new
                {
                    jobId = job.JobId,
                    feedId = job.OutcomeFeedId,
                    sourceFeedId = job.Source.FeedId,
                }),
                ct);

            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IAggregationPipeline>();

            var result = await pipeline.Run(
                job,
                onProgress: ev =>
                {
                    if (ev is ProgressEvent.Progress p)
                    {
                        channel.Writer.TryWrite(JsonSerializer.Serialize(new
                        {
                            partition = p.CurrentPartition,
                            barsEmitted = p.BarsEmitted,
                            elapsedMs = p.ElapsedMs,
                        }));
                    }
                },
                ct);

            await Flush();
            await sink.Complete(JsonSerializer.Serialize(new
            {
                jobId = result.JobId,
                outcomeFeedId = result.OutcomeFeedId,
                barCount = result.BarCount,
                firstBarTs = result.FirstBarTs,
                lastBarTs = result.LastBarTs,
                durationSeconds = result.DurationSeconds,
            }), ct);
        }
        // D2 (cancellation ownership): the HOST classifies. Any cancellation OCE carrying `ct`
        // (user-cancel OR host-shutdown — both trip the linked token the host passes) is left to
        // propagate so AggregationWorkerHost's catch arms, keyed on its stoppingToken, decide
        // between terminal `cancelled` and a non-terminal row for restart. The service only owns
        // its own domain failures. Mirrors ArchiveLoadService.
        catch (Exception ex) when (!IsTrueShutdown(ex, ct))
        {
            await Flush();
            logger.LogError(ex, "Aggregation job {JobId} (feedId={FeedId}) failed.", job.JobId, job.OutcomeFeedId);
            var redacted = $"Aggregation job failed ({ex.GetType().Name}); see server logs (job_id={job.JobId}).";
            await sink.Fail("internal_error", redacted, CancellationToken.None);
        }
    }
}

using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.WebApi.Jobs;

/// <summary>
/// Drains the keyed "materialize" wakeup queue and runs each job's ordered stages sequentially,
/// advancing <c>progress_json.done</c> (the stage index, in the canonical progress shape) after each
/// stage so a crashed composite resumes mid-stage. Mirrors <c>LoadJobWorker</c>'s drain / per-item isolation / cancellation-classification
/// structure. §B5: stages run DIRECTLY (no per-stage feed-gate) — the job already holds the output
/// gate, and the aggregate stage's key equals the job's own key, so re-claiming would self-Busy.
/// </summary>
internal sealed class MaterializeWorkerHost(
    [FromKeyedServices("materialize")] IJobWakeupQueue wakeup,
    IHistoryIndex index,
    IArchiveLoadService archiveLoad,
    IAggregationService aggregation,
    IJobProgressSinkFactory sinkFactory,
    IJobCancellationMap cancellations,
    ICollectionPlanSource planSource,
    IMaterializeStageRequestFactory requestFactory,
    ILogger<MaterializeWorkerHost> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions _snakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedOnBootForTest(stoppingToken);
        await Drain(maxItems: null, stoppingToken);
    }

    // Processes exactly one wakeup item then returns — unit-test seam.
    internal Task DrainOnceForTest(CancellationToken ct) => Drain(maxItems: 1, ct);

    // Processes up to `count` wakeup items then returns — multi-item unit-test seam.
    internal Task DrainForTest(int count, CancellationToken ct) => Drain(count, ct);

    // §S7: seed BOTH 'queued' AND 'interrupted' materialize rows on boot. Nothing else re-triggers a
    // crashed composite (the 3a kick knows feeds, not materialize rows), so an interrupted row is
    // reset to 'queued' FIRST, then all rows are enqueued to resume at their persisted stage_index.
    // Returns the count seeded.
    internal async Task<int> SeedOnBootForTest(CancellationToken ct)
    {
        var queued = await index.ListJobs("materialize", "queued", ct);
        var interrupted = await index.ListJobs("materialize", "interrupted", ct);

        foreach (var row in interrupted)
            await index.UpdateJob(row.Id, "queued", ct: ct);

        var ids = queued.Select(j => j.Id)
            .Concat(interrupted.Select(j => j.Id))
            .Distinct()
            .ToList();
        foreach (var id in ids)
            wakeup.TryEnqueue(id);
        return ids.Count;
    }

    // Shared drain loop. Per-item faults are isolated inside RunJob so one bad row never kills the
    // loop; the outer catch swallows host-shutdown so the BackgroundService exits cleanly.
    private async Task Drain(int? maxItems, CancellationToken stoppingToken)
    {
        var processed = 0;
        try
        {
            await foreach (var jobId in wakeup.Reader(stoppingToken))
            {
                await RunJob(jobId, stoppingToken);
                if (maxItems is { } max && ++processed >= max)
                    return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task RunJob(string jobId, CancellationToken stoppingToken)
    {
        IndexJobRow? row;
        try
        {
            row = await index.GetJob(jobId, stoppingToken);
        }
        catch (Exception ex) when (!IsTrueShutdown(ex, stoppingToken))
        {
            logger.LogError(ex, "Failed to read materialize job {JobId}; skipping wakeup", jobId);
            return;
        }

        if (row is null)
        {
            logger.LogWarning("Materialize job {JobId} woke the worker but is no longer in the store", jobId);
            return;
        }

        // Cancel-while-queued: a DELETE that arrived while the job sat queued set cancel_requested
        // but could not Trip a per-job token (not yet Registered). Short-circuit to 'cancelled'.
        if (row.CancelRequested)
        {
            await sinkFactory.For(jobId).Cancel("user_cancelled", CancellationToken.None);
            return;
        }

        var baseSink = sinkFactory.For(jobId);
        try
        {
            var linked = cancellations.Register(jobId, stoppingToken);
            await RunStages(jobId, row, baseSink, linked);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // User DELETE tripped the linked (per-job) token; the host is still running.
            await baseSink.Cancel("user_cancelled", CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown mid-run — leave the row non-terminal for restart rehydration (§S7 reseed).
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Materialize job {JobId} failed", jobId);
            await baseSink.Fail("materialize_failed", ex.Message, CancellationToken.None);
        }
        finally
        {
            cancellations.Remove(jobId);
        }
    }

    private async Task RunStages(string jobId, IndexJobRow row, IJobProgressSink baseSink, CancellationToken linked)
    {
        var plan = ResolvePlan(row);
        var stagesTotal = plan.Stages.Count;
        var stageIndex = ReadStageIndex(row, stagesTotal);

        await index.UpdateJob(jobId, "running", ct: linked);
        await baseSink.Started(JsonSerializer.Serialize(new { stages_total = stagesTotal }, _snakeCase), linked);

        string? lastResult = null;
        for (var i = stageIndex; i < stagesTotal; i++)
        {
            var stage = plan.Stages[i];
            var stageSink = new MaterializeProgressSink(baseSink, i, stagesTotal, PhaseOf(stage));

            // §B5: run the stage service DIRECTLY — no TryAcquireFeedGate. The job holds the output gate.
            switch (stage)
            {
                case MaterializeStage.Load load:
                    await archiveLoad.Run(requestFactory.BuildLoad(plan, load, jobId), stageSink, linked);
                    break;
                case MaterializeStage.Aggregate agg:
                    await aggregation.Run(requestFactory.BuildAggregate(plan, agg, jobId), stageSink, linked);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown materialize stage '{stage.GetType().Name}'.");
            }

            // A stage that Fail'd / Cancel'd already drove the composite terminal via its sink.
            if (stageSink.StageCancelled || stageSink.StageFailed)
                return;

            lastResult = stageSink.LastResultJson;

            // Advance so a crash after stage i resumes at i+1. Snake_case MUST round-trip with the
            // M4.1 progress schema or resume resets to 0 and re-runs completed stages.
            await index.UpdateJob(jobId, "running",
                progressJson: Progress(i + 1, stagesTotal, NextPhase(plan.Stages, i + 1)), ct: linked);
        }

        // Worker owns the composite terminal — Complete only after the LAST stage.
        await baseSink.Complete(lastResult ?? "{}", linked);
    }

    private MaterializePlan ResolvePlan(IndexJobRow row)
    {
        if (string.IsNullOrEmpty(row.RequestJson))
            throw new InvalidOperationException($"Materialize job '{row.Id}' has no request_json to rehydrate.");

        var p = JsonSerializer.Deserialize<MaterializeReqPayload>(row.RequestJson, _snakeCase)
            ?? throw new InvalidOperationException($"Materialize job '{row.Id}' request_json deserialized to null.");

        DateRange? range = (p.From, p.To) switch
        {
            ({ } from, { } to) => new DateRange(from, to),
            _ => null,
        };
        return MaterializePlan.Resolve(planSource.Current, p.Exchange, p.Symbol, p.Feed, range);
    }

    // ★ CRITICAL: progress_json is the canonical snake_case shape {phase, done, total, detail:{…}}.
    // The stage index to resume from is the top-level `done` counter; SnakeCaseLower must bind it —
    // a shape mismatch would silently read 0 and re-run a resumed job from scratch.
    private int ReadStageIndex(IndexJobRow row, int stagesTotal)
    {
        var stageIndex = 0;
        if (!string.IsNullOrEmpty(row.ProgressJson))
        {
            try
            {
                var p = JsonSerializer.Deserialize<StageProgress>(row.ProgressJson, _snakeCase);
                if (p is not null) stageIndex = p.Done;
            }
            catch (JsonException) { /* malformed progress: restart from stage 0 */ }
        }
        return Math.Clamp(stageIndex, 0, stagesTotal);
    }

    // Canonical progress shape read by JobEnvelope + FE JobCard: done=stage index drives both the
    // coarse stage bar and the resume position (ReadStageIndex); detail carries the "Stage i of n" fields.
    private static string Progress(int stageIndex, int stagesTotal, string phase) =>
        JsonSerializer.Serialize(new
        {
            Phase = phase,
            Done = stageIndex,
            Total = stagesTotal,
            Detail = new { StageIndex = stageIndex, StagesTotal = stagesTotal },
        }, _snakeCase);

    private static string PhaseOf(MaterializeStage stage) => stage switch
    {
        MaterializeStage.Load => "load",
        MaterializeStage.Aggregate => "aggregate",
        _ => "unknown",
    };

    private static string NextPhase(IReadOnlyList<MaterializeStage> stages, int index) =>
        index < stages.Count ? PhaseOf(stages[index]) : "done";

    private sealed record StageProgress(int Done, int Total);

    private sealed record MaterializeReqPayload(string Exchange, string Symbol, string Feed, DateOnly? From, DateOnly? To);
}

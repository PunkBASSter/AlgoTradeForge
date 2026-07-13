using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

/// <summary>
/// Wraps the composite job's base sink for ONE stage of a materialize run. Stage progress is
/// rewritten into the canonical envelope <c>{phase, done, total, detail:{stage_index, stages_total,
/// stage}}</c> (done=stage_index, total=stages_total) so the SSE stream reads as
/// "stage i/n: &lt;inner progress&gt;". The stage services own their per-stage
/// terminal calls, but the COMPOSITE terminal is owned by the worker — so this sink deliberately
/// does NOT forward a stage <see cref="Started"/>/<see cref="Complete"/> as a composite terminal
/// (that would evict the job from the SSE cache mid-run). A stage <see cref="Fail"/>/<see cref="Cancel"/>
/// IS forwarded — a failed/cancelled stage terminates the whole composite — and latched so the
/// worker can stop the stage loop.
/// </summary>
public sealed class MaterializeProgressSink(
    IJobProgressSink baseSink, int stageIndex, int stagesTotal, string phase) : IJobProgressSink
{
    private static readonly JsonSerializerOptions _snakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public bool StageFailed { get; private set; }
    public bool StageCancelled { get; private set; }
    public string? LastResultJson { get; private set; }

    public Task Report(string progressJson, CancellationToken ct = default)
    {
        JsonNode? inner = null;
        try { inner = JsonNode.Parse(progressJson); }
        catch (JsonException) { /* non-JSON stage payload: embed verbatim as a string below */ }

        // Canonical progress shape read by JobEnvelope + FE JobCard: top-level phase/done/total
        // drive the coarse stage bar (done=stage_index); detail carries the "Stage i of n (stage)"
        // fields plus the inner stage payload.
        var envelope = new JsonObject
        {
            ["phase"] = phase,
            ["done"] = stageIndex,
            ["total"] = stagesTotal,
            ["detail"] = new JsonObject
            {
                ["stage_index"] = stageIndex,
                ["stages_total"] = stagesTotal,
                ["stage"] = inner ?? JsonValue.Create(progressJson),
            },
        };
        return baseSink.Report(envelope.ToJsonString(_snakeCase), ct);
    }

    // Composite Started is worker-owned; a per-stage Started must not re-flip/re-emit the composite.
    public Task Started(string startedPayloadJson, CancellationToken ct = default) => Task.CompletedTask;

    // Stage success: capture the result for the worker but DO NOT terminate the composite. The
    // worker calls baseSink.Complete only after the LAST stage.
    public Task Complete(string resultPayloadJson, CancellationToken ct = default)
    {
        LastResultJson = resultPayloadJson;
        return Task.CompletedTask;
    }

    // A failed stage fails the whole composite: forward as the composite terminal and latch so the
    // worker stops advancing stages.
    public Task Fail(string code, string message, CancellationToken ct = default)
    {
        StageFailed = true;
        return baseSink.Fail(code, message, ct);
    }

    public Task Cancel(string reason, CancellationToken ct = default)
    {
        StageCancelled = true;
        return baseSink.Cancel(reason, ct);
    }
}

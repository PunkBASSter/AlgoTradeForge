using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class MaterializeEndpoints
{
    private static readonly JsonSerializerOptions _snakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static WebApplication MapMaterializeEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/v1").MapPost("/materialize", PostMaterialize);
        return app;
    }

    public sealed record MaterializeRequest(
        string Exchange,
        string Symbol,
        string Feed,
        DateOnly? From = null,
        DateOnly? To = null);

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task<IResult> PostMaterialize(
        MaterializeRequest body,
        ICollectionPlanSource planSource,
        IHistoryIndex index,
        [FromKeyedServices("materialize")] IJobWakeupQueue wakeup,
        CancellationToken ct)
    {
        body = body with
        {
            Exchange = body.Exchange.ToLowerInvariant(),
            Symbol = body.Symbol.ToUpperInvariant()
        };

        DateRange? range = (body.From, body.To) switch
        {
            ({ } from, { } to) => new DateRange(from, to),
            _ => null
        };

        MaterializePlan materializePlan;
        try
        {
            materializePlan = MaterializePlan.Resolve(planSource.Current, body.Exchange, body.Symbol, body.Feed, range);
        }
        catch (FeedNotMaterializableException ex)
        {
            return Results.Json(
                new { code = "feed_not_materializable", message = ex.Message },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var stagesTotal = materializePlan.Stages.Count;
        // Canonical progress shape read by JobEnvelope + FE JobCard: done=stage index (0 at seed).
        var initialProgress = JsonSerializer.Serialize(
            new
            {
                Phase = "load",
                Done = 0,
                Total = stagesTotal,
                Detail = new { StageIndex = 0, StagesTotal = stagesTotal },
            },
            _snakeCase);
        var reqJson = JsonSerializer.Serialize(body, _snakeCase);

        var outcome = await index.TryAcquireFeedGate(
            "materialize", materializePlan.OutputFeedKey, initialProgress, reqJson, ct);

        switch (outcome)
        {
            case FeedGateOutcome.Acquired acquired:
                var location = $"/api/v1/jobs/{acquired.JobId}/progress";
                if (wakeup.TryEnqueue(acquired.JobId))
                    return Results.Json(
                        new { job_id = acquired.JobId, location },
                        statusCode: StatusCodes.Status202Accepted);
                // Dispatch channel full: drop the just-claimed row so a 503 leaves no phantom job.
                await index.DeleteJob(acquired.JobId, ct);
                return Results.Json(
                    new { code = "queue_full", retry_after_seconds = 5 },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            case FeedGateOutcome.Busy busy:
                return Results.Json(
                    new { code = "feed_busy", active_job_id = busy.ExistingJobId },
                    statusCode: StatusCodes.Status409Conflict);

            default:
                return Results.Problem("Unknown feed-gate outcome");
        }
    }
}

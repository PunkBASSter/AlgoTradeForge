using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class LoadEndpoints
{
    public static WebApplication MapLoadEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");
        v1.MapPost("/loads", PostLoad);
        v1.MapGet("/loads/{jobId}", GetLoad);
        return app;
    }

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task<IResult> PostLoad(
        LoadRequest body,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ArchiveMaterializerRegistry registry,
        IHistoryIndex index,
        [FromKeyedServices("load")] IJobWakeupQueue wakeup,
        ICollectionPlanSource planSource,
        CancellationToken ct)
    {
        // Normalize casing before building paths or keys.
        body = body with { Symbol = body.Symbol.ToUpperInvariant(), Exchange = body.Exchange.ToLowerInvariant() };

        if (!FeedIdValidator.TryValidatePathComponent(body.Exchange, out var exchangeErr))
            return Unprocessable("invalid_path_component", exchangeErr!);
        if (!FeedIdValidator.TryValidatePathComponent(body.Symbol, out var symbolErr))
            return Unprocessable("invalid_path_component", symbolErr!);

        // P5: symbol must be declared in an enabled collection group (groups are the only entry point).
        var asset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, body.Exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.ApiSymbol, body.Symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.AssetType, body.AssetType, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
            return Unprocessable("symbol_not_declared",
                "symbol is not declared in any enabled collection group");

        var opts = options.CurrentValue;
        var error = LoadRequestValidator.Validate(body, registry, opts.Load);
        if (error is not null)
            return Unprocessable(error.Code, error.Message);

        // Feed-level gate (was symbol-level in phase-2). See §S5 in the commit body.
        var feedKey = $"{body.Exchange}|{asset.Venue.Dir}|{body.FeedName}|{body.Interval}";
        var reqJson = LoadRequestRehydrator.Serialize(
            body.Exchange, body.Symbol, body.AssetType, body.FeedName, body.Interval, body.From, body.To);

        var outcome = await index.TryAcquireFeedGate("load", feedKey, "{}", reqJson, ct);
        switch (outcome)
        {
            case FeedGateOutcome.Acquired acquired:
                if (wakeup.TryEnqueue(acquired.JobId))
                    return Results.Accepted(value: new { job_id = acquired.JobId });
                // Dispatch channel full: drop the just-claimed row so a 503 leaves no phantom job.
                await index.DeleteJob(acquired.JobId, ct);
                return Results.Json(new { error = "queue_full" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            case FeedGateOutcome.Busy busy:
                return Results.Json(new { error = "feed_busy", active_job_id = busy.ExistingJobId },
                    statusCode: StatusCodes.Status409Conflict);

            default:
                return Results.Problem("Unknown feed-gate outcome");
        }
    }

    // `/loads/{jobId}` is now a thin alias over the unified job store (design §3.5): it returns
    // the same envelope as GET /jobs/{jobId}. The legacy LoadJobSnapshot shape is retired.
    private static Task<IResult> GetLoad(string jobId, IHistoryIndex index, CancellationToken ct) =>
        JobEndpoints.GetJob(jobId, index, ct);

    private static IResult Unprocessable(string code, string message) =>
        Results.Json(new { error = code, message }, statusCode: StatusCodes.Status422UnprocessableEntity);
}

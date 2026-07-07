using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
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
    internal static IResult PostLoad(
        LoadRequest body,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ArchiveMaterializerRegistry registry,
        ILoadJobRegistry loadRegistry)
    {
        // Normalize casing before building paths or keys.
        body = body with { Symbol = body.Symbol.ToUpperInvariant(), Exchange = body.Exchange.ToLowerInvariant() };

        if (!FeedIdValidator.TryValidatePathComponent(body.Exchange, out var exchangeErr))
            return Unprocessable("invalid_path_component", exchangeErr!);
        if (!FeedIdValidator.TryValidatePathComponent(body.Symbol, out var symbolErr))
            return Unprocessable("invalid_path_component", symbolErr!);

        var opts = options.CurrentValue;

        var error = LoadRequestValidator.Validate(body, registry, opts.Load);
        if (error is not null)
            return Unprocessable(error.Code, error.Message);

        var assetDir = BuildAssetDir(opts.DataRoot, body.Exchange, body.Symbol, body.AssetType);

        var activeForSymbol = loadRegistry.ActiveJobForSymbol(assetDir);
        if (activeForSymbol is not null)
            return Results.Json(
                new { error = "symbol_busy", active_job_id = activeForSymbol },
                statusCode: StatusCodes.Status409Conflict);

        var jobId = Guid.NewGuid().ToString("N");
        var feedKey = $"{assetDir}|{body.FeedName}|{body.Interval}";
        var job = new LoadJob(jobId, body.Exchange, body.Symbol, body.AssetType,
            body.FeedName, body.Interval, body.From, body.To);

        return loadRegistry.TryEnqueue(job, feedKey) switch
        {
            LoadEnqueueOutcome.Accepted => Results.Accepted(value: new { job_id = jobId }),
            LoadEnqueueOutcome.FeedBusy busy => Results.Json(
                new { error = "feed_busy", active_job_id = busy.ActiveJobId },
                statusCode: StatusCodes.Status409Conflict),
            LoadEnqueueOutcome.QueueFull => Results.Json(
                new { error = "queue_full" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Problem("Unknown enqueue outcome"),
        };
    }

    private static IResult GetLoad(string jobId, ILoadJobRegistry loadRegistry)
    {
        var snapshot = loadRegistry.Get(jobId);
        return snapshot is null
            ? Results.NotFound(new { error = "job_not_found_or_expired", job_id = jobId })
            : Results.Ok(snapshot);
    }

    private static IResult Unprocessable(string code, string message) =>
        Results.Json(new { error = code, message }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static string BuildAssetDir(string dataRoot, string exchange, string symbol, string assetType) =>
        Path.Combine(dataRoot, exchange, AssetPathConvention.DirectoryName(symbol, assetType));
}

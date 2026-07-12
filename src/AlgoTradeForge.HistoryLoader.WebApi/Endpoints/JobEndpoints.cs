using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class JobEndpoints
{
    public static WebApplication MapJobEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");
        v1.MapGet("/jobs", ListJobs);
        v1.MapGet("/jobs/{jobId}", GetJob);
        return app;
    }

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task<IResult> ListJobs(
        string? kind, string? state, IHistoryIndex index, CancellationToken ct)
    {
        var rows = await index.ListJobs(kind, state, ct);
        return TypedResults.Ok(rows.Select(JobEnvelope.From).ToList());
    }

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task<IResult> GetJob(
        string jobId, IHistoryIndex index, CancellationToken ct)
    {
        var row = await index.GetJob(jobId, ct);
        if (row is null)
            return TypedResults.NotFound(new { code = "job_not_found", message = $"job '{jobId}' not found" });
        return TypedResults.Ok(JobEnvelope.From(row));
    }
}

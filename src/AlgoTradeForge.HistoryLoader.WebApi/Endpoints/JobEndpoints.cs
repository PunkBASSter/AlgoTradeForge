using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Microsoft.Net.Http.Headers;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class JobEndpoints
{
    public static WebApplication MapJobEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");
        v1.MapGet("/jobs", ListJobs);
        v1.MapGet("/jobs/{jobId}", GetJob);
        v1.MapGet("/jobs/{jobId}/progress", GetJobProgressSse);
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

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task GetJobProgressSse(
        string jobId, HttpContext context, IHistoryIndex index, IJobEventSignal signal)
    {
        var ct = context.RequestAborted;

        if (await index.GetJob(jobId, ct) is null && await index.GetLastEventSeq(jobId, ct) == 0)
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            try
            {
                await context.Response.WriteAsJsonAsync(
                    new { error = "job_not_found_or_expired", job_id = jobId }, ct);
            }
            catch (OperationCanceledException) { }
            return;
        }

        context.Response.Headers[HeaderNames.ContentType] = "text/event-stream";
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        // SSE writes throw OperationCanceledException on client disconnect — that's normal
        // (the FE may reconnect with Last-Event-ID or navigate away). Swallow and exit.
        try
        {
            await context.Response.Body.FlushAsync(ct);

            var lastEventId = ParseLastEventId(context);
            await JobSseWriter.Tail(jobId, lastEventId, index, signal, WriteFrame, ct);

            async Task WriteFrame(int seq, string kind, string payloadJson)
            {
                await context.Response.WriteAsync($"id: {seq}\nevent: {kind}\ndata: {payloadJson}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static int ParseLastEventId(HttpContext context)
    {
        var header = context.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrEmpty(header)) return 0;
        return int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}

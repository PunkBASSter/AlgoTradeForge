using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.WebApi.Groups;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class DesiredStateEndpoints
{
    public static WebApplication MapDesiredStateEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/v1").MapGet("/desired-state", GetDesiredState);
        return app;
    }

    internal static IResult GetDesiredState(DesiredStateService service, string? exchange = null)
    {
        var report = service.LatestReport;
        if (report is null)
            return Results.Json(new { error = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var tuples = exchange is null
            ? report.Tuples
            : (IEnumerable<TupleStatus>)report.Tuples
                .Where(t => string.Equals(t.Tuple.Exchange, exchange, StringComparison.OrdinalIgnoreCase));

        var orphaned = exchange is null
            ? report.Orphaned
            : (IReadOnlyList<OrphanEntry>)report.Orphaned
                .Where(o => string.Equals(o.Exchange, exchange, StringComparison.OrdinalIgnoreCase))
                .ToList();

        // Endpoint caps the serialized orphan list at 500; full count is always reported.
        var orphanedTotal = orphaned.Count;
        var orphanedCapped = orphaned.Take(500);

        return Results.Json(new
        {
            computed_at     = report.ComputedAt,
            tuples          = tuples.Select(t => new
            {
                exchange      = t.Tuple.Exchange,
                canonical     = t.Tuple.Canonical,
                dir           = t.Tuple.Venue?.Dir,
                feed_name     = t.Tuple.FeedName,
                interval      = t.Tuple.Interval,
                status        = t.Status,
                months_expected = t.MonthsExpected,
                months_covered  = t.MonthsCovered,
                collect       = t.Tuple.Collect,
                history_start = t.Tuple.HistoryStart,
                is_derived    = t.Tuple.IsDerived,
                groups        = t.Tuple.Groups,
            }),
            orphaned        = orphanedCapped.Select(o => new
            {
                exchange  = o.Exchange,
                dir       = o.Dir,
                feed_name = o.FeedName,
                interval  = o.Interval,
            }),
            orphaned_total  = orphanedTotal,
            conflicts       = report.Conflicts.Select(c => new
            {
                key     = c.Key,
                kind    = c.Kind,
                groups  = c.Groups,
                message = c.Message,
            }),
        });
    }
}

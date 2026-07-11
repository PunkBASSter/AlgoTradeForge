using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class GroupEndpoints
{

    public static WebApplication MapGroupEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/groups",           GetGroups);
        v1.MapGet("/groups/{name}",    GetGroup);
        v1.MapPut("/groups/{name}",    PutGroup);
        v1.MapDelete("/groups/{name}", DeleteGroup);
        v1.MapPost("/groups/validate", ValidateGroup);

        return app;
    }

    // ---- handlers (internal for direct test invocation via InternalsVisibleTo) ----

    internal static async Task<IResult> GetGroups(IGroupStore store, CancellationToken ct)
    {
        var docs = await store.List(ct);
        return Results.Json(new
        {
            groups = docs.Select(d => new
            {
                name         = d.Group.Name,
                enabled      = d.Group.Enabled,
                exchanges    = d.Group.Exchanges,
                symbol_count = d.Group.Assets.Symbols.Count,
                feed_count   = d.Group.Feeds.Count + (d.Group.Derived?.Count ?? 0),
                etag         = d.ETag,
            }).ToList(),
        });
    }

    internal static async Task<IResult> GetGroup(
        string name, IGroupStore store, HttpContext httpContext, CancellationToken ct)
    {
        GroupDocument? doc;
        try
        {
            doc = await store.Get(name, ct);
        }
        catch (ArgumentException)
        {
            return NotFound404(name);
        }
        catch (GroupValidationException ex)
        {
            return Unprocessable(ex.Errors);
        }

        if (doc is null)
            return NotFound404(name);

        httpContext.Response.Headers["ETag"] = doc.ETag;
        // Group document serialized with GroupJson (camelCase), not the global wire snake_case.
        return Results.Json(doc.Group, GroupJson.Options);
    }

    internal static async Task<IResult> PutGroup(
        string name, HttpRequest request, IGroupStore store, CancellationToken ct)
    {
        if (!GroupName.IsValid(name))
            return Unprocessable([$"name '{name}' does not match ^[a-z0-9][a-z0-9_-]{{0,63}}$"]);

        CollectionGroup? group;
        try
        {
            group = await JsonSerializer.DeserializeAsync<CollectionGroup>(
                request.Body, GroupJson.Options, ct);
        }
        catch (JsonException ex)
        {
            return Unprocessable([ex.Message]);
        }

        if (group is null)
            return Unprocessable(["body is null or empty"]);

        var ifMatch = request.Headers["If-Match"].FirstOrDefault();

        try
        {
            var newEtag = await store.Put(name, group, ifMatch, ct);
            return Results.Json(new { etag = newEtag });
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Json(
                new { error = "concurrency_conflict" },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (GroupValidationException ex)
        {
            return Unprocessable(ex.Errors);
        }
        catch (ArgumentException ex)
        {
            return Unprocessable([ex.Message]);
        }
    }

    internal static async Task<IResult> DeleteGroup(
        string name, IGroupStore store, CancellationToken ct)
    {
        bool deleted;
        try
        {
            deleted = await store.Delete(name, ct);
        }
        catch (ArgumentException)
        {
            return NotFound404(name);
        }

        return deleted
            ? Results.NoContent()
            : NotFound404(name);
    }

    internal static async Task<IResult> ValidateGroup(
        HttpRequest request,
        IGroupStore store,
        SymbologyRegistry registry,
        IHistoryIndex index,
        CancellationToken ct)
    {
        CollectionGroup? group;
        try
        {
            group = await JsonSerializer.DeserializeAsync<CollectionGroup>(
                request.Body, GroupJson.Options, ct);
        }
        catch (JsonException ex)
        {
            return Results.Json(
                new { errors = (IEnumerable<string>)[ex.Message], expansion = (object?)null },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (group is null)
            return Results.Json(
                new { errors = (IEnumerable<string>)["body is null or empty"], expansion = (object?)null },
                statusCode: StatusCodes.Status422UnprocessableEntity);

        var errors = GroupValidator.Validate(group);

        // Force Enabled = true: a disabled draft would expand to 0 tuples and preview nothing.
        group = group with { Enabled = true };

        // Stored enabled groups, EXCLUDING the same name to avoid self-conflict when editing.
        var stored = await store.List(ct);
        var peers = stored
            .Where(d => d.Group.Enabled
                && !string.Equals(d.Group.Name, group.Name, StringComparison.Ordinal))
            .Select(d => d.Group)
            .ToList();

        var allGroups = new List<CollectionGroup>(peers.Count + 1) { group };
        allGroups.AddRange(peers);

        var state = GroupExpansion.Expand(allGroups, registry);

        // Count tuples with any existing index rows; cache (exchange, dir) lookups.
        // Non-candles collected tuples (GroupExpansion emits Interval="") match any cadence interval
        // for their feedName — the real rows may be at "1h", "5m", etc.
        var feedCache = new Dictionary<(string Exchange, string Dir), IReadOnlyList<(string FeedName, string Interval)>>();
        var alreadyMaterialized = 0;
        foreach (var tuple in state.Tuples)
        {
            if (tuple.Venue is null) continue;
            var cacheKey = (tuple.Exchange, tuple.Venue.Dir);
            if (!feedCache.TryGetValue(cacheKey, out var feedKeys))
            {
                feedKeys = await index.ListFeedKeys(tuple.Exchange, tuple.Venue.Dir, ct);
                feedCache[cacheKey] = feedKeys;
            }
            bool hasRows = tuple.FeedName == FeedNames.Candles || tuple.IsDerived
                ? feedKeys.Any(fk => fk.FeedName == tuple.FeedName && fk.Interval == tuple.Interval)
                : feedKeys.Any(fk => fk.FeedName == tuple.FeedName);
            if (hasRows)
                alreadyMaterialized++;
        }

        var perExchange = state.Tuples
            .GroupBy(t => t.Exchange)
            .Select(g => new
            {
                exchange = g.Key,
                symbols  = g.Select(t => t.Canonical).Distinct(StringComparer.Ordinal).Count(),
                feeds    = g.Select(t => (t.FeedName, t.Interval)).Distinct().Count(),
            })
            .OrderBy(x => x.exchange, StringComparer.Ordinal)
            .ToList();

        return Results.Json(new
        {
            errors,
            expansion = new
            {
                tuple_count          = state.Tuples.Count,
                unsupported          = state.Unsupported.Select(u => new
                                      {
                                          exchange  = u.Exchange,
                                          canonical = u.Canonical,
                                          reason    = u.Reason,
                                      }),
                conflicts            = state.Conflicts.Select(c => new
                                      {
                                          key     = c.Key,
                                          kind    = c.Kind,
                                          groups  = c.Groups,
                                          message = c.Message,
                                      }),
                per_exchange         = perExchange,
                already_materialized = alreadyMaterialized,
            },
        });
    }

    // ---- private helpers ----

    private static IResult NotFound404(string name) =>
        Results.Json(
            new { error = "group_not_found", name },
            statusCode: StatusCodes.Status404NotFound);

    private static IResult Unprocessable(IReadOnlyList<string> errors) =>
        Results.Json(
            new { error = "validation_failed", errors },
            statusCode: StatusCodes.Status422UnprocessableEntity);
}

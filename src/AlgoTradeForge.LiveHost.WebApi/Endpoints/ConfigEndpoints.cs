using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.WebApi.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/config").WithTags("Collection Config");

        group.MapGet("/", GetConfig)
            .WithName("GetCollectionConfig")
            .WithSummary("Get the collection config (what this host captures)")
            .WithOpenApi();

        group.MapPut("/", PutConfig)
            .WithName("PutCollectionConfig")
            .WithSummary("Replace the collection config (CAS via If-Match)")
            .WithOpenApi();
    }

    private static async Task<IResult> GetConfig(
        ICollectionConfigStore store, HttpResponse response, CancellationToken ct)
    {
        var stored = await store.Load(ct);
        if (stored.ETag is not null)
            response.Headers.ETag = $"\"{stored.ETag}\"";
        return Results.Json(stored.Config);
    }

    private static async Task<IResult> PutConfig(
        CollectionConfig config, ICollectionConfigStore store, HttpRequest request,
        HttpResponse response, CancellationToken ct)
    {
        // If-Match absent or "*" => create-only (expectedETag null).
        var ifMatch = request.Headers.IfMatch.ToString();
        var expectedETag = string.IsNullOrEmpty(ifMatch) || ifMatch == "*" ? null : ifMatch.Trim('"');

        try
        {
            var newETag = await store.Save(config, expectedETag, ct);
            response.Headers.ETag = $"\"{newETag}\"";
            return Results.Json(config);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Conflict("collection.json was modified concurrently; re-GET and retry.");
        }
    }
}

using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class StrategyEndpoints
{
    public static void MapStrategyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/strategies")
            .WithTags("Strategies");

        group.MapGet("/", GetStrategies)
            .WithName("GetStrategies")
            .WithSummary("Get distinct strategy names from run history")
            .WithOpenApi()
            .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetStrategies(
        IRunRepository repository,
        CancellationToken ct)
    {
        var names = await repository.GetDistinctStrategyNamesAsync(ct);
        return Results.Ok(names);
    }
}

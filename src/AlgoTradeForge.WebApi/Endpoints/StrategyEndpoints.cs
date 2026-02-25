using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Infrastructure.Optimization;

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

        group.MapGet("/available", GetAvailableStrategies)
            .WithName("GetAvailableStrategies")
            .WithSummary("Get all strategy names discovered via reflection")
            .WithOpenApi()
            .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetStrategies(
        ICommandHandler<GetDistinctStrategyNamesQuery, IReadOnlyList<string>> handler,
        CancellationToken ct)
    {
        var names = await handler.HandleAsync(new GetDistinctStrategyNamesQuery(), ct);
        return Results.Ok(names);
    }

    private static IResult GetAvailableStrategies(SpaceDescriptorBuilder builder)
    {
        var names = builder.Build().Keys.Order().ToList();
        return Results.Ok(names);
    }
}

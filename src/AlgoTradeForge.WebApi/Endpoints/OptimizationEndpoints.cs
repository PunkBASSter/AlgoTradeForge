using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class OptimizationEndpoints
{
    public static void MapOptimizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/optimizations")
            .WithTags("Optimizations");

        group.MapPost("/", RunOptimization)
            .WithName("RunOptimization")
            .WithSummary("Run a brute-force parameter optimization")
            .WithOpenApi()
            .Produces<OptimizationResultDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> RunOptimization(
        RunOptimizationRequest request,
        ICommandHandler<RunOptimizationCommand, OptimizationResultDto> handler,
        CancellationToken ct)
    {
        var command = new RunOptimizationCommand
        {
            StrategyName = request.StrategyName,
            Axes = request.OptimizationAxes,
            DataSubscriptions = request.DataSubscriptions,
            InitialCash = request.InitialCash,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            CommissionPerTrade = request.CommissionPerTrade,
            SlippageTicks = request.SlippageTicks,
            MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
            MaxCombinations = request.MaxCombinations,
            SortBy = request.SortBy
        };

        try
        {
            var result = await handler.HandleAsync(command, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}

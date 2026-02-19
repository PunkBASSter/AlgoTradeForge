using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class BacktestEndpoints
{
    public static void MapBacktestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backtests")
            .WithTags("Backtests");

        group.MapPost("/", RunBacktest)
            .WithName("RunBacktest")
            .WithSummary("Run a backtest with the specified parameters")
            .WithOpenApi()
            .Produces<BacktestResultDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetBacktest)
            .WithName("GetBacktest")
            .WithSummary("Get a backtest result by ID")
            .WithOpenApi()
            .Produces<BacktestResultDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> RunBacktest(
        RunBacktestRequest request,
        ICommandHandler<RunBacktestCommand, BacktestResultDto> handler,
        CancellationToken ct)
    {
        TimeSpan? timeFrame = null;
        if (request.TimeFrame is not null)
        {
            if (!TimeSpan.TryParse(request.TimeFrame, CultureInfo.InvariantCulture, out var parsed))
                return Results.BadRequest(new { error = $"Invalid TimeFrame '{request.TimeFrame}'." });
            timeFrame = parsed;
        }

        var command = new RunBacktestCommand
        {
            AssetName = request.AssetName,
            Exchange = request.Exchange,
            StrategyName = request.StrategyName,
            InitialCash = request.InitialCash,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            CommissionPerTrade = request.CommissionPerTrade,
            SlippageTicks = request.SlippageTicks,
            TimeFrame = timeFrame,
            StrategyParameters = request.StrategyParameters
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

    private static Task<IResult> GetBacktest(Guid id)
    {
        return Task.FromResult(Results.NotFound(new { error = $"Backtest with ID '{id}' not found." }));
    }
}

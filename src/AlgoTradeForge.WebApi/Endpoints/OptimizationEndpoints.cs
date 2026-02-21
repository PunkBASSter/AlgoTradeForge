using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
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

        group.MapGet("/", ListOptimizations)
            .WithName("ListOptimizations")
            .WithSummary("List optimization runs with optional filters")
            .WithOpenApi()
            .Produces<IReadOnlyList<OptimizationRunResponse>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetOptimization)
            .WithName("GetOptimization")
            .WithSummary("Get an optimization run with all trials")
            .WithOpenApi()
            .Produces<OptimizationRunResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
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

    private static async Task<IResult> ListOptimizations(
        ICommandHandler<ListOptimizationRunsQuery, IReadOnlyList<OptimizationRunRecord>> handler,
        string? strategyName,
        string? assetName,
        string? exchange,
        string? timeFrame,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var query = new ListOptimizationRunsQuery(new OptimizationRunQuery
        {
            StrategyName = strategyName,
            AssetName = assetName,
            Exchange = exchange,
            TimeFrame = timeFrame,
            From = from,
            To = to,
            Limit = limit,
            Offset = offset,
        });

        var records = await handler.HandleAsync(query, ct);
        var response = records.Select(MapToResponse).ToList();
        return Results.Ok(response);
    }

    private static async Task<IResult> GetOptimization(
        Guid id,
        ICommandHandler<GetOptimizationByIdQuery, OptimizationRunRecord?> handler,
        CancellationToken ct)
    {
        var record = await handler.HandleAsync(new GetOptimizationByIdQuery(id), ct);
        if (record is null)
            return Results.NotFound(new { error = $"Optimization with ID '{id}' not found." });

        return Results.Ok(MapToResponse(record));
    }

    private static OptimizationRunResponse MapToResponse(OptimizationRunRecord r) => new()
    {
        Id = r.Id,
        StrategyName = r.StrategyName,
        StrategyVersion = r.StrategyVersion,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        DurationMs = r.DurationMs,
        TotalCombinations = r.TotalCombinations,
        SortBy = r.SortBy,
        DataStart = r.DataStart,
        DataEnd = r.DataEnd,
        InitialCash = r.InitialCash,
        Commission = r.Commission,
        SlippageTicks = r.SlippageTicks,
        MaxParallelism = r.MaxParallelism,
        DataSubscriptions = r.DataSubscriptions
            .Select(ds => new DataSubscriptionResponse(ds.AssetName, ds.Exchange, ds.TimeFrame))
            .ToList(),
        Trials = r.Trials.Select(MapTrialToResponse).ToList(),
    };

    private static BacktestRunResponse MapTrialToResponse(BacktestRunRecord r) => new()
    {
        Id = r.Id,
        StrategyName = r.StrategyName,
        StrategyVersion = r.StrategyVersion,
        Parameters = new Dictionary<string, object>(r.Parameters),
        DataSubscriptions = r.DataSubscriptions
            .Select(ds => new DataSubscriptionResponse(ds.AssetName, ds.Exchange, ds.TimeFrame))
            .ToList(),
        InitialCash = r.InitialCash,
        Commission = r.Commission,
        SlippageTicks = r.SlippageTicks,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        DataStart = r.DataStart,
        DataEnd = r.DataEnd,
        DurationMs = r.DurationMs,
        TotalBars = r.TotalBars,
        Metrics = MetricsToDict(r.Metrics),
        HasCandleData = r.RunFolderPath is not null,
        RunMode = r.RunMode,
        OptimizationRunId = r.OptimizationRunId,
    };

    private static Dictionary<string, object> MetricsToDict(Domain.Reporting.PerformanceMetrics m) => new()
    {
        ["totalTrades"] = m.TotalTrades,
        ["winningTrades"] = m.WinningTrades,
        ["losingTrades"] = m.LosingTrades,
        ["netProfit"] = m.NetProfit,
        ["grossProfit"] = m.GrossProfit,
        ["grossLoss"] = m.GrossLoss,
        ["totalCommissions"] = m.TotalCommissions,
        ["totalReturnPct"] = m.TotalReturnPct,
        ["annualizedReturnPct"] = m.AnnualizedReturnPct,
        ["sharpeRatio"] = m.SharpeRatio,
        ["sortinoRatio"] = m.SortinoRatio,
        ["maxDrawdownPct"] = m.MaxDrawdownPct,
        ["winRatePct"] = m.WinRatePct,
        ["profitFactor"] = m.ProfitFactor,
        ["averageWin"] = m.AverageWin,
        ["averageLoss"] = m.AverageLoss,
        ["initialCapital"] = m.InitialCapital,
        ["finalEquity"] = m.FinalEquity,
        ["tradingDays"] = m.TradingDays,
    };
}

using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Persistence;
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

        group.MapGet("/", ListBacktests)
            .WithName("ListBacktests")
            .WithSummary("List backtest runs with optional filters")
            .WithOpenApi()
            .Produces<IReadOnlyList<BacktestRunResponse>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetBacktest)
            .WithName("GetBacktest")
            .WithSummary("Get a backtest result by ID")
            .WithOpenApi()
            .Produces<BacktestRunResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/equity", GetEquityCurve)
            .WithName("GetBacktestEquity")
            .WithSummary("Get the equity curve for a backtest run")
            .WithOpenApi()
            .Produces<IReadOnlyList<EquityPointResponse>>(StatusCodes.Status200OK)
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

    private static async Task<IResult> ListBacktests(
        ICommandHandler<ListBacktestRunsQuery, IReadOnlyList<BacktestRunRecord>> handler,
        string? strategyName,
        string? assetName,
        string? exchange,
        string? timeFrame,
        bool? standaloneOnly,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var query = new ListBacktestRunsQuery(new BacktestRunQuery
        {
            StrategyName = strategyName,
            AssetName = assetName,
            Exchange = exchange,
            TimeFrame = timeFrame,
            StandaloneOnly = standaloneOnly,
            From = from,
            To = to,
            Limit = limit,
            Offset = offset,
        });

        var records = await handler.HandleAsync(query, ct);
        var response = records.Select(MapToResponse).ToList();
        return Results.Ok(response);
    }

    private static async Task<IResult> GetBacktest(
        Guid id,
        ICommandHandler<GetBacktestByIdQuery, BacktestRunRecord?> handler,
        CancellationToken ct)
    {
        var record = await handler.HandleAsync(new GetBacktestByIdQuery(id), ct);
        if (record is null)
            return Results.NotFound(new { error = $"Backtest with ID '{id}' not found." });

        return Results.Ok(MapToResponse(record));
    }

    private static async Task<IResult> GetEquityCurve(
        Guid id,
        ICommandHandler<GetBacktestByIdQuery, BacktestRunRecord?> handler,
        CancellationToken ct)
    {
        var record = await handler.HandleAsync(new GetBacktestByIdQuery(id), ct);
        if (record is null)
            return Results.NotFound(new { error = $"Backtest with ID '{id}' not found." });

        var points = record.EquityCurve
            .Select(ep => new EquityPointResponse(ep.TimestampMs, ep.Value))
            .ToList();

        return Results.Ok(points);
    }

    private static BacktestRunResponse MapToResponse(BacktestRunRecord r) => new()
    {
        Id = r.Id,
        StrategyName = r.StrategyName,
        StrategyVersion = r.StrategyVersion,
        Parameters = new Dictionary<string, object>(r.Parameters),
        AssetName = r.AssetName,
        Exchange = r.Exchange,
        TimeFrame = r.TimeFrame,
        InitialCash = r.InitialCash,
        Commission = r.Commission,
        SlippageTicks = r.SlippageTicks,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        DataStart = r.DataStart,
        DataEnd = r.DataEnd,
        DurationMs = r.DurationMs,
        TotalBars = r.TotalBars,
        Metrics = MetricsMapping.ToDict(r.Metrics),
        HasCandleData = r.RunFolderPath is not null,
        RunMode = r.RunMode,
        OptimizationRunId = r.OptimizationRunId,
    };
}

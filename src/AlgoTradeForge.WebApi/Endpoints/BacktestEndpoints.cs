using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
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
            .WithSummary("Submit a backtest for background execution")
            .WithOpenApi()
            .Produces<BacktestSubmissionResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListBacktests)
            .WithName("ListBacktests")
            .WithSummary("List backtest runs with optional filters")
            .WithOpenApi()
            .Produces<PagedResponse<BacktestRunResponse>>(StatusCodes.Status200OK);

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

        group.MapGet("/{id:guid}/status", GetBacktestStatus)
            .WithName("GetBacktestStatus")
            .WithSummary("Poll for backtest progress and results")
            .WithOpenApi()
            .Produces<BacktestStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/cancel", CancelBacktest)
            .WithName("CancelBacktest")
            .WithSummary("Cancel an in-progress backtest")
            .WithOpenApi()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> RunBacktest(
        RunBacktestRequest request,
        ICommandHandler<RunBacktestCommand, BacktestSubmissionDto> handler,
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
            var submission = await handler.HandleAsync(command, ct);
            var response = new BacktestSubmissionResponse
            {
                Id = submission.Id,
                TotalBars = submission.TotalBars,
            };
            return Results.Accepted($"/api/backtests/{submission.Id}/status", response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetBacktestStatus(
        Guid id,
        RunProgressCache progressCache,
        ICommandHandler<GetBacktestByIdQuery, BacktestRunRecord?> queryHandler,
        CancellationToken ct)
    {
        // Check in-progress cache first
        var entry = await progressCache.GetAsync(id, ct);
        if (entry is not null)
        {
            BacktestRunResponse? result = null;

            // If completed, fetch the full result from persistence
            if (entry.Status == RunStatus.Completed)
            {
                var record = await queryHandler.HandleAsync(new GetBacktestByIdQuery(id), ct);
                if (record is not null)
                    result = MapToResponse(record);
            }

            return Results.Ok(new BacktestStatusResponse
            {
                Id = id,
                Status = entry.Status.ToString(),
                ProcessedBars = (int)entry.Processed,
                TotalBars = (int)entry.Total,
                ErrorMessage = entry.ErrorMessage,
                ErrorStackTrace = entry.ErrorStackTrace,
                Result = result
            });
        }

        // Check persistence for completed runs
        var completedRecord = await queryHandler.HandleAsync(new GetBacktestByIdQuery(id), ct);
        if (completedRecord is not null)
        {
            return Results.Ok(new BacktestStatusResponse
            {
                Id = id,
                Status = RunStatus.Completed.ToString(),
                ProcessedBars = completedRecord.TotalBars,
                TotalBars = completedRecord.TotalBars,
                Result = MapToResponse(completedRecord)
            });
        }

        return Results.NotFound(new { error = $"Run '{id}' not found." });
    }

    private static async Task<IResult> CancelBacktest(
        Guid id,
        RunProgressCache progressCache,
        IRunCancellationRegistry cancellationRegistry,
        CancellationToken ct)
    {
        var entry = await progressCache.GetAsync(id, ct);
        if (entry is null)
            return Results.NotFound(new { error = $"Run '{id}' not found." });

        if (entry.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
            return Results.Conflict(new { error = $"Run '{id}' is already {entry.Status} and cannot be cancelled." });

        cancellationRegistry.TryCancel(id);

        await progressCache.SetAsync(entry with { Status = RunStatus.Cancelled });

        return Results.Ok(new { id, status = "Cancelled" });
    }

    private static async Task<IResult> ListBacktests(
        ICommandHandler<ListBacktestRunsQuery, PagedResult<BacktestRunRecord>> handler,
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
        var filter = new BacktestRunQuery
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
        };
        var query = new ListBacktestRunsQuery(filter);

        var paged = await handler.HandleAsync(query, ct);
        var items = paged.Items.Select(MapToResponse).ToList();
        var response = new PagedResponse<BacktestRunResponse>(
            items, paged.TotalCount, filter.Limit, filter.Offset,
            filter.Offset + items.Count < paged.TotalCount);
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

    internal static BacktestRunResponse MapToResponse(BacktestRunRecord r) => new()
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
        ErrorMessage = r.ErrorMessage,
        ErrorStackTrace = r.ErrorStackTrace,
    };
}

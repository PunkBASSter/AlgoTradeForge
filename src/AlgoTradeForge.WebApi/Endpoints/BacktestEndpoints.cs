using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class BacktestEndpoints
{
    private static bool _isDevelopment;

    public static void MapBacktestEndpoints(this IEndpointRouteBuilder app)
    {
        _isDevelopment = app.ServiceProvider.GetRequiredService<IWebHostEnvironment>().IsDevelopment();

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

        group.MapGet("/{id:guid}/trades", GetTradePnl)
            .WithName("GetBacktestTrades")
            .WithSummary("Get per-trade PnL for a backtest run")
            .WithOpenApi()
            .Produces<IReadOnlyList<TradePointResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/events", GetBacktestEvents)
            .WithName("GetBacktestEvents")
            .WithSummary("Get bulk chart data (candles, trades, indicators) for a backtest run")
            .WithOpenApi()
            .Produces<EventsDataResponse>(StatusCodes.Status200OK)
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
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteBacktest)
            .WithName("DeleteBacktest")
            .WithSummary("Delete a standalone backtest run")
            .WithOpenApi()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> RunBacktest(
        RunBacktestRequest request,
        ICommandHandler<RunBacktestCommand, BacktestSubmissionDto> handler,
        CancellationToken ct)
    {
        if (request.DataSubscriptions is not { Count: > 0 })
            return Results.BadRequest("At least one data subscription is required.");

        var command = new RunBacktestCommand
        {
            DataSubscriptions = request.DataSubscriptions,
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = request.BacktestSettings.InitialCash,
                StartTime = request.BacktestSettings.StartTime,
                EndTime = request.BacktestSettings.EndTime,
                CommissionPerTrade = request.BacktestSettings.CommissionPerTrade,
                SlippageTicks = request.BacktestSettings.SlippageTicks,
            },
            StrategyName = request.StrategyName,
            StrategyParameters = request.StrategyParameters,
            FitnessConfig = request.FitnessWeights?.ToFitnessConfig(),
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
        catch (DirectoryNotFoundException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetBacktestStatus(
        Guid id,
        IQueryHandler<GetBacktestStatusQuery, BacktestStatusDto?> handler,
        CancellationToken ct)
    {
        var dto = await handler.HandleAsync(new GetBacktestStatusQuery(id), ct);
        if (dto is null)
            return Results.NotFound(new { error = $"Run '{id}' not found." });

        return Results.Ok(new BacktestStatusResponse
        {
            Id = dto.Id,
            ProcessedBars = dto.ProcessedBars,
            TotalBars = dto.TotalBars,
            Result = dto.Result is not null ? MapToResponse(dto.Result) : null,
        });
    }

    private static async Task<IResult> CancelBacktest(
        Guid id,
        ICommandHandler<CancelRunCommand, bool> handler,
        CancellationToken ct)
    {
        var cancelled = await handler.HandleAsync(new CancelRunCommand(id), ct);
        if (!cancelled)
            return Results.NotFound(new { error = $"Run '{id}' not found." });

        return Results.Ok(new { id, status = "Cancelled" });
    }

    private static async Task<IResult> ListBacktests(
        IQueryHandler<ListBacktestRunsQuery, PagedResult<BacktestRunRecord>> handler,
        string? strategyName,
        string? assetName,
        string? exchange,
        string? timeFrame,
        bool? standaloneOnly,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? sortBy,
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
            SortBy = sortBy,
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
        IQueryHandler<GetBacktestByIdQuery, BacktestRunRecord?> handler,
        CancellationToken ct)
    {
        var record = await handler.HandleAsync(new GetBacktestByIdQuery(id), ct);
        if (record is null)
            return Results.NotFound(new { error = $"Backtest with ID '{id}' not found." });

        return Results.Ok(MapToResponse(record));
    }

    private static async Task<IResult> GetEquityCurve(
        Guid id,
        IQueryHandler<GetBacktestByIdQuery, BacktestRunRecord?> handler,
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

    private static async Task<IResult> GetBacktestEvents(
        Guid id,
        IQueryHandler<GetBacktestByIdQuery, BacktestRunRecord?> handler,
        IRunTradeLogReader tradeLogReader,
        IAssetRepository assetRepository,
        IHistoryRepository historyRepository,
        ILogger<EventsDataResponse> logger,
        CancellationToken ct)
    {
        var record = await handler.HandleAsync(new GetBacktestByIdQuery(id), ct);
        if (record is null)
            return Results.NotFound(new { error = $"Backtest with ID '{id}' not found." });
        if (record.RunFolderPath is null)
            return Results.NotFound(new { error = $"Backtest '{id}' has no run folder (events were not captured)." });

        // Chart the finest-granularity primary time-bar feed — matches entry resolution.
        var chartSub = record.DataSubscriptions
            .OfType<TimeBarSubscription>()
            .Where(s => s.Role == DataFeedRole.Primary)
            .OrderBy(s => s.TimeFrame.Duration)
            .FirstOrDefault();

        var candles = new List<CandlePointResponse>();
        var scale = new ScaleContext(0.01m);
        if (chartSub is not null)
        {
            var asset = await assetRepository.GetByNameAsync(chartSub.AssetName, chartSub.Exchange, ct);
            if (asset is not null)
            {
                scale = new ScaleContext(asset);
                var from = DateOnly.FromDateTime(record.BacktestSettings.StartTime.UtcDateTime);
                var to = DateOnly.FromDateTime(record.BacktestSettings.EndTime.UtcDateTime);
                try
                {
                    var series = await historyRepository.Load(asset, chartSub, from, to, ct);
                    candles.Capacity = series.Count;
                    foreach (var bar in series)
                        candles.Add(new CandlePointResponse(
                            bar.TimestampMs / 1000,
                            scale.TicksToAmount(bar.Open),
                            scale.TicksToAmount(bar.High),
                            scale.TicksToAmount(bar.Low),
                            scale.TicksToAmount(bar.Close),
                            bar.Volume));
                }
                catch (ArgumentException ex)
                {
                    // History moved/deleted since the run — serve trades without candles
                    // rather than failing the whole report.
                    logger.LogWarning(ex, "Cannot reload candles for backtest {Id}", id);
                }
            }
        }

        var trades = (await tradeLogReader.Read(record.RunFolderPath, ct))
            .Select(t => new TradeMarkerResponse(
                t.EntryTime.ToUnixTimeSeconds(),
                scale.TicksToAmount(t.EntryPrice),
                t.ExitTime?.ToUnixTimeSeconds(),
                t.ExitPrice is { } xp ? scale.TicksToAmount(xp) : null,
                t.Side,
                t.Quantity,
                t.Pnl is { } pnl ? scale.TicksToAmount(pnl) : null,
                scale.TicksToAmount(t.Commission),
                t.TakeProfitPrice is { } tp ? scale.TicksToAmount(tp) : null,
                t.StopLossPrice is { } sl ? scale.TicksToAmount(sl) : null))
            .ToList();

        return Results.Ok(new EventsDataResponse(candles, new Dictionary<string, object>(), trades));
    }

    private static async Task<IResult> GetTradePnl(
        Guid id,
        IRunRepository repository,
        CancellationToken ct)
    {
        var trades = await repository.GetTradePnlAsync(id, ct);
        if (trades is null)
            return Results.NotFound(new { error = $"Backtest '{id}' not found." });

        return Results.Ok(trades.Select(t => new TradePointResponse(t.TimestampMs, t.Pnl)).ToList());
    }

    private static async Task<IResult> DeleteBacktest(
        Guid id,
        ICommandHandler<DeleteBacktestCommand, bool> handler,
        CancellationToken ct)
    {
        var deleted = await handler.HandleAsync(new DeleteBacktestCommand(id), ct);
        if (!deleted)
            return Results.NotFound(new { error = $"Backtest with ID '{id}' not found." });

        return Results.NoContent();
    }

    internal static BacktestRunResponse MapToResponse(BacktestRunRecord r) => new()
    {
        Id = r.Id,
        StrategyName = r.StrategyName,
        StrategyVersion = r.StrategyVersion,
        Parameters = new Dictionary<string, object>(r.Parameters),
        DataSubscriptions = r.DataSubscriptions,
        BacktestSettings = r.BacktestSettings,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        DurationMs = r.DurationMs,
        TotalBars = r.TotalBars,
        Metrics = MetricsMapping.ToDict(r.Metrics, r.FitnessScore),
        HasCandleData = r.RunFolderPath is not null,
        RunMode = r.RunMode,
        OptimizationRunId = r.OptimizationRunId,
        ErrorMessage = r.ErrorMessage,
        ErrorStackTrace = _isDevelopment ? r.ErrorStackTrace : null,
    };
}

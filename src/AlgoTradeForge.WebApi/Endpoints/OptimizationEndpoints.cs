using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class OptimizationEndpoints
{
    private static bool _isDevelopment;
    public static void MapOptimizationEndpoints(this IEndpointRouteBuilder app)
    {
        _isDevelopment = app.ServiceProvider.GetRequiredService<IWebHostEnvironment>().IsDevelopment();

        var group = app.MapGroup("/api/optimizations")
            .WithTags("Optimizations");

        group.MapPost("/", RunOptimization)
            .WithName("RunOptimization")
            .WithSummary("Submit an optimization for background execution")
            .WithOpenApi()
            .Produces<OptimizationSubmissionResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListOptimizations)
            .WithName("ListOptimizations")
            .WithSummary("List optimization runs with optional filters")
            .WithOpenApi()
            .Produces<PagedResponse<OptimizationRunResponse>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetOptimization)
            .WithName("GetOptimization")
            .WithSummary("Get an optimization run with all trials")
            .WithOpenApi()
            .Produces<OptimizationRunResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/status", GetOptimizationStatus)
            .WithName("GetOptimizationStatus")
            .WithSummary("Poll for optimization progress and results")
            .WithOpenApi()
            .Produces<OptimizationStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/cancel", CancelOptimization)
            .WithName("CancelOptimization")
            .WithSummary("Cancel an in-progress optimization")
            .WithOpenApi()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> RunOptimization(
        RunOptimizationRequest request,
        ICommandHandler<RunOptimizationCommand, OptimizationSubmissionDto> handler,
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
            SortBy = request.SortBy,
            MaxTrialsToKeep = request.MaxTrialsToKeep,
            MinProfitFactor = request.MinProfitFactor,
            MaxDrawdownPct = request.MaxDrawdownPct,
            MinSharpeRatio = request.MinSharpeRatio,
            MinSortinoRatio = request.MinSortinoRatio,
            MinAnnualizedReturnPct = request.MinAnnualizedReturnPct,
        };

        try
        {
            var submission = await handler.HandleAsync(command, ct);
            var response = new OptimizationSubmissionResponse
            {
                Id = submission.Id,
                TotalCombinations = submission.TotalCombinations,
            };
            return Results.Accepted($"/api/optimizations/{submission.Id}/status", response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetOptimizationStatus(
        Guid id,
        ICommandHandler<GetOptimizationStatusQuery, OptimizationStatusDto?> handler,
        CancellationToken ct)
    {
        var dto = await handler.HandleAsync(new GetOptimizationStatusQuery(id), ct);
        if (dto is null)
            return Results.NotFound(new { error = $"Run '{id}' not found." });

        return Results.Ok(new OptimizationStatusResponse
        {
            Id = dto.Id,
            CompletedCombinations = dto.CompletedCombinations,
            TotalCombinations = dto.TotalCombinations,
            FilteredTrials = dto.Result?.FilteredTrials ?? 0,
            FailedTrials = dto.Result?.FailedTrials ?? 0,
            Result = dto.Result is not null ? MapToResponse(dto.Result) : null,
        });
    }

    private static async Task<IResult> CancelOptimization(
        Guid id,
        ICommandHandler<CancelRunCommand, bool> handler,
        CancellationToken ct)
    {
        var cancelled = await handler.HandleAsync(new CancelRunCommand(id), ct);
        if (!cancelled)
            return Results.NotFound(new { error = $"Run '{id}' not found." });

        return Results.Ok(new { id, status = "Cancelled" });
    }

    private static async Task<IResult> ListOptimizations(
        ICommandHandler<ListOptimizationRunsQuery, PagedResult<OptimizationRunRecord>> handler,
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
        var filter = new OptimizationRunQuery
        {
            StrategyName = strategyName,
            AssetName = assetName,
            Exchange = exchange,
            TimeFrame = timeFrame,
            From = from,
            To = to,
            Limit = limit,
            Offset = offset,
        };
        var query = new ListOptimizationRunsQuery(filter);

        var paged = await handler.HandleAsync(query, ct);
        var items = paged.Items.Select(MapToResponse).ToList();
        var response = new PagedResponse<OptimizationRunResponse>(
            items, paged.TotalCount, filter.Limit, filter.Offset,
            filter.Offset + items.Count < paged.TotalCount);
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
        FilteredTrials = r.FilteredTrials,
        FailedTrials = r.FailedTrials,
        SortBy = r.SortBy,
        DataStart = r.DataStart,
        DataEnd = r.DataEnd,
        InitialCash = r.InitialCash,
        Commission = r.Commission,
        SlippageTicks = r.SlippageTicks,
        MaxParallelism = r.MaxParallelism,
        AssetName = r.AssetName,
        Exchange = r.Exchange,
        TimeFrame = r.TimeFrame,
        Trials = r.Trials.Select(MapTrialToResponse).ToList(),
    };

    private static BacktestRunResponse MapTrialToResponse(BacktestRunRecord r) => new()
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
        ErrorStackTrace = _isDevelopment ? r.ErrorStackTrace : null,
    };
}

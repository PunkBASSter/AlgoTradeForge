using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Optimization.Genetic;
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
            .WithSummary("Submit a brute-force optimization for background execution")
            .WithOpenApi()
            .Produces<OptimizationSubmissionResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/genetic", RunGeneticOptimization)
            .WithName("RunGeneticOptimization")
            .WithSummary("Submit a genetic algorithm optimization for background execution")
            .WithOpenApi()
            .Produces<OptimizationSubmissionResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/evaluate", EvaluateOptimization)
            .WithName("EvaluateOptimization")
            .WithSummary("Preview combination count and GA config without running")
            .WithOpenApi()
            .Produces<OptimizationEvaluationResponse>(StatusCodes.Status200OK)
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

        group.MapDelete("/{id:guid}", DeleteOptimization)
            .WithName("DeleteOptimization")
            .WithSummary("Delete an optimization and all related runs")
            .WithOpenApi()
            .Produces(StatusCodes.Status204NoContent)
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
            SubscriptionAxis = request.SubscriptionAxis,
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = request.BacktestSettings.InitialCash,
                StartTime = request.BacktestSettings.StartTime,
                EndTime = request.BacktestSettings.EndTime,
                CommissionPerTrade = request.BacktestSettings.CommissionPerTrade,
                SlippageTicks = request.BacktestSettings.SlippageTicks,
            },
            MaxDegreeOfParallelism = request.OptimizationSettings.MaxDegreeOfParallelism,
            MaxCombinations = request.OptimizationSettings.MaxCombinations,
            SortBy = request.OptimizationSettings.SortBy,
            MaxTrialsToKeep = request.OptimizationSettings.MaxTrialsToKeep,
            MinProfitFactor = request.OptimizationSettings.MinProfitFactor,
            MaxDrawdownPct = request.OptimizationSettings.MaxDrawdownPct,
            MinSharpeRatio = request.OptimizationSettings.MinSharpeRatio,
            MinSortinoRatio = request.OptimizationSettings.MinSortinoRatio,
            MinAnnualizedReturnPct = request.OptimizationSettings.MinAnnualizedReturnPct,
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

    private static async Task<IResult> RunGeneticOptimization(
        RunGeneticOptimizationRequest request,
        ICommandHandler<RunGeneticOptimizationCommand, OptimizationSubmissionDto> handler,
        CancellationToken ct)
    {
        var command = new RunGeneticOptimizationCommand
        {
            StrategyName = request.StrategyName,
            Axes = request.OptimizationAxes,
            DataSubscriptions = request.DataSubscriptions,
            SubscriptionAxis = request.SubscriptionAxis,
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = request.BacktestSettings.InitialCash,
                StartTime = request.BacktestSettings.StartTime,
                EndTime = request.BacktestSettings.EndTime,
                CommissionPerTrade = request.BacktestSettings.CommissionPerTrade,
                SlippageTicks = request.BacktestSettings.SlippageTicks,
            },
            MaxDegreeOfParallelism = request.OptimizationSettings.MaxDegreeOfParallelism,
            SortBy = request.OptimizationSettings.SortBy,
            MaxTrialsToKeep = request.OptimizationSettings.MaxTrialsToKeep,
            MinProfitFactor = request.OptimizationSettings.MinProfitFactor,
            MaxDrawdownPct = request.OptimizationSettings.MaxDrawdownPct,
            MinSharpeRatio = request.OptimizationSettings.MinSharpeRatio,
            MinSortinoRatio = request.OptimizationSettings.MinSortinoRatio,
            MinAnnualizedReturnPct = request.OptimizationSettings.MinAnnualizedReturnPct,
            GeneticSettings = MapGeneticSettings(request.GeneticSettings),
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

    private static async Task<IResult> EvaluateOptimization(
        EvaluateOptimizationRequest request,
        IQueryHandler<EvaluateOptimizationQuery, OptimizationEvaluationDto> handler,
        CancellationToken ct)
    {
        var mode = request.Mode ?? "BruteForce";
        var query = new EvaluateOptimizationQuery
        {
            StrategyName = request.StrategyName,
            Axes = request.OptimizationAxes,
            DataSubscriptions = request.DataSubscriptions,
            SubscriptionAxis = request.SubscriptionAxis,
            MaxCombinations = request.OptimizationSettings?.MaxCombinations ?? 500_000,
            Mode = mode,
            GeneticSettings = mode.Equals("Genetic", StringComparison.OrdinalIgnoreCase) && request.GeneticSettings is { } gs
                ? MapGeneticSettings(gs)
                : null,
        };

        try
        {
            var dto = await handler.HandleAsync(query, ct);
            var response = new OptimizationEvaluationResponse
            {
                TotalCombinations = dto.TotalCombinations,
                ExceedsMaxCombinations = dto.ExceedsMaxCombinations,
                MaxCombinations = dto.MaxCombinations,
                EffectiveDimensions = dto.EffectiveDimensions,
                GeneticConfig = dto.GeneticConfig is { } gc
                    ? new ResolvedGeneticConfigResponse
                    {
                        PopulationSize = gc.PopulationSize,
                        MaxGenerations = gc.MaxGenerations,
                        MaxEvaluations = gc.MaxEvaluations,
                        MutationRate = gc.MutationRate,
                    }
                    : null,
            };
            return Results.Ok(response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static GeneticConfig MapGeneticSettings(GeneticSettingsInput gs)
    {
        var fw = gs.FitnessWeights;
        return new GeneticConfig
        {
            PopulationSize = gs.PopulationSize,
            MaxGenerations = gs.MaxGenerations,
            MaxEvaluations = gs.MaxEvaluations,
            EliteCount = gs.EliteCount,
            CrossoverRate = gs.CrossoverRate,
            TournamentSize = gs.TournamentSize,
            StagnationLimit = gs.StagnationLimit,
            TimeBudget = gs.TimeBudgetMinutes.HasValue
                ? TimeSpan.FromMinutes(gs.TimeBudgetMinutes.Value)
                : null,
            MinTrades = fw?.MinTrades ?? 10,
            MaxDrawdownThreshold = fw?.MaxDrawdownThreshold ?? 30.0,
            Weights = new FitnessWeights
            {
                SharpeWeight = fw?.SharpeWeight ?? 0.5,
                SortinoWeight = fw?.SortinoWeight ?? 0.2,
                ProfitFactorWeight = fw?.ProfitFactorWeight ?? 0.15,
                AnnualizedReturnWeight = fw?.AnnualizedReturnWeight ?? 0.15,
            },
        };
    }

    private static async Task<IResult> GetOptimizationStatus(
        Guid id,
        IQueryHandler<GetOptimizationStatusQuery, OptimizationStatusDto?> handler,
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
            Status = dto.Result?.Status ?? OptimizationRunStatus.InProgress,
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
        IQueryHandler<ListOptimizationRunsQuery, PagedResult<OptimizationRunRecord>> handler,
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
        IQueryHandler<GetOptimizationByIdQuery, OptimizationRunRecord?> handler,
        CancellationToken ct)
    {
        var record = await handler.HandleAsync(new GetOptimizationByIdQuery(id), ct);
        if (record is null)
            return Results.NotFound(new { error = $"Optimization with ID '{id}' not found." });

        return Results.Ok(MapToResponse(record));
    }

    private static async Task<IResult> DeleteOptimization(
        Guid id,
        ICommandHandler<DeleteOptimizationCommand, bool> handler,
        CancellationToken ct)
    {
        var deleted = await handler.HandleAsync(new DeleteOptimizationCommand(id), ct);
        if (!deleted)
            return Results.NotFound(new { error = $"Optimization with ID '{id}' not found." });

        return Results.NoContent();
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
        DataSubscription = r.DataSubscription,
        BacktestSettings = r.BacktestSettings,
        MaxParallelism = r.MaxParallelism,
        Trials = r.Trials.Select(MapTrialToResponse).ToList(),
        OptimizationMethod = r.OptimizationMethod,
        GenerationsCompleted = r.GenerationsCompleted,
        Status = r.Status,
        ErrorMessage = r.ErrorMessage,
        FailedTrialDetails = r.FailedTrialDetails.Select(f => new FailedTrialResponse
        {
            ExceptionType = f.ExceptionType,
            ExceptionMessage = f.ExceptionMessage,
            StackTrace = _isDevelopment ? f.StackTrace : null,
            SampleParameters = new Dictionary<string, object>(f.SampleParameters),
            OccurrenceCount = f.OccurrenceCount,
        }).ToList(),
    };

    private static BacktestRunResponse MapTrialToResponse(BacktestRunRecord r) => new()
    {
        Id = r.Id,
        StrategyName = r.StrategyName,
        StrategyVersion = r.StrategyVersion,
        Parameters = new Dictionary<string, object>(r.Parameters),
        DataSubscription = r.DataSubscription,
        BacktestSettings = r.BacktestSettings,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
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

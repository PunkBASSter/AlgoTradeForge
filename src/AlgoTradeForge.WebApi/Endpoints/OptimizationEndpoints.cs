using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class OptimizationEndpoints
{
    private static bool _isDevelopment;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
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
            .WithSummary("Get optimization run metadata (trials loaded separately)")
            .WithOpenApi()
            .Produces<OptimizationRunResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/trials", GetOptimizationTrials)
            .WithName("GetOptimizationTrials")
            .WithSummary("List trials for an optimization with pagination")
            .WithOpenApi()
            .Produces<PagedResponse<BacktestRunResponse>>(StatusCodes.Status200OK);

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

        // Group sub-routes
        group.MapGet("/groups/{groupId:guid}", GetOptimizationGroup)
            .WithName("GetOptimizationGroup")
            .WithSummary("Get optimization group detail with child runs")
            .WithOpenApi()
            .Produces<OptimizationGroupDetailResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/groups/{groupId:guid}/trials", GetOptimizationGroupTrials)
            .WithName("GetOptimizationGroupTrials")
            .WithSummary("List cross-DSS trials for an optimization group")
            .WithOpenApi()
            .Produces<PagedResponse<BacktestRunResponse>>(StatusCodes.Status200OK);

        group.MapGet("/groups/{groupId:guid}/status", GetOptimizationGroupStatus)
            .WithName("GetOptimizationGroupStatus")
            .WithSummary("Poll optimization group progress")
            .WithOpenApi()
            .Produces<OptimizationGroupStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/groups/{groupId:guid}/cancel", CancelOptimizationGroup)
            .WithName("CancelOptimizationGroup")
            .WithSummary("Cancel all in-progress runs in the group")
            .WithOpenApi()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/groups/{groupId:guid}", DeleteOptimizationGroup)
            .WithName("DeleteOptimizationGroup")
            .WithSummary("Delete optimization group and all related data")
            .WithOpenApi()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> RunOptimization(
        RunOptimizationRequest request,
        ICommandHandler<RunOptimizationCommand, OptimizationSubmissionDto> handler,
        ICommandHandler<RunGroupOptimizationCommand, OptimizationGroupSubmissionDto> groupHandler,
        CancellationToken ct)
    {
        var backtestSettings = new BacktestSettingsDto
        {
            InitialCash = request.BacktestSettings.InitialCash,
            StartTime = request.BacktestSettings.StartTime,
            EndTime = request.BacktestSettings.EndTime,
            CommissionPerTrade = request.BacktestSettings.CommissionPerTrade,
            SlippageTicks = request.BacktestSettings.SlippageTicks,
        };
        var inputJson = JsonSerializer.Serialize(request, JsonOptions);

        // Dispatch to group handler when DSS is present
        if (request.SubscriptionAxis is { Count: > 0 })
        {
            var groupCommand = new RunGroupOptimizationCommand
            {
                StrategyName = request.StrategyName,
                OptimizationMethod = "BruteForce",
                Axes = request.OptimizationAxes,
                SubscriptionAxis = request.SubscriptionAxis,
                BacktestSettings = backtestSettings,
                MaxDegreeOfParallelism = request.OptimizationSettings.MaxDegreeOfParallelism,
                MaxCombinations = request.OptimizationSettings.MaxCombinations,
                MaxTrialsToKeep = request.OptimizationSettings.MaxTrialsToKeep,
                MinProfitFactor = request.OptimizationSettings.MinProfitFactor,
                MaxDrawdownPct = request.OptimizationSettings.MaxDrawdownPct,
                MinSharpeRatio = request.OptimizationSettings.MinSharpeRatio,
                MinSortinoRatio = request.OptimizationSettings.MinSortinoRatio,
                MinAnnualizedReturnPct = request.OptimizationSettings.MinAnnualizedReturnPct,
                MinTradeCount = request.OptimizationSettings.MinTradeCount,
                MinNetProfit = request.OptimizationSettings.MinNetProfit,
                FitnessConfig = MapFitnessConfig(request.OptimizationSettings.FitnessWeights),
                InputJson = inputJson,
                Validate = request.Validate,
                ThresholdProfileName = request.ThresholdProfileName,
                MaxThreads = request.MaxThreads,
            };

            return await DispatchGroupOptimization(groupCommand, groupHandler, ct);
        }

        // Single-run path (no DSS — backward compat)
        var command = new RunOptimizationCommand
        {
            StrategyName = request.StrategyName,
            Axes = request.OptimizationAxes,
            SubscriptionAxis = request.SubscriptionAxis,
            BacktestSettings = backtestSettings,
            MaxDegreeOfParallelism = request.OptimizationSettings.MaxDegreeOfParallelism,
            MaxCombinations = request.OptimizationSettings.MaxCombinations,
            MaxTrialsToKeep = request.OptimizationSettings.MaxTrialsToKeep,
            MinProfitFactor = request.OptimizationSettings.MinProfitFactor,
            MaxDrawdownPct = request.OptimizationSettings.MaxDrawdownPct,
            MinSharpeRatio = request.OptimizationSettings.MinSharpeRatio,
            MinSortinoRatio = request.OptimizationSettings.MinSortinoRatio,
            MinAnnualizedReturnPct = request.OptimizationSettings.MinAnnualizedReturnPct,
            MinTradeCount = request.OptimizationSettings.MinTradeCount,
            MinNetProfit = request.OptimizationSettings.MinNetProfit,
            FitnessConfig = MapFitnessConfig(request.OptimizationSettings.FitnessWeights),
            InputJson = inputJson,
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
        ICommandHandler<RunGroupOptimizationCommand, OptimizationGroupSubmissionDto> groupHandler,
        CancellationToken ct)
    {
        var backtestSettings = new BacktestSettingsDto
        {
            InitialCash = request.BacktestSettings.InitialCash,
            StartTime = request.BacktestSettings.StartTime,
            EndTime = request.BacktestSettings.EndTime,
            CommissionPerTrade = request.BacktestSettings.CommissionPerTrade,
            SlippageTicks = request.BacktestSettings.SlippageTicks,
        };
        var inputJson = JsonSerializer.Serialize(request, JsonOptions);

        // Genetic group mode not yet implemented — fail fast at the boundary
        if (request.SubscriptionAxis is { Count: > 0 })
            return Results.BadRequest(new { error = "Genetic optimization does not yet support multi-DSS groups. Use brute-force mode or run each DSS individually." });

        // Single-run path (no DSS — backward compat)
        var command = new RunGeneticOptimizationCommand
        {
            StrategyName = request.StrategyName,
            Axes = request.OptimizationAxes,
            SubscriptionAxis = request.SubscriptionAxis,
            BacktestSettings = backtestSettings,
            MaxDegreeOfParallelism = request.MaxThreads > 0 ? request.MaxThreads : request.OptimizationSettings.MaxDegreeOfParallelism,
            MaxTrialsToKeep = request.OptimizationSettings.MaxTrialsToKeep,
            MinProfitFactor = request.OptimizationSettings.MinProfitFactor,
            MaxDrawdownPct = request.OptimizationSettings.MaxDrawdownPct,
            MinSharpeRatio = request.OptimizationSettings.MinSharpeRatio,
            MinSortinoRatio = request.OptimizationSettings.MinSortinoRatio,
            MinAnnualizedReturnPct = request.OptimizationSettings.MinAnnualizedReturnPct,
            MinTradeCount = request.OptimizationSettings.MinTradeCount,
            MinNetProfit = request.OptimizationSettings.MinNetProfit,
            GeneticSettings = MapGeneticSettings(request.GeneticSettings, request.OptimizationSettings.FitnessWeights),
            InputJson = inputJson,
            Validate = request.Validate,
            ThresholdProfileName = request.ThresholdProfileName,
        };

        try
        {
            var submission = await handler.HandleAsync(command, ct);
            var response = new OptimizationSubmissionResponse
            {
                Id = submission.Id,
                TotalCombinations = submission.TotalCombinations,
                EnqueuedTasks = submission.EnqueuedTasks,
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
            SubscriptionAxis = request.SubscriptionAxis,
            MaxCombinations = request.OptimizationSettings?.MaxCombinations ?? 500_000,
            Mode = mode,
            GeneticSettings = mode.Equals("Genetic", StringComparison.OrdinalIgnoreCase) && request.GeneticSettings is { } gs
                ? MapGeneticSettings(gs, request.OptimizationSettings?.FitnessWeights)
                : null,
        };

        try
        {
            var dto = await handler.HandleAsync(query, ct);
            var response = new OptimizationEvaluationResponse
            {
                TotalCombinations = dto.TotalCombinations,
                UniqueCombinations = dto.UniqueCombinations,
                ExceedsMaxCombinations = dto.ExceedsMaxCombinations,
                MaxCombinations = dto.MaxCombinations,
                EffectiveDimensions = dto.EffectiveDimensions,
                DataSubscriptionSetsCount = dto.DssCount,
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

    private static GeneticConfig MapGeneticSettings(GeneticSettingsInput gs, FitnessWeightsInput? fw)
    {
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
            Fitness = MapFitnessConfig(fw) ?? new FitnessConfig(),
        };
    }

    private static FitnessConfig? MapFitnessConfig(FitnessWeightsInput? fw) => fw?.ToFitnessConfig();

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

    private static async Task<IResult> GetOptimizationTrials(
        Guid id,
        IQueryHandler<GetOptimizationTrialsQuery, PagedResult<BacktestRunRecord>> handler,
        int limit = 50,
        int offset = 0,
        string? sortBy = null,
        CancellationToken ct = default)
    {
        var paged = await handler.HandleAsync(
            new GetOptimizationTrialsQuery(id, limit, offset, sortBy), ct);
        var items = paged.Items.Select(MapTrialToResponse).ToList();
        return Results.Ok(new PagedResponse<BacktestRunResponse>(
            items, paged.TotalCount, limit, offset,
            offset + items.Count < paged.TotalCount));
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
        DedupSkipped = r.DedupSkipped,
        SortBy = r.SortBy,
        DataSubscriptions = r.DataSubscriptions,
        BacktestSettings = r.BacktestSettings,
        MaxParallelism = r.MaxParallelism,
        TrialCount = r.TrialCount,
        Trials = r.Trials.Select(MapTrialToResponse).ToList(),
        OptimizationMethod = r.OptimizationMethod,
        GenerationsCompleted = r.GenerationsCompleted,
        InputJson = r.InputJson,
        Status = r.Status,
        ErrorMessage = r.ErrorMessage,
        GroupId = r.GroupId,
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
        Params = r.Params,
    };

    // ── Shared group dispatch helper ────────────────────────────────────

    private static async Task<IResult> DispatchGroupOptimization(
        RunGroupOptimizationCommand groupCommand,
        ICommandHandler<RunGroupOptimizationCommand, OptimizationGroupSubmissionDto> groupHandler,
        CancellationToken ct)
    {
        try
        {
            var groupSubmission = await groupHandler.HandleAsync(groupCommand, ct);
            var groupResponse = new OptimizationGroupSubmissionResponse
            {
                GroupId = groupSubmission.GroupId,
                TotalCombinationsPerRun = groupSubmission.TotalCombinationsPerRun,
                Runs = groupSubmission.Runs.Select(r => new GroupRunSubmission
                {
                    Id = r.Id,
                    Dss = r.Dss.Select(d => new DataSubscriptionInput
                    {
                        AssetName = d.AssetName,
                        Exchange = d.Exchange,
                        TimeFrame = d.TimeFrame,
                    }).ToList(),
                    TotalCombinations = r.TotalCombinations,
                }).ToList(),
            };
            return Results.Accepted($"/api/optimizations/groups/{groupSubmission.GroupId}/status", groupResponse);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // ── Group endpoint handlers ───────────────────────────────────────

    private static async Task<IResult> GetOptimizationGroup(
        Guid groupId,
        IQueryHandler<GetOptimizationGroupByIdQuery, OptimizationGroupRecord?> handler,
        CancellationToken ct)
    {
        var group = await handler.HandleAsync(new GetOptimizationGroupByIdQuery(groupId), ct);
        if (group is null)
            return Results.NotFound(new { error = $"Optimization group '{groupId}' not found." });

        return Results.Ok(MapGroupToResponse(group));
    }

    private static async Task<IResult> GetOptimizationGroupTrials(
        Guid groupId,
        IQueryHandler<GetOptimizationGroupTrialsQuery, PagedResult<BacktestRunRecord>> handler,
        int? limit,
        int? offset,
        string? sortBy,
        CancellationToken ct)
    {
        var effectiveLimit = limit ?? 1000;
        var effectiveOffset = offset ?? 0;
        var result = await handler.HandleAsync(
            new GetOptimizationGroupTrialsQuery(groupId, effectiveLimit, effectiveOffset, sortBy), ct);

        var items = result.Items.Select(MapTrialToResponse).ToList();
        return Results.Ok(new PagedResponse<BacktestRunResponse>(
            items, result.TotalCount, effectiveLimit, effectiveOffset,
            effectiveOffset + items.Count < result.TotalCount));
    }

    private static async Task<IResult> GetOptimizationGroupStatus(
        Guid groupId,
        IQueryHandler<GetOptimizationGroupStatusQuery, OptimizationGroupStatusDto?> handler,
        CancellationToken ct)
    {
        var dto = await handler.HandleAsync(new GetOptimizationGroupStatusQuery(groupId), ct);
        if (dto is null)
            return Results.NotFound(new { error = $"Optimization group '{groupId}' not found." });

        return Results.Ok(new OptimizationGroupStatusResponse
        {
            Id = dto.Id,
            Status = dto.Status,
            Runs = dto.Runs.Select(r => new GroupRunStatusResponse
            {
                Id = r.Id,
                Status = r.Status,
                Processed = r.Processed,
                Total = r.Total,
            }).ToList(),
        });
    }

    private static async Task<IResult> CancelOptimizationGroup(
        Guid groupId,
        ICommandHandler<CancelOptimizationGroupCommand, bool> handler,
        CancellationToken ct)
    {
        var found = await handler.HandleAsync(new CancelOptimizationGroupCommand(groupId), ct);
        if (!found)
            return Results.NotFound(new { error = $"Optimization group '{groupId}' not found." });

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteOptimizationGroup(
        Guid groupId,
        ICommandHandler<DeleteOptimizationGroupCommand, bool> handler,
        CancellationToken ct)
    {
        var deleted = await handler.HandleAsync(new DeleteOptimizationGroupCommand(groupId), ct);
        if (!deleted)
            return Results.NotFound(new { error = $"Optimization group '{groupId}' not found." });

        return Results.NoContent();
    }

    private static OptimizationGroupDetailResponse MapGroupToResponse(OptimizationGroupRecord group)
    {
        List<List<DataSubscriptionInput>> subscriptions = [];
        if (!string.IsNullOrEmpty(group.SubscriptionsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<List<DataSubscriptionInput>>>(
                    group.SubscriptionsJson, JsonOptions);
                if (parsed is not null)
                    subscriptions = parsed;
            }
            catch (JsonException)
            {
                // Malformed JSON — fall back to empty
            }
        }

        return new()
        {
            Id = group.Id,
            StrategyName = group.StrategyName,
            StrategyVersion = group.StrategyVersion ?? "",
            OptimizationMethod = group.OptimizationMethod,
            StartedAt = group.StartedAt,
            CompletedAt = group.CompletedAt,
            Status = group.Status,
            TotalRuns = group.TotalRuns,
            MaxParallelism = group.MaxParallelism,
            InputJson = group.InputJson,
            Subscriptions = subscriptions,
            Runs = group.Runs.Select(r => new GroupRunDetailResponse
            {
                Id = r.Id,
                Dss = r.DataSubscriptions.Select(d => new DataSubscriptionInput
                {
                    AssetName = d.AssetName,
                    Exchange = d.Exchange,
                    TimeFrame = d.TimeFrame,
                }).ToList(),
                Status = r.Status,
                TotalCombinations = r.TotalCombinations,
                KeptTrials = r.TrialCount,
                FilteredTrials = r.FilteredTrials,
                FailedTrials = r.FailedTrials,
                DurationMs = r.DurationMs,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
            }).ToList(),
        };
    }
}

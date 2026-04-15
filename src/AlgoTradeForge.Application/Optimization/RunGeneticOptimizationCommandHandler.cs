using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Validation;
using Microsoft.Extensions.Logging;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunGeneticOptimizationCommandHandler(
    OptimizationSetupHelper helper,
    OptimizationAxisResolver axisResolver,
    RunProgressCache progressCache,
    ComputeTaskQueue queue,
    ILogger<RunGeneticOptimizationCommandHandler> logger) : ICommandHandler<RunGeneticOptimizationCommand, OptimizationSubmissionDto>
{
    public async Task<OptimizationSubmissionDto> HandleAsync(
        RunGeneticOptimizationCommand command, CancellationToken ct = default)
    {
        // 1. Validate strategy descriptor
        var descriptor = helper.SpaceProvider.GetDescriptor(command.StrategyName)
            ?? throw new ArgumentException($"Strategy '{command.StrategyName}' not found.");

        var settings = command.BacktestSettings;

        // 2. Validate subscriptions (data loading deferred to executor)
        var subscriptionAxis = command.SubscriptionAxis;
        if (subscriptionAxis is not { Count: > 0 } || subscriptionAxis[0].Count == 0)
            throw new ArgumentException("At least one data subscription must be provided.");
        var primarySub = OptimizationSetupHelper.GetSubscriptionDtos(subscriptionAxis);

        // 3. Resolve axes and GA config
        var resolvedAxes = axisResolver.Resolve(descriptor, command.Axes);
        var activeAxes = resolvedAxes
            .Where(a => a switch
            {
                ResolvedNumericAxis n => n.Values.Count > 0,
                ResolvedDiscreteAxis d => d.Values.Count > 0,
                ResolvedModuleSlotAxis m => m.Variants.Count > 0,
                _ => true
            })
            .ToList();

        // Append subscription axis if multi-DSS (currently unsupported for genetic, but keep consistent)
        if (subscriptionAxis is { Count: > 0 })
        {
            var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
            var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);
            var (axisSubscriptionGroups, _) =
                await helper.ResolveSubscriptionsAsync(subscriptionAxis, fromDate, toDate, ct);
            var reqSubs = OptimizationSetupHelper.GetRequiredSubscriptionCount(descriptor.ParamsType);
            OptimizationSetupHelper.ValidateSubscriptionCounts(
                command.StrategyName, reqSubs, axisSubscriptionGroups);
            activeAxes = OptimizationSetupHelper.AppendSubscriptionAxisAndFilter(
                resolvedAxes, axisSubscriptionGroups);
        }

        ValidateGeneticSettings(command.GeneticSettings);
        var gaConfig = GeneticConfigResolver.Resolve(command.GeneticSettings, activeAxes);

        // 4. Create IDs and progress
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var groupId = runId; // Genetic uses runId as jobId (single-DSS)
        var groupRunKey = RunKeyBuilder.BuildGroupKey(
            command.StrategyName, settings, "Genetic",
            command.SubscriptionAxis, command.Axes);
        var maxParallelism = command.MaxDegreeOfParallelism > 0
            ? command.MaxDegreeOfParallelism
            : Environment.ProcessorCount;

        await progressCache.SetProgressAsync(runId, 0, gaConfig.MaxEvaluations, ct);

        // Everything below may fail — clean up progress cache reservation on error.
        try
        {
        // 5. Insert DB placeholder
        await helper.InsertPlaceholderAsync(new OptimizationRunRecord
        {
            Id = runId,
            StrategyName = command.StrategyName,
            StrategyVersion = "0",
            StartedAt = startedAt,
            CompletedAt = startedAt,
            DurationMs = 0,
            TotalCombinations = gaConfig.MaxEvaluations,
            SortBy = Fitness,
            DataSubscriptions = primarySub,
            BacktestSettings = command.BacktestSettings,
            MaxParallelism = maxParallelism,
            Trials = [],
            OptimizationMethod = "Genetic",
            InputJson = command.InputJson,
            Status = OptimizationRunStatus.Enqueued,
        }, ct);

        // 6. Build execution context and enqueue
        var normalizer = NormalizingEnumerable.TryCreateNormalizer(descriptor.ParamsType);
        var dssLabel = primarySub.Count > 0
            ? string.Join(", ", primarySub.Select(s => $"{s.AssetName}/{s.Exchange}/{s.TimeFrame}"))
            : command.StrategyName;

        var geneticCtx = new GeneticExecutionContext
        {
            StrategyName = command.StrategyName,
            BacktestSettings = settings,
            SubscriptionDtos = primarySub.ToList(),
            ActiveAxes = activeAxes,
            GaConfig = gaConfig,
            MaxParallelism = maxParallelism,
            MaxTrialsToKeep = command.MaxTrialsToKeep,
            FilterOptions = command,
            Normalizer = normalizer,
            GroupId = groupId,
            GroupRunKey = groupRunKey,
            StartedAt = startedAt,
            InputJson = command.InputJson,
        };

        var computeTasks = new List<ComputeTask>
        {
            new()
            {
                JobId = groupId,
                Type = ComputeTaskType.Optimization,
                DssIndex = 0,
                RunId = runId,
                DssLabel = dssLabel,
                ExecutionContext = geneticCtx,
            }
        };

        // Optionally pair with validation task
        if (command.Validate)
        {
            var valRunId = Guid.NewGuid();
            computeTasks.Add(new ComputeTask
            {
                JobId = groupId,
                Type = ComputeTaskType.Validation,
                DssIndex = 0,
                RunId = valRunId,
                DssLabel = dssLabel,
                ExecutionContext = new ValidationExecutionContext
                {
                    OptimizationRunId = runId,
                    StrategyName = command.StrategyName,
                    ThresholdProfileName = command.ThresholdProfileName,
                    ThresholdProfileJson = "{}",
                    Profile = ValidationThresholdProfile.GetByName(command.ThresholdProfileName),
                    StartedAt = startedAt,
                },
            });
        }

        queue.EnqueueRange(computeTasks);
        queue.RegisterJob(groupId, computeTasks.Count, isOptimizationJob: true, groupRunKey);

        logger.LogInformation(
            "Genetic optimization {RunId} enqueued ({Tasks} tasks, {MaxEvals} max evaluations)",
            runId, computeTasks.Count, gaConfig.MaxEvaluations);

        return new OptimizationSubmissionDto
        {
            Id = runId,
            TotalCombinations = gaConfig.MaxEvaluations,
            EnqueuedTasks = computeTasks.Count,
        };
        }
        catch
        {
            await progressCache.RemoveProgressAsync(runId);
            throw;
        }
    }

    private static void ValidateGeneticSettings(GeneticConfig settings)
    {
        if (settings.PopulationSize > 2000)
            throw new ArgumentException("PopulationSize cannot exceed 2,000.");

        if (settings.MaxGenerations > 5000)
            throw new ArgumentException("MaxGenerations cannot exceed 5,000.");

        if (settings.MaxEvaluations > 1_000_000)
            throw new ArgumentException("MaxEvaluations cannot exceed 1,000,000.");
    }
}

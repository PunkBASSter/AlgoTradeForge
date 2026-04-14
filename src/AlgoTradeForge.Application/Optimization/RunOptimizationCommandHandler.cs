using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Space;
using Microsoft.Extensions.Logging;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunOptimizationCommandHandler(
    OptimizationSetupHelper helper,
    OptimizationAxisResolver axisResolver,
    ICartesianProductGenerator cartesianGenerator,
    RunProgressCache progressCache,
    ComputeTaskQueue queue) : ICommandHandler<RunOptimizationCommand, OptimizationSubmissionDto>
{
    public async Task<OptimizationSubmissionDto> HandleAsync(RunOptimizationCommand command, CancellationToken ct = default)
    {
        // 1. Compute RunKey and check for dedup (under lock to prevent TOCTOU races)
        var runKey = RunKeyBuilder.Build(command);
        using (await progressCache.AcquireRunKeyLockAsync(runKey, ct))
        {
            var existingId = await progressCache.TryGetRunIdByKeyAsync(runKey, ct);
            if (existingId is not null)
            {
                var existing = await progressCache.GetProgressAsync(existingId.Value, ct);
                if (existing is not null)
                {
                    return new OptimizationSubmissionDto
                    {
                        Id = existingId.Value,
                        TotalCombinations = existing.Value.Total,
                    };
                }

                await progressCache.RemoveRunKeyAsync(runKey, ct);
            }

        // 2. Validation and data loading
        var descriptor = helper.SpaceProvider.GetDescriptor(command.StrategyName)
            ?? throw new ArgumentException($"Strategy '{command.StrategyName}' not found.");

        var settings = command.BacktestSettings;
        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        var (axisSubscriptionGroups, dataCache) =
            await helper.ResolveSubscriptionsAsync(
                command.SubscriptionAxis, fromDate, toDate, ct);

        var reqSubs = OptimizationSetupHelper.GetRequiredSubscriptionCount(descriptor.ParamsType);
        OptimizationSetupHelper.ValidateSubscriptionCounts(
            command.StrategyName, reqSubs, axisSubscriptionGroups);

        var resolvedAxes = axisResolver.Resolve(descriptor, command.Axes);
        var activeAxes = OptimizationSetupHelper.AppendSubscriptionAxisAndFilter(
            resolvedAxes, axisSubscriptionGroups);

        var estimatedCount = cartesianGenerator.EstimateCount(activeAxes);
        if (estimatedCount > command.MaxCombinations)
            throw new ArgumentException(
                $"Estimated {estimatedCount} combinations exceeds maximum of {command.MaxCombinations}.");

        // 3. Store progress marker in cache
        var startedAt = DateTimeOffset.UtcNow;
        var optimizationRunId = Guid.NewGuid();
        await progressCache.SetProgressAsync(optimizationRunId, 0, estimatedCount, ct);
        await progressCache.SetRunKeyAsync(runKey, optimizationRunId, ct);

        // Everything below runs outside the lock. On failure, clean up the reservation.
        try
        {
        // 4. Insert placeholder row so the run is visible in the list immediately
        var optPrimarySub = OptimizationSetupHelper.GetSubscriptionDtos(
            command.SubscriptionAxis);
        var maxParallelism = command.MaxDegreeOfParallelism > 0
            ? command.MaxDegreeOfParallelism
            : Environment.ProcessorCount;
        await helper.InsertPlaceholderAsync(new OptimizationRunRecord
        {
            Id = optimizationRunId,
            StrategyName = command.StrategyName,
            StrategyVersion = "0",
            StartedAt = startedAt,
            CompletedAt = startedAt,
            DurationMs = 0,
            TotalCombinations = estimatedCount,
            SortBy = Fitness,
            DataSubscriptions = optPrimarySub,
            BacktestSettings = command.BacktestSettings,
            MaxParallelism = maxParallelism,
            Trials = [],
            OptimizationMethod = "BruteForce",
            InputJson = command.InputJson,
            Status = OptimizationRunStatus.InProgress,
        }, ct);

        // 5. Enqueue compute task to the queue
        var normalizer = NormalizingEnumerable.TryCreateNormalizer(descriptor.ParamsType);
        var dssLabel = string.Join(", ", optPrimarySub
            .Select(s => $"{s.AssetName}/{s.Exchange}/{s.TimeFrame}"));

        var computeTask = new ComputeTask
        {
            JobId = optimizationRunId,
            Type = ComputeTaskType.Optimization,
            DssIndex = 0,
            RunId = optimizationRunId,
            DssLabel = dssLabel,
            ExecutionContext = new OptimizationExecutionContext
            {
                StrategyName = command.StrategyName,
                OptimizationMethod = "BruteForce",
                BacktestSettings = command.BacktestSettings,
                SubscriptionDtos = optPrimarySub.ToList(),
                ActiveAxes = activeAxes,
                EstimatedCount = estimatedCount,
                MaxParallelism = maxParallelism,
                MaxTrialsToKeep = command.MaxTrialsToKeep,
                FilterOptions = command,
                FitnessConfig = command.FitnessConfig ?? FitnessConfig.Default,
                Normalizer = normalizer,
                GroupId = optimizationRunId,
                GroupRunKey = runKey,
                StartedAt = startedAt,
                InputJson = command.InputJson,
            },
        };

        queue.Enqueue(computeTask);
        queue.RegisterJob(optimizationRunId, 1, isOptimizationJob: true, runKey);

        return new OptimizationSubmissionDto
        {
            Id = optimizationRunId,
            TotalCombinations = estimatedCount,
        };
        }
        catch
        {
            await progressCache.RemoveProgressAsync(optimizationRunId);
            await progressCache.RemoveRunKeyAsync(runKey);
            throw;
        }
        } // end using (runKey lock)
    }

}

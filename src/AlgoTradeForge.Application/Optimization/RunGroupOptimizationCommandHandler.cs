using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Validation;
using Microsoft.Extensions.Logging;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunGroupOptimizationCommandHandler(
    OptimizationSetupHelper helper,
    OptimizationAxisResolver axisResolver,
    ICartesianProductGenerator cartesianGenerator,
    IRunRepository repository,
    RunProgressCache progressCache,
    ComputeTaskQueue queue,
    ILogger<RunGroupOptimizationCommandHandler> logger) : ICommandHandler<RunGroupOptimizationCommand, OptimizationGroupSubmissionDto>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OptimizationGroupSubmissionDto> HandleAsync(
        RunGroupOptimizationCommand command, CancellationToken ct = default)
    {
        var isGenetic = command.OptimizationMethod == "Genetic";

        // 1. Compute group RunKey and check for dedup under a narrow lock.
        //    Only the dedup check + key reservation are inside the lock;
        //    data loading, DB inserts, and background launch happen outside.
        var groupRunKey = RunKeyBuilder.BuildGroupKey(
            command.StrategyName, command.BacktestSettings,
            command.OptimizationMethod, command.SubscriptionAxis, command.Axes);
        var groupId = Guid.NewGuid();
        using (await progressCache.AcquireRunKeyLockAsync(groupRunKey, ct))
        {
            var existingGroupId = await progressCache.TryGetRunIdByKeyAsync(groupRunKey, ct);
            if (existingGroupId is not null)
            {
                var existingProgress = await progressCache.GetProgressAsync(existingGroupId.Value, ct);
                if (existingProgress is not null)
                {
                    // Group is already in progress — return the existing group info
                    logger.LogInformation(
                        "Group optimization dedup hit: existing group {GroupId} for key {RunKey}",
                        existingGroupId.Value, groupRunKey);
                    return new OptimizationGroupSubmissionDto
                    {
                        GroupId = existingGroupId.Value,
                        Runs = [],
                        TotalCombinationsPerRun = existingProgress.Value.Total,
                    };
                }

                await progressCache.RemoveRunKeyAsync(groupRunKey, ct);
            }

            // Reserve the key immediately so concurrent requests see the dedup.
            // Progress placeholder uses Total=0; updated with real count after axis resolution.
            await progressCache.SetProgressAsync(groupId, 0, 0, ct);
            await progressCache.SetRunKeyAsync(groupRunKey, groupId, ct);
        }

        // Everything below runs outside the lock. On failure, clean up the reservation.
        try
        {
        // 2. Validate strategy descriptor
        var descriptor = helper.SpaceProvider.GetDescriptor(command.StrategyName)
            ?? throw new ArgumentException($"Strategy '{command.StrategyName}' not found.");

        var settings = command.BacktestSettings;

        // 3. Validate subscriptions (data loading deferred to executor at execution time)
        var subscriptionAxis = command.SubscriptionAxis;
        if (subscriptionAxis is not { Count: > 0 })
            throw new ArgumentException("At least one SubscriptionAxis group must be provided.");

        var dssCount = subscriptionAxis.Count;

        // 4. Resolve parameter axes (without subscription axis — each DSS gets its own subscription)
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

        // 5. Estimate work per DSS — brute-force uses cartesian count, genetic uses GA eval budget
        long estimatedCountPerDss;
        GeneticConfig? gaConfig = null;

        if (isGenetic)
        {
            var geneticSettings = command.GeneticSettings
                ?? throw new ArgumentException("GeneticSettings is required for Genetic optimization.");
            GeneticConfigResolver.ValidateSettings(geneticSettings);
            gaConfig = GeneticConfigResolver.Resolve(geneticSettings, activeAxes);
            estimatedCountPerDss = gaConfig.MaxEvaluations;
        }
        else
        {
            estimatedCountPerDss = cartesianGenerator.EstimateCount(activeAxes);
            if (estimatedCountPerDss > command.MaxCombinations)
                throw new ArgumentException(
                    $"Estimated {estimatedCountPerDss} combinations per DSS exceeds maximum of {command.MaxCombinations}.");
        }

        // 6. Create IDs
        var startedAt = DateTimeOffset.UtcNow;
        var maxParallelism = command.MaxDegreeOfParallelism > 0
            ? command.MaxDegreeOfParallelism
            : Environment.ProcessorCount;

        // 7. Insert optimization group placeholder
        await repository.InsertOptimizationGroupAsync(new OptimizationGroupRecord
        {
            Id = groupId,
            StrategyName = command.StrategyName,
            OptimizationMethod = command.OptimizationMethod,
            StartedAt = startedAt,
            TotalRuns = dssCount,
            Status = OptimizationGroupStatus.InProgress,
            InputJson = command.InputJson,
            SubscriptionsJson = JsonSerializer.Serialize(subscriptionAxis, JsonOptions),
            BacktestSettingsJson = JsonSerializer.Serialize(settings, JsonOptions),
            FitnessConfigJson = command.FitnessConfig is not null
                ? JsonSerializer.Serialize(command.FitnessConfig, JsonOptions)
                : null,
            MaxParallelism = maxParallelism,
        }, ct);

        // 8. Insert child optimization run placeholders (one per DSS)
        var childRuns = new List<GroupRunSubmissionDto>(dssCount);
        var childRunIds = new Guid[dssCount];

        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
        {
            var childRunId = Guid.NewGuid();
            childRunIds[dssIdx] = childRunId;

            var dssSubs = subscriptionAxis[dssIdx]
                .Select(s => new DataSubscriptionDto
                {
                    AssetName = s.AssetName,
                    Exchange = s.Exchange,
                    TimeFrame = s.TimeFrame,
                })
                .ToList();

            await helper.InsertPlaceholderAsync(new OptimizationRunRecord
            {
                Id = childRunId,
                StrategyName = command.StrategyName,
                StrategyVersion = "0",
                StartedAt = startedAt,
                CompletedAt = startedAt,
                DurationMs = 0,
                TotalCombinations = estimatedCountPerDss,
                SortBy = Fitness,
                DataSubscriptions = dssSubs,
                BacktestSettings = settings,
                MaxParallelism = maxParallelism,
                Trials = [],
                OptimizationMethod = command.OptimizationMethod,
                InputJson = command.InputJson,
                Status = OptimizationRunStatus.Enqueued,
                GroupId = groupId,
                DssIndex = dssIdx,
            }, ct);

            childRuns.Add(new GroupRunSubmissionDto
            {
                Id = childRunId,
                Dss = dssSubs,
                TotalCombinations = estimatedCountPerDss,
            });
        }

        // 9. Progress for each child run is set when it starts executing (not upfront)
        //    so enqueued runs correctly fall through to DB status check.

        // 10. Update group progress with real total (replaces the placeholder from the lock)
        await progressCache.SetProgressAsync(groupId, 0, estimatedCountPerDss * dssCount, ct);

        // 11. Enqueue compute tasks (optimization + optional validation per DSS)
        var normalizer = NormalizingEnumerable.TryCreateNormalizer(descriptor.ParamsType);

        var computeTasks = new List<ComputeTask>();
        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
        {
            var dssLabel = string.Join(", ", subscriptionAxis[dssIdx]
                .Select(s => $"{s.AssetName}/{s.Exchange}/{s.TimeFrame}"));

            var dssSubs = subscriptionAxis[dssIdx]
                .Select(s => new DataSubscriptionDto
                {
                    AssetName = s.AssetName,
                    Exchange = s.Exchange,
                    TimeFrame = s.TimeFrame,
                })
                .ToList();

            object executionCtx = isGenetic
                ? new GeneticExecutionContext
                {
                    StrategyName = command.StrategyName,
                    BacktestSettings = settings,
                    SubscriptionDtos = dssSubs,
                    ActiveAxes = activeAxes,
                    GaConfig = gaConfig!,
                    MaxParallelism = maxParallelism,
                    MaxTrialsToKeep = command.MaxTrialsToKeep,
                    FilterOptions = command,
                    Normalizer = normalizer,
                    GroupId = groupId,
                    GroupRunKey = groupRunKey,
                    StartedAt = startedAt,
                    InputJson = command.InputJson,
                }
                : new OptimizationExecutionContext
                {
                    StrategyName = command.StrategyName,
                    OptimizationMethod = command.OptimizationMethod,
                    BacktestSettings = settings,
                    SubscriptionDtos = dssSubs,
                    ActiveAxes = activeAxes,
                    EstimatedCount = estimatedCountPerDss,
                    MaxParallelism = maxParallelism,
                    MaxTrialsToKeep = command.MaxTrialsToKeep,
                    FilterOptions = command,
                    FitnessConfig = command.FitnessConfig ?? FitnessConfig.Default,
                    Normalizer = normalizer,
                    GroupId = groupId,
                    GroupRunKey = groupRunKey,
                    StartedAt = startedAt,
                    InputJson = command.InputJson,
                };

            computeTasks.Add(new ComputeTask
            {
                JobId = groupId,
                Type = ComputeTaskType.Optimization,
                DssIndex = dssIdx,
                RunId = childRunIds[dssIdx],
                DssLabel = dssLabel,
                ExecutionContext = executionCtx,
            });

            // Optionally pair with validation task
            if (command.Validate)
            {
                var valRunId = Guid.NewGuid();

                // Insert validation group + child placeholder
                // (deferred — created when validation task starts in the consumer)
                computeTasks.Add(new ComputeTask
                {
                    JobId = groupId,
                    Type = ComputeTaskType.Validation,
                    DssIndex = dssIdx,
                    RunId = valRunId,
                    DssLabel = dssLabel,
                    ExecutionContext = new ValidationExecutionContext
                    {
                        OptimizationRunId = childRunIds[dssIdx],
                        StrategyName = command.StrategyName,
                        ThresholdProfileName = command.ThresholdProfileName,
                        ThresholdProfileJson = "{}",
                        Profile = ValidationThresholdProfile.GetByName(command.ThresholdProfileName),
                        StartedAt = startedAt,
                    },
                });
            }
        }

        queue.EnqueueRange(computeTasks);

        // Register job tracker for group status finalization
        queue.RegisterJob(groupId, computeTasks.Count, isOptimizationJob: true, groupRunKey);

        logger.LogInformation(
            "Group optimization {GroupId} created with {DssCount} DSS, {TaskCount} tasks enqueued ({CombosPerDss} combinations each)",
            groupId, dssCount, computeTasks.Count, estimatedCountPerDss);

        return new OptimizationGroupSubmissionDto
        {
            GroupId = groupId,
            Runs = childRuns,
            TotalCombinationsPerRun = estimatedCountPerDss,
        };
        }
        catch
        {
            // Clean up the progress/key reservation so the key doesn't stay permanently claimed
            await progressCache.RemoveProgressAsync(groupId);
            await progressCache.RemoveRunKeyAsync(groupRunKey);
            throw;
        }
    }

}

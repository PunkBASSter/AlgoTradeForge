using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunGroupOptimizationCommandHandler(
    IOptimizationStrategyFactory strategyFactory,
    OptimizationSetupHelper helper,
    OptimizationAxisResolver axisResolver,
    ICartesianProductGenerator cartesianGenerator,
    IRunRepository repository,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    IOptions<RunTimeoutOptions> timeoutOptions,
    ILogger<RunGroupOptimizationCommandHandler> logger) : ICommandHandler<RunGroupOptimizationCommand, OptimizationGroupSubmissionDto>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OptimizationGroupSubmissionDto> HandleAsync(
        RunGroupOptimizationCommand command, CancellationToken ct = default)
    {
        if (command.OptimizationMethod != "BruteForce")
            throw new NotImplementedException("Genetic group mode not yet implemented");

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
        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        // 3. Resolve subscriptions for ALL DSS groups (I/O-heavy — loads CSV data)
        var subscriptionAxis = command.SubscriptionAxis;
        if (subscriptionAxis is not { Count: > 0 })
            throw new ArgumentException("At least one SubscriptionAxis group must be provided.");

        var reqSubs = OptimizationSetupHelper.GetRequiredSubscriptionCount(descriptor.ParamsType);
        var dssCount = subscriptionAxis.Count;

        var allDssSubscriptions = new List<List<DataSubscription>>(dssCount);
        var sharedDataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();

        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
        {
            var dssGroup = subscriptionAxis[dssIdx];
            var resolvedGroup = new List<DataSubscription>();
            foreach (var sub in dssGroup)
                await helper.ResolveAndCacheAsync(sub, resolvedGroup, sharedDataCache, fromDate, toDate, ct);
            allDssSubscriptions.Add(resolvedGroup);
        }

        OptimizationSetupHelper.ValidateSubscriptionCounts(
            command.StrategyName, reqSubs, allDssSubscriptions);

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

        // 5. Estimate combinations count (same for each DSS)
        var estimatedCountPerDss = cartesianGenerator.EstimateCount(activeAxes);
        if (estimatedCountPerDss > command.MaxCombinations)
            throw new ArgumentException(
                $"Estimated {estimatedCountPerDss} combinations per DSS exceeds maximum of {command.MaxCombinations}.");

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
                OptimizationMethod = "BruteForce",
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

        // 11. Launch background task
        var normalizer = NormalizingEnumerable.TryCreateNormalizer(descriptor.ParamsType);
        _ = Task.Factory.StartNew(
            () => RunGroupBruteForceAsync(
                command, sharedDataCache, allDssSubscriptions, activeAxes,
                estimatedCountPerDss, groupId, childRunIds, groupRunKey, startedAt,
                strategyFactory, normalizer),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        logger.LogInformation(
            "Group optimization {GroupId} created with {DssCount} child runs, {CombosPerDss} combinations each",
            groupId, dssCount, estimatedCountPerDss);

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

    private async Task RunGroupBruteForceAsync(
        RunGroupOptimizationCommand command,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> sharedDataCache,
        List<List<DataSubscription>> allDssSubscriptions,
        List<ResolvedAxis> activeAxes,
        long estimatedCountPerDss,
        Guid groupId,
        Guid[] childRunIds,
        string groupRunKey,
        DateTimeOffset startedAt,
        IOptimizationStrategyFactory factory,
        IParameterNormalizer? normalizer)
    {
        using var groupCts = new CancellationTokenSource(timeoutOptions.Value.OptimizationTimeout);
        cancellationRegistry.Register(groupId, groupCts);
        var groupCt = groupCts.Token;

        var dssCount = allDssSubscriptions.Count;
        var maxParallelism = command.MaxDegreeOfParallelism > 0
            ? command.MaxDegreeOfParallelism
            : Environment.ProcessorCount;

        if (command.MaxTrialsToKeep < 1)
            throw new ArgumentException("MaxTrialsToKeep must be at least 1.");

        var filter = new TrialFilter(command);
        var fitnessConfig = command.FitnessConfig ?? FitnessConfig.Default;
        var fitnessFunc = new CompositeFitnessFunction(fitnessConfig);

        // Per-DSS data cache: each DSS only needs its own subscriptions from the shared cache
        var perDssDataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>[dssCount];
        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
        {
            var dssCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
            foreach (var sub in allDssSubscriptions[dssIdx])
            {
                var key = OptimizationSetupHelper.CacheKey(sub.Asset, sub.TimeFrame);
                if (sharedDataCache.TryGetValue(key, out var cached))
                    dssCache[key] = cached;
            }
            perDssDataCache[dssIdx] = dssCache;
        }

        // Per-DSS subscription DTOs
        var perDssPrimarySub = new IReadOnlyList<DataSubscriptionDto>[dssCount];
        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
        {
            perDssPrimarySub[dssIdx] = command.SubscriptionAxis[dssIdx]
                .Select(s => new DataSubscriptionDto
                {
                    AssetName = s.AssetName,
                    Exchange = s.Exchange,
                    TimeFrame = s.TimeFrame,
                })
                .ToList();
        }

        var trialTimeout = timeoutOptions.Value.BacktestTimeout;
        var progressInterval = (long)Math.Clamp(estimatedCountPerDss / 10_000.0, 100, 10_000);

        // Track per-DSS results (allocated once, populated sequentially)
        var perDssTopTrials = new BoundedTrialQueue[dssCount];
        var perDssFailedTrials = new FailedTrialCollector[dssCount];
        var perDssFilteredOut = new long[dssCount];
        var perDssFailedCount = new long[dssCount];
        var perDssProcessed = new long[dssCount];
        var perDssStrategyVersion = new string?[dssCount];

        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
        {
            perDssTopTrials[dssIdx] = new BoundedTrialQueue(command.MaxTrialsToKeep, fitnessFunc);
            perDssFailedTrials[dssIdx] = new FailedTrialCollector(capacity: 100);
        }

        var childStatuses = new List<string>(dssCount);
        var overallStopwatch = Stopwatch.StartNew();

        try
        {
            // ── Sequential DSS loop: run each DSS to completion before starting the next ──
            for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
            {
                groupCt.ThrowIfCancellationRequested();

                var childRunId = childRunIds[dssIdx];
                var dssStopwatch = Stopwatch.StartNew();

                // Transition this child run: Enqueued → InProgress
                await repository.UpdateOptimizationRunStatusAsync(
                    childRunId, OptimizationRunStatus.InProgress, CancellationToken.None);
                await progressCache.SetProgressAsync(childRunId, 0, estimatedCountPerDss, CancellationToken.None);

                // Per-DSS CTS linked to group — cancelling either stops this DSS
                using var dssCts = CancellationTokenSource.CreateLinkedTokenSource(groupCt);
                cancellationRegistry.Register(childRunId, dssCts);
                var dssCt = dssCts.Token;

                try
                {
                    await RunSingleDssBruteForceAsync(
                        command, dssIdx, dssCt,
                        activeAxes, normalizer, factory,
                        allDssSubscriptions, perDssDataCache[dssIdx],
                        childRunId, startedAt,
                        perDssTopTrials[dssIdx], perDssFailedTrials[dssIdx],
                        perDssFilteredOut, perDssFailedCount,
                        perDssProcessed, perDssStrategyVersion,
                        filter, fitnessFunc,
                        estimatedCountPerDss, maxParallelism, trialTimeout, progressInterval,
                        groupId);

                    dssStopwatch.Stop();

                    // Final progress flush
                    var processed = Interlocked.Read(ref perDssProcessed[dssIdx]);
                    await progressCache.SetProgressAsync(childRunId, processed, estimatedCountPerDss, CancellationToken.None);

                    // Save completed results
                    var trials = perDssTopTrials[dssIdx].DeduplicateAndDrainSorted();
                    var failedTrialDetails = perDssFailedTrials[dssIdx].Drain(childRunId);

                    var record = new OptimizationRunRecord
                    {
                        Id = childRunId,
                        StrategyName = command.StrategyName,
                        StrategyVersion = perDssStrategyVersion[dssIdx] ?? "0",
                        StartedAt = startedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        DurationMs = dssStopwatch.ElapsedMilliseconds,
                        TotalCombinations = estimatedCountPerDss,
                        SortBy = Fitness,
                        DataSubscriptions = perDssPrimarySub[dssIdx],
                        BacktestSettings = command.BacktestSettings,
                        MaxParallelism = maxParallelism,
                        Trials = trials,
                        FailedTrialDetails = failedTrialDetails,
                        FilteredTrials = Interlocked.Read(ref perDssFilteredOut[dssIdx]),
                        FailedTrials = Interlocked.Read(ref perDssFailedCount[dssIdx]),
                        OptimizationMethod = "BruteForce",
                        GroupId = groupId,
                        DssIndex = dssIdx,
                    };

                    await helper.SaveOptimizationAsync(record);
                    childStatuses.Add(OptimizationRunStatus.Completed);

                    logger.LogInformation(
                        "Group {GroupId} DSS[{DssIndex}] run {RunId}: {Processed} executed, {Kept} kept, {Filtered} filtered, {Failed} failed in {Duration}ms",
                        groupId, dssIdx, childRunId, processed, trials.Count,
                        Interlocked.Read(ref perDssFilteredOut[dssIdx]),
                        Interlocked.Read(ref perDssFailedCount[dssIdx]),
                        dssStopwatch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (!groupCt.IsCancellationRequested)
                {
                    // Individual DSS was cancelled (not the group) — save partial results, continue to next
                    dssStopwatch.Stop();
                    logger.LogInformation(
                        "Group {GroupId} DSS[{DssIndex}] run {RunId} was cancelled individually",
                        groupId, dssIdx, childRunId);

                    await SaveSingleDssError(
                        command, dssIdx, childRunId, perDssPrimarySub[dssIdx],
                        startedAt, estimatedCountPerDss, maxParallelism,
                        perDssTopTrials[dssIdx], perDssFailedTrials[dssIdx],
                        perDssFilteredOut, perDssFailedCount,
                        OptimizationRunStatus.CancelledMessage, groupId);

                    childStatuses.Add(OptimizationRunStatus.Cancelled);
                }
                catch (OperationCanceledException)
                {
                    // Group-level cancellation — re-throw to outer handler
                    throw;
                }
                catch (Exception ex)
                {
                    // DSS failed — save error, continue to next
                    dssStopwatch.Stop();
                    logger.LogError(ex,
                        "Group {GroupId} DSS[{DssIndex}] run {RunId} failed",
                        groupId, dssIdx, childRunId);

                    await SaveSingleDssError(
                        command, dssIdx, childRunId, perDssPrimarySub[dssIdx],
                        startedAt, estimatedCountPerDss, maxParallelism,
                        perDssTopTrials[dssIdx], perDssFailedTrials[dssIdx],
                        perDssFilteredOut, perDssFailedCount,
                        ex.Message, groupId, ex.StackTrace);

                    childStatuses.Add(OptimizationRunStatus.Failed);
                }
                finally
                {
                    cancellationRegistry.Remove(childRunId);
                }
            }

            overallStopwatch.Stop();

            // Update group status from collected child statuses
            var groupStatus = GroupStatusCalculator.Compute(childStatuses);
            await repository.UpdateOptimizationGroupStatusAsync(
                groupId, groupStatus, DateTimeOffset.UtcNow, CancellationToken.None);

            logger.LogInformation(
                "Group optimization {GroupId} completed with status {Status}: {DssCount} DSS runs in {Duration}ms",
                groupId, groupStatus, dssCount, overallStopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Group optimization {GroupId} was cancelled", groupId);

            // Mark remaining enqueued runs as cancelled
            for (var dssIdx = childStatuses.Count; dssIdx < dssCount; dssIdx++)
            {
                var childRunId = childRunIds[dssIdx];
                await SaveSingleDssError(
                    command, dssIdx, childRunId, perDssPrimarySub[dssIdx],
                    startedAt, estimatedCountPerDss, maxParallelism,
                    perDssTopTrials[dssIdx], perDssFailedTrials[dssIdx],
                    perDssFilteredOut, perDssFailedCount,
                    OptimizationRunStatus.CancelledMessage, groupId);
            }

            await repository.UpdateOptimizationGroupStatusAsync(
                groupId, OptimizationGroupStatus.Cancelled, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Group optimization {GroupId} failed", groupId);

            // Mark remaining enqueued runs as failed
            for (var dssIdx = childStatuses.Count; dssIdx < dssCount; dssIdx++)
            {
                var childRunId = childRunIds[dssIdx];
                await SaveSingleDssError(
                    command, dssIdx, childRunId, perDssPrimarySub[dssIdx],
                    startedAt, estimatedCountPerDss, maxParallelism,
                    perDssTopTrials[dssIdx], perDssFailedTrials[dssIdx],
                    perDssFilteredOut, perDssFailedCount,
                    ex.Message, groupId, ex.StackTrace);
            }

            await repository.UpdateOptimizationGroupStatusAsync(
                groupId, OptimizationGroupStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        finally
        {
            for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
                await progressCache.RemoveProgressAsync(childRunIds[dssIdx]);

            await progressCache.RemoveProgressAsync(groupId);
            await progressCache.RemoveRunKeyAsync(groupRunKey);
            cancellationRegistry.Remove(groupId);
        }
    }

    /// <summary>Run partitioned parallel brute-force for a single DSS index.</summary>
    private async Task RunSingleDssBruteForceAsync(
        RunGroupOptimizationCommand command,
        int dssIdx,
        CancellationToken ct,
        List<ResolvedAxis> activeAxes,
        IParameterNormalizer? normalizer,
        IOptimizationStrategyFactory factory,
        List<List<DataSubscription>> allDssSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dssDataCache,
        Guid childRunId,
        DateTimeOffset startedAt,
        BoundedTrialQueue topTrials,
        FailedTrialCollector failedTrials,
        long[] filteredOutArr,
        long[] failedCountArr,
        long[] processedArr,
        string?[] strategyVersionArr,
        TrialFilter filter,
        CompositeFitnessFunction fitnessFunc,
        long estimatedCountPerDss,
        int maxParallelism,
        TimeSpan trialTimeout,
        long progressInterval,
        Guid groupId)
    {
        IEnumerable<ParameterCombination> combinations = cartesianGenerator.Enumerate(activeAxes);
        if (normalizer is not null)
        {
            var normEnumerable = new NormalizingEnumerable(combinations, normalizer);
            combinations = normEnumerable.Enumerate();
        }

        var partitions = Partitioner.Create(combinations, EnumerablePartitionerOptions.NoBuffering)
            .GetPartitions(maxParallelism);
        var tasks = new Task[partitions.Count];
        var workerItemCounts = new long[partitions.Count];
        for (var p = 0; p < partitions.Count; p++)
        {
            var workerId = p;
            var partition = partitions[p];
            tasks[p] = Task.Factory.StartNew(() =>
            {
                long localCount = 0;
                string exitReason = "enumeration-complete";
                try
                {
                    using (partition)
                    {
                        var trialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        try
                        {
                            while (partition.MoveNext())
                            {
                                ct.ThrowIfCancellationRequested();
                                var combo = partition.Current;

                                var mutableValues = new Dictionary<string, object>(combo.Values)
                                {
                                    ["DataSubscriptions"] = allDssSubscriptions[dssIdx]
                                };
                                var combinationWithSubs = new ParameterCombination(mutableValues);

                                if (!trialCts.TryReset())
                                {
                                    trialCts.Dispose();
                                    trialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                }
                                trialCts.CancelAfter(trialTimeout);

                                try
                                {
                                    var record = helper.ExecuteTrial(
                                        command.StrategyName, command.BacktestSettings,
                                        combinationWithSubs, factory, dssDataCache,
                                        childRunId, startedAt,
                                        ref strategyVersionArr[dssIdx], trialCts.Token);
                                    record = record with { FitnessScore = fitnessFunc.Evaluate(record.Metrics) };

                                    if (filter.Passes(record.Metrics))
                                        topTrials.TryAdd(record);
                                    else
                                        Interlocked.Increment(ref filteredOutArr[dssIdx]);
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    Interlocked.Increment(ref failedCountArr[dssIdx]);
                                    failedTrials.RecordTimeout(combo.Values, trialTimeout);
                                    logger.LogWarning(
                                        "Group {GroupId} DSS[{DssIndex}]: trial timed out after {Timeout}",
                                        groupId, dssIdx, trialTimeout);
                                }
                                catch (OperationCanceledException)
                                {
                                    exitReason = "cancelled";
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref failedCountArr[dssIdx]);
                                    failedTrials.Record(
                                        combo.Values,
                                        ex.GetType().FullName ?? ex.GetType().Name,
                                        ex.Message,
                                        ex.StackTrace ?? string.Empty);
                                    logger.LogWarning(ex,
                                        "Group {GroupId} DSS[{DssIndex}]: trial failed", groupId, dssIdx);
                                }

                                localCount++;
                                var count = Interlocked.Increment(ref processedArr[dssIdx]);
                                if (count % progressInterval == 0)
                                    _ = progressCache.SetProgressAsync(
                                        childRunId, count, estimatedCountPerDss, CancellationToken.None);
                            }
                        }
                        finally
                        {
                            trialCts.Dispose();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    exitReason = "cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    exitReason = $"exception: {ex.GetType().Name}: {ex.Message}";
                    throw;
                }
                finally
                {
                    workerItemCounts[workerId] = localCount;
                    logger.LogInformation(
                        "Group {GroupId} DSS[{DssIndex}] worker {WorkerId}/{Total} exited: {Reason}, processed {Count} items",
                        groupId, dssIdx, workerId, maxParallelism, exitReason, localCount);
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        await Task.WhenAll(tasks);

        var totalProcessedByWorkers = workerItemCounts.Sum();
        logger.LogInformation(
            "Group {GroupId} DSS[{DssIndex}]: all {WorkerCount} workers completed. Total items: {Total} (expected {Expected})",
            groupId, dssIdx, maxParallelism, totalProcessedByWorkers, estimatedCountPerDss);
    }

    private async Task SaveSingleDssError(
        RunGroupOptimizationCommand command,
        int dssIdx,
        Guid childRunId,
        IReadOnlyList<DataSubscriptionDto> dssSubs,
        DateTimeOffset startedAt,
        long estimatedCountPerDss,
        int maxParallelism,
        BoundedTrialQueue topTrials,
        FailedTrialCollector failedTrials,
        long[] filteredOutArr,
        long[] failedCountArr,
        string errorMessage,
        Guid groupId,
        string? errorStackTrace = null)
    {
        try
        {
            await helper.SaveErrorOptimizationAsync(
                command.StrategyName, command.BacktestSettings, dssSubs,
                Fitness, maxParallelism,
                childRunId, startedAt, estimatedCountPerDss,
                topTrials, failedTrials,
                Interlocked.Read(ref filteredOutArr[dssIdx]),
                Interlocked.Read(ref failedCountArr[dssIdx]),
                errorMessage, errorStackTrace,
                optimizationMethod: "BruteForce");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to persist error record for group child run {RunId}",
                childRunId);
        }
    }
}

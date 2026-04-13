using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
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
                Status = OptimizationRunStatus.InProgress,
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

        // 9. Set progress for each child run
        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
            await progressCache.SetProgressAsync(childRunIds[dssIdx], 0, estimatedCountPerDss, ct);

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
        using var cts = new CancellationTokenSource(timeoutOptions.Value.OptimizationTimeout);
        cancellationRegistry.Register(groupId, cts);
        var ct = cts.Token;

        var dssCount = allDssSubscriptions.Count;
        var maxParallelism = command.MaxDegreeOfParallelism > 0
            ? command.MaxDegreeOfParallelism
            : Environment.ProcessorCount;

        if (command.MaxTrialsToKeep < 1)
            throw new ArgumentException("MaxTrialsToKeep must be at least 1.");

        // Per-DSS tracking
        var filter = new TrialFilter(command);
        var fitnessConfig = command.FitnessConfig ?? FitnessConfig.Default;
        var fitnessFunc = new CompositeFitnessFunction(fitnessConfig);

        var perDssTopTrials = new BoundedTrialQueue[dssCount];
        var perDssFailedTrials = new FailedTrialCollector[dssCount];
        var perDssFilteredOut = new long[dssCount];
        var perDssFailedCount = new long[dssCount];
        var perDssProcessed = new long[dssCount];
        var perDssStrategyVersion = new string?[dssCount];

        // Per-DSS data cache: each DSS only needs its own subscriptions from the shared cache
        var perDssDataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>[dssCount];

        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
        {
            perDssTopTrials[dssIdx] = new BoundedTrialQueue(command.MaxTrialsToKeep, fitnessFunc);
            perDssFailedTrials[dssIdx] = new FailedTrialCollector(capacity: 100);

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

        try
        {
            var stopwatch = Stopwatch.StartNew();

            // Create bounded channel for work items
            var channel = Channel.CreateBounded<(int DssIndex, ParameterCombination Combo)>(
                new BoundedChannelOptions(maxParallelism * 2)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = true,
                    SingleReader = false,
                });

            // Spawn consumer tasks
            var consumers = new Task[maxParallelism];
            for (var c = 0; c < maxParallelism; c++)
            {
                consumers[c] = Task.Factory.StartNew(() =>
                {
                    var trialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        while (channel.Reader.TryRead(out var item)
                            || WaitForRead(channel.Reader, ct, out item))
                        {
                            ct.ThrowIfCancellationRequested();
                            var (dssIdx, combination) = item;

                            // Inject this DSS's subscriptions into the combination
                            var mutableValues = new Dictionary<string, object>(combination.Values)
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
                                    combinationWithSubs, factory, perDssDataCache[dssIdx],
                                    childRunIds[dssIdx], startedAt,
                                    ref perDssStrategyVersion[dssIdx], trialCts.Token);
                                record = record with { FitnessScore = fitnessFunc.Evaluate(record.Metrics) };

                                if (filter.Passes(record.Metrics))
                                    perDssTopTrials[dssIdx].TryAdd(record);
                                else
                                    Interlocked.Increment(ref perDssFilteredOut[dssIdx]);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref perDssFailedCount[dssIdx]);
                                perDssFailedTrials[dssIdx].RecordTimeout(combination.Values, trialTimeout);
                                logger.LogWarning(
                                    "Group {GroupId} DSS[{DssIndex}]: trial timed out after {Timeout}",
                                    groupId, dssIdx, trialTimeout);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref perDssFailedCount[dssIdx]);
                                perDssFailedTrials[dssIdx].Record(
                                    combination.Values,
                                    ex.GetType().FullName ?? ex.GetType().Name,
                                    ex.Message,
                                    ex.StackTrace ?? string.Empty);
                                logger.LogWarning(ex,
                                    "Group {GroupId} DSS[{DssIndex}]: trial failed", groupId, dssIdx);
                            }

                            var count = Interlocked.Increment(ref perDssProcessed[dssIdx]);
                            if (count % progressInterval == 0)
                                _ = progressCache.SetProgressAsync(
                                    childRunIds[dssIdx], count, estimatedCountPerDss, CancellationToken.None);
                        }
                    }
                    finally
                    {
                        trialCts.Dispose();
                    }
                }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            // Producer: round-robin enqueue across DSS groups
            var producerTask = Task.Factory.StartNew(async () =>
            {
                try
                {
                    // Create per-DSS combination enumerators
                    var enumerators = new IEnumerator<ParameterCombination>[dssCount];
                    var dssActive = new bool[dssCount];
                    var activeDssCount = dssCount;

                    for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
                    {
                        IEnumerable<ParameterCombination> combinations = cartesianGenerator.Enumerate(activeAxes);
                        if (normalizer is not null)
                        {
                            var normEnumerable = new NormalizingEnumerable(combinations, normalizer);
                            combinations = normEnumerable.Enumerate();
                        }
                        enumerators[dssIdx] = combinations.GetEnumerator();
                        dssActive[dssIdx] = true;
                    }

                    try
                    {
                        while (activeDssCount > 0)
                        {
                            ct.ThrowIfCancellationRequested();

                            for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
                            {
                                if (!dssActive[dssIdx]) continue;

                                if (enumerators[dssIdx].MoveNext())
                                {
                                    var combo = enumerators[dssIdx].Current;
                                    await channel.Writer.WriteAsync((dssIdx, combo), ct);
                                }
                                else
                                {
                                    dssActive[dssIdx] = false;
                                    activeDssCount--;
                                }
                            }
                        }
                    }
                    finally
                    {
                        for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
                            enumerators[dssIdx].Dispose();
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

            // Wait for producer and all consumers to finish
            await producerTask;
            await Task.WhenAll(consumers);

            stopwatch.Stop();

            // Save results for each DSS child run
            var childStatuses = new List<string>(dssCount);
            for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
            {
                var childRunId = childRunIds[dssIdx];
                var processed = Interlocked.Read(ref perDssProcessed[dssIdx]);
                var filteredOut = Interlocked.Read(ref perDssFilteredOut[dssIdx]);
                var failedCount = Interlocked.Read(ref perDssFailedCount[dssIdx]);

                // Final progress flush
                await progressCache.SetProgressAsync(childRunId, processed, estimatedCountPerDss, ct);

                var trials = perDssTopTrials[dssIdx].DeduplicateAndDrainSorted();
                var failedTrialDetails = perDssFailedTrials[dssIdx].Drain(childRunId);

                var record = new OptimizationRunRecord
                {
                    Id = childRunId,
                    StrategyName = command.StrategyName,
                    StrategyVersion = perDssStrategyVersion[dssIdx] ?? "0",
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                    TotalCombinations = estimatedCountPerDss,
                    SortBy = Fitness,
                    DataSubscriptions = perDssPrimarySub[dssIdx],
                    BacktestSettings = command.BacktestSettings,
                    MaxParallelism = maxParallelism,
                    Trials = trials,
                    FailedTrialDetails = failedTrialDetails,
                    FilteredTrials = filteredOut,
                    FailedTrials = failedCount,
                    OptimizationMethod = "BruteForce",
                    GroupId = groupId,
                    DssIndex = dssIdx,
                };

                await helper.SaveOptimizationAsync(record);
                childStatuses.Add(OptimizationRunStatus.Completed);

                logger.LogInformation(
                    "Group {GroupId} DSS[{DssIndex}] run {RunId}: {Processed} executed, {Kept} kept, {Filtered} filtered, {Failed} failed in {Duration}ms",
                    groupId, dssIdx, childRunId, processed, trials.Count, filteredOut, failedCount,
                    stopwatch.ElapsedMilliseconds);
            }

            // Update group status
            var groupStatus = GroupStatusCalculator.Compute(childStatuses);
            await repository.UpdateOptimizationGroupStatusAsync(
                groupId, groupStatus, DateTimeOffset.UtcNow, ct);

            logger.LogInformation(
                "Group optimization {GroupId} completed with status {Status}: {DssCount} DSS runs in {Duration}ms",
                groupId, groupStatus, dssCount, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Group optimization {GroupId} was cancelled", groupId);
            await SaveErrorForAllChildRuns(
                command, childRunIds, allDssSubscriptions, perDssPrimarySub,
                groupId, startedAt, estimatedCountPerDss, maxParallelism,
                perDssTopTrials, perDssFailedTrials, perDssFilteredOut, perDssFailedCount,
                OptimizationRunStatus.CancelledMessage);

            await repository.UpdateOptimizationGroupStatusAsync(
                groupId, OptimizationGroupStatus.Cancelled, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Group optimization {GroupId} failed", groupId);
            await SaveErrorForAllChildRuns(
                command, childRunIds, allDssSubscriptions, perDssPrimarySub,
                groupId, startedAt, estimatedCountPerDss, maxParallelism,
                perDssTopTrials, perDssFailedTrials, perDssFilteredOut, perDssFailedCount,
                ex.Message, ex.StackTrace);

            await repository.UpdateOptimizationGroupStatusAsync(
                groupId, OptimizationGroupStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        finally
        {
            // Clean up progress for all child runs
            for (var dssIdx = 0; dssIdx < dssCount; dssIdx++)
                await progressCache.RemoveProgressAsync(childRunIds[dssIdx]);

            await progressCache.RemoveProgressAsync(groupId);
            await progressCache.RemoveRunKeyAsync(groupRunKey);
            cancellationRegistry.Remove(groupId);
        }
    }

    private async Task SaveErrorForAllChildRuns(
        RunGroupOptimizationCommand command,
        Guid[] childRunIds,
        List<List<DataSubscription>> allDssSubscriptions,
        IReadOnlyList<DataSubscriptionDto>[] perDssPrimarySub,
        Guid groupId,
        DateTimeOffset startedAt,
        long estimatedCountPerDss,
        int maxParallelism,
        BoundedTrialQueue[] perDssTopTrials,
        FailedTrialCollector[] perDssFailedTrials,
        long[] perDssFilteredOut,
        long[] perDssFailedCount,
        string errorMessage,
        string? errorStackTrace = null)
    {
        for (var dssIdx = 0; dssIdx < childRunIds.Length; dssIdx++)
        {
            try
            {
                await helper.SaveErrorOptimizationAsync(
                    command.StrategyName, command.BacktestSettings, perDssPrimarySub[dssIdx],
                    Fitness, maxParallelism,
                    childRunIds[dssIdx], startedAt, estimatedCountPerDss,
                    perDssTopTrials[dssIdx], perDssFailedTrials[dssIdx],
                    Interlocked.Read(ref perDssFilteredOut[dssIdx]),
                    Interlocked.Read(ref perDssFailedCount[dssIdx]),
                    errorMessage, errorStackTrace,
                    optimizationMethod: "BruteForce");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to persist error record for group {GroupId} child run {RunId}",
                    groupId, childRunIds[dssIdx]);
            }
        }
    }

    private static bool WaitForRead(
        ChannelReader<(int DssIndex, ParameterCombination Combo)> reader,
        CancellationToken ct,
        out (int DssIndex, ParameterCombination Combo) item)
    {
        // Synchronous wait suitable for LongRunning tasks
        try
        {
            var task = reader.WaitToReadAsync(ct).AsTask();
            task.Wait(ct);
            if (task.Result)
                return reader.TryRead(out item);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation
            throw;
        }
        catch (AggregateException ae) when (ae.InnerException is OperationCanceledException oce)
        {
            throw oce;
        }
        catch
        {
            // Channel completed or other error
        }

        item = default;
        return false;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunOptimizationCommandHandler(
    IOptimizationStrategyFactory strategyFactory,
    OptimizationSetupHelper helper,
    OptimizationAxisResolver axisResolver,
    ICartesianProductGenerator cartesianGenerator,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    IOptions<RunTimeoutOptions> timeoutOptions,
    ILogger<RunOptimizationCommandHandler> logger) : ICommandHandler<RunOptimizationCommand, OptimizationSubmissionDto>
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

        var (fixedSubscriptions, axisSubscriptions, dataCache) =
            await helper.ResolveSubscriptionsAsync(
                command.DataSubscriptions, command.SubscriptionAxis, fromDate, toDate, ct);

        var resolvedAxes = axisResolver.Resolve(descriptor, command.Axes);
        var activeAxes = OptimizationSetupHelper.AppendSubscriptionAxisAndFilter(
            resolvedAxes, axisSubscriptions);

        var estimatedCount = cartesianGenerator.EstimateCount(activeAxes);
        if (estimatedCount > command.MaxCombinations)
            throw new ArgumentException(
                $"Estimated {estimatedCount} combinations exceeds maximum of {command.MaxCombinations}.");

        // 3. Store progress marker in cache
        var startedAt = DateTimeOffset.UtcNow;
        var optimizationRunId = Guid.NewGuid();
        await progressCache.SetProgressAsync(optimizationRunId, 0, estimatedCount, ct);
        await progressCache.SetRunKeyAsync(runKey, optimizationRunId, ct);

        // 4. Start background task on a dedicated thread (coordinates long-running parallel work)
        _ = Task.Factory.StartNew(
            () => RunOptimizationAsync(
                command, fixedSubscriptions, dataCache, activeAxes,
                estimatedCount, optimizationRunId, runKey, startedAt,
                strategyFactory),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return new OptimizationSubmissionDto
        {
            Id = optimizationRunId,
            TotalCombinations = estimatedCount,
        };
        } // end using (runKey lock)
    }

    private async Task RunOptimizationAsync(
        RunOptimizationCommand command,
        List<DataSubscription> fixedSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        List<ResolvedAxis> activeAxes,
        long estimatedCount,
        Guid optimizationRunId,
        string runKey,
        DateTimeOffset startedAt,
        IOptimizationStrategyFactory factory)
    {
        using var cts = new CancellationTokenSource(timeoutOptions.Value.OptimizationTimeout);
        cancellationRegistry.Register(optimizationRunId, cts);
        var ct = cts.Token;

        if (command.MaxTrialsToKeep < 1)
            throw new ArgumentException("MaxTrialsToKeep must be at least 1.");

        var filter = new TrialFilter(command);
        var topTrials = new BoundedTrialQueue(command.MaxTrialsToKeep, command.SortBy);
        var failedTrials = new FailedTrialCollector(capacity: 100);
        long filteredOutCount = 0;
        long failedTrialCount = 0;

        var maxParallelism = command.MaxDegreeOfParallelism > 0
            ? command.MaxDegreeOfParallelism
            : Environment.ProcessorCount;
        var optPrimarySub = OptimizationSetupHelper.GetPrimarySubscriptionDto(
            command.DataSubscriptions, command.SubscriptionAxis);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var combinations = cartesianGenerator.Enumerate(activeAxes);
            string? strategyVersion = null;
            long processedCount = 0;

            var trialTimeout = timeoutOptions.Value.BacktestTimeout;
            var progressInterval = (long)Math.Clamp(estimatedCount / 10_000.0, 100, 10_000);

            // Dedicated LongRunning threads — isolated from the ASP.NET ThreadPool
            var partitions = Partitioner.Create(combinations, EnumerablePartitionerOptions.NoBuffering)
                .GetPartitions(maxParallelism);
            var tasks = new Task[partitions.Count];
            for (var p = 0; p < partitions.Count; p++)
            {
                var partition = partitions[p];
                tasks[p] = Task.Factory.StartNew(() =>
                {
                    using (partition)
                    {
                        // Reuse a single linked CTS per partition via TryReset (P4)
                        var trialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        try
                        {
                            while (partition.MoveNext())
                            {
                                ct.ThrowIfCancellationRequested();
                                var combination = partition.Current;

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
                                        combination, factory, fixedSubscriptions, dataCache,
                                        optimizationRunId, startedAt, ref strategyVersion, trialCts.Token);
                                    if (filter.Passes(record.Metrics))
                                        topTrials.TryAdd(record);
                                    else
                                        Interlocked.Increment(ref filteredOutCount);
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    Interlocked.Increment(ref failedTrialCount);
                                    failedTrials.RecordTimeout(combination.Values, trialTimeout);
                                    logger.LogWarning("Optimization {RunId}: trial timed out after {Timeout}",
                                        optimizationRunId, trialTimeout);
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref failedTrialCount);
                                    failedTrials.Record(
                                        combination.Values,
                                        ex.GetType().FullName ?? ex.GetType().Name,
                                        ex.Message,
                                        ex.StackTrace ?? string.Empty);
                                    logger.LogWarning(ex, "Optimization {RunId}: trial failed", optimizationRunId);
                                }

                                var count = Interlocked.Increment(ref processedCount);
                                if (count % progressInterval == 0)
                                    _ = progressCache.SetProgressAsync(
                                        optimizationRunId, count, estimatedCount, CancellationToken.None);
                            }
                        }
                        finally
                        {
                            trialCts.Dispose();
                        }
                    }
                }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            await Task.WhenAll(tasks);

            stopwatch.Stop();

            // Final progress flush (handles case where total % ProgressUpdateInterval != 0)
            await progressCache.SetProgressAsync(
                optimizationRunId, Interlocked.Read(ref processedCount), estimatedCount, ct);

            var trials = topTrials.DrainSorted();
            var failedTrialDetails = failedTrials.Drain(optimizationRunId);

            var optimizationRecord = new OptimizationRunRecord
            {
                Id = optimizationRunId,
                StrategyName = command.StrategyName,
                StrategyVersion = strategyVersion ?? "0",
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                TotalCombinations = estimatedCount,
                SortBy = command.SortBy,
                DataSubscription = optPrimarySub,
                BacktestSettings = command.BacktestSettings,
                MaxParallelism = maxParallelism,
                Trials = trials,
                FailedTrialDetails = failedTrialDetails,
                FilteredTrials = Interlocked.Read(ref filteredOutCount),
                FailedTrials = Interlocked.Read(ref failedTrialCount),
                OptimizationMethod = "BruteForce",
            };

            await helper.SaveOptimizationAsync(optimizationRecord);

            logger.LogInformation(
                "Optimization {RunId}: {Total} executed, {Kept} kept, {Filtered} filtered, {Failed} failed in {Duration}ms",
                optimizationRunId, processedCount, trials.Count, filteredOutCount, failedTrialCount, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Optimization {RunId} was cancelled", optimizationRunId);
            await helper.SaveErrorOptimizationAsync(
                command.StrategyName, command.BacktestSettings, optPrimarySub,
                command.SortBy, maxParallelism,
                optimizationRunId, startedAt, estimatedCount, topTrials,
                failedTrials, filteredOutCount, failedTrialCount, "Run was cancelled by user.",
                optimizationMethod: "BruteForce");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Optimization {RunId} failed", optimizationRunId);
            await helper.SaveErrorOptimizationAsync(
                command.StrategyName, command.BacktestSettings, optPrimarySub,
                command.SortBy, maxParallelism,
                optimizationRunId, startedAt, estimatedCount, topTrials,
                failedTrials, filteredOutCount, failedTrialCount, ex.Message, ex.StackTrace,
                optimizationMethod: "BruteForce");
        }
        finally
        {
            await progressCache.RemoveProgressAsync(optimizationRunId);
            await progressCache.RemoveRunKeyAsync(runKey);
            cancellationRegistry.Remove(optimizationRunId);
        }
    }
}

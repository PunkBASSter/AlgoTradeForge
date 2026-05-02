using System.Collections.Concurrent;
using System.Diagnostics;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Context captured at enqueue time, stored on ComputeTask.ExecutionContext.
/// Contains everything needed to execute a single-DSS optimization.
/// </summary>
public sealed record OptimizationExecutionContext
{
    public required string StrategyName { get; init; }
    public required string OptimizationMethod { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required IReadOnlyList<DataFeedSubscription> Subscriptions { get; init; }
    public required List<ResolvedAxis> ActiveAxes { get; init; }
    public required long EstimatedCount { get; init; }
    public required int MaxParallelism { get; init; }
    public required int MaxTrialsToKeep { get; init; }
    public required ITrialFilterOptions FilterOptions { get; init; }
    public required FitnessConfig FitnessConfig { get; init; }
    public required IParameterNormalizer? Normalizer { get; init; }
    public required Guid GroupId { get; init; }
    public required string GroupRunKey { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string? InputJson { get; init; }
}

/// <summary>
/// Results from executing a single-DSS optimization.
/// </summary>
public sealed record OptimizationExecutionResult
{
    public required IReadOnlyList<BacktestRunRecord> Trials { get; init; }
    public required IReadOnlyList<FailedTrialRecord> FailedTrialDetails { get; init; }
    public required long FilteredTrials { get; init; }
    public required long FailedTrials { get; init; }
    public required long ProcessedCount { get; init; }
    public required string? StrategyVersion { get; init; }
    public required long DurationMs { get; init; }
}

/// <summary>
/// Executes a single-DSS brute-force optimization. Extracted from RunGroupOptimizationCommandHandler
/// to be called by the ComputeQueueConsumer.
/// </summary>
public sealed class OptimizationTaskExecutor(
    IOptimizationStrategyFactory strategyFactory,
    OptimizationSetupHelper helper,
    ICartesianProductGenerator cartesianGenerator,
    RunProgressCache progressCache,
    IOptions<RunTimeoutOptions> timeoutOptions,
    ILogger<OptimizationTaskExecutor> logger)
{
    public async Task<OptimizationExecutionResult> ExecuteAsync(
        OptimizationExecutionContext ctx,
        Guid childRunId,
        int dssIndex,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 1. Load market data for this DSS
        var settings = ctx.BacktestSettings;
        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        var resolvedSubs = new List<DataSubscription>();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        foreach (var sub in ctx.Subscriptions)
            await helper.ResolveAndCacheAsync(sub, resolvedSubs, dataCache, fromDate, toDate, ct);

        // Phase 4 dual-key carrier (TRD §9.3): keep the polymorphic originals alongside the
        // strategy-side projection so ExecuteTrial can round-trip AltBar FeedIds into the run
        // record and use kind-aware cache lookups.
        var feedSubs = ctx.Subscriptions.ToList();

        // 2. Set up trial infrastructure
        var maxParallelism = ctx.MaxParallelism > 0
            ? Math.Min(ctx.MaxParallelism, Environment.ProcessorCount)
            : Environment.ProcessorCount;

        var fitnessFunc = new CompositeFitnessFunction(ctx.FitnessConfig);
        var filter = new TrialFilter(ctx.FilterOptions);
        var topTrials = new BoundedTrialQueue(ctx.MaxTrialsToKeep, fitnessFunc);
        var failedTrials = new FailedTrialCollector(capacity: 100);

        long filteredOut = 0;
        long failedCount = 0;
        long processedCount = 0;
        string? strategyVersion = null;

        var trialTimeout = timeoutOptions.Value.BacktestTimeout;
        var progressInterval = (long)Math.Clamp(ctx.EstimatedCount / 10_000.0, 100, 10_000);

        // 3. Build combinations and run parallel evaluation
        IEnumerable<ParameterCombination> combinations = cartesianGenerator.Enumerate(ctx.ActiveAxes);
        if (ctx.Normalizer is not null)
        {
            var normEnumerable = new NormalizingEnumerable(combinations, ctx.Normalizer);
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
                                    ["DataSubscriptions"] = resolvedSubs,
                                    ["FeedSubscriptions"] = feedSubs,
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
                                        ctx.StrategyName, ctx.BacktestSettings,
                                        combinationWithSubs, strategyFactory, dataCache,
                                        childRunId, ctx.StartedAt,
                                        ref strategyVersion, trialCts.Token);
                                    var rawFitness = fitnessFunc.Evaluate(record.Metrics);
                                    record = record with { FitnessScore = rawFitness <= double.MinValue ? null : rawFitness };

                                    if (filter.Passes(record.Metrics))
                                        topTrials.TryAdd(record);
                                    else
                                        Interlocked.Increment(ref filteredOut);
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    Interlocked.Increment(ref failedCount);
                                    failedTrials.RecordTimeout(combo.Values, trialTimeout);
                                }
                                catch (OperationCanceledException)
                                {
                                    exitReason = "cancelled";
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref failedCount);
                                    failedTrials.Record(
                                        combo.Values,
                                        ex.GetType().FullName ?? ex.GetType().Name,
                                        ex.Message,
                                        ex.StackTrace ?? string.Empty);
                                }

                                localCount++;
                                var count = Interlocked.Increment(ref processedCount);
                                if (count % progressInterval == 0)
                                    _ = progressCache.SetProgressAsync(
                                        childRunId, count, ctx.EstimatedCount, CancellationToken.None);
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
                    Volatile.Write(ref workerItemCounts[workerId], localCount);
                    logger.LogInformation(
                        "Optimization {RunId} DSS[{DssIndex}] worker {WorkerId}/{Total} exited: {Reason}, processed {Count} items",
                        childRunId, dssIndex, workerId, maxParallelism, exitReason, localCount);
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        // 4. Final progress flush
        var totalProcessed = Interlocked.Read(ref processedCount);
        await progressCache.SetProgressAsync(childRunId, totalProcessed, ctx.EstimatedCount, CancellationToken.None);

        logger.LogInformation(
            "Optimization {RunId} DSS[{DssIndex}]: {WorkerCount} workers completed, {Total} items in {Duration}ms",
            childRunId, dssIndex, maxParallelism, totalProcessed, sw.ElapsedMilliseconds);

        return new OptimizationExecutionResult
        {
            Trials = topTrials.DeduplicateAndDrainSorted(),
            FailedTrialDetails = failedTrials.Drain(childRunId),
            FilteredTrials = Interlocked.Read(ref filteredOut),
            FailedTrials = Interlocked.Read(ref failedCount),
            ProcessedCount = totalProcessed,
            StrategyVersion = strategyVersion,
            DurationMs = sw.ElapsedMilliseconds,
        };
    }
}

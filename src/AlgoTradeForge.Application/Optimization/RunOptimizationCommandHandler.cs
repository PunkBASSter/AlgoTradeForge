using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunOptimizationCommandHandler(
    BacktestEngine engine,
    IOptimizationStrategyFactory strategyFactory,
    IAssetRepository assetRepository,
    IHistoryRepository historyRepository,
    IMetricsCalculator metricsCalculator,
    IOptimizationSpaceProvider spaceProvider,
    OptimizationAxisResolver axisResolver,
    ICartesianProductGenerator cartesianGenerator,
    IRunRepository runRepository,
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

        // 2. Synchronous validation and data loading
        var descriptor = spaceProvider.GetDescriptor(command.StrategyName)
            ?? throw new ArgumentException($"Strategy '{command.StrategyName}' not found.");

        var dataSubs = command.DataSubscriptions;
        var axisSubs = command.SubscriptionAxis;
        var hasDataSubs = dataSubs is { Count: > 0 };
        var hasAxisSubs = axisSubs is { Count: > 0 };

        if (!hasDataSubs && !hasAxisSubs)
            throw new ArgumentException("At least one DataSubscription or SubscriptionAxis entry must be provided.");

        var fromDate = DateOnly.FromDateTime(command.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(command.EndTime.UtcDateTime);

        // Resolve fixed vs axis subscriptions based on which fields are populated
        List<DataSubscriptionDto> fixedDtos;
        List<DataSubscriptionDto> axisDtos;

        if (hasAxisSubs)
        {
            // Explicit axis: dataSubscriptions are fixed, subscriptionAxis are discrete candidates
            fixedDtos = dataSubs ?? [];
            axisDtos = axisSubs!;
        }
        else if (dataSubs!.Count > 1)
        {
            // Backward compat: multiple dataSubscriptions with no axis → treat as discrete axis
            fixedDtos = [];
            axisDtos = dataSubs;
        }
        else
        {
            // Single fixed subscription, no axis
            fixedDtos = dataSubs;
            axisDtos = [];
        }

        // Resolve all subscriptions and pre-load data
        var fixedSubscriptions = new List<DataSubscription>();
        var axisSubscriptions = new List<DataSubscription>();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();

        foreach (var sub in fixedDtos)
            await ResolveAndCacheAsync(sub, fixedSubscriptions, dataCache, fromDate, toDate, ct);
        foreach (var sub in axisDtos)
            await ResolveAndCacheAsync(sub, axisSubscriptions, dataCache, fromDate, toDate, ct);

        var primaryAsset = fixedSubscriptions.Count > 0
            ? fixedSubscriptions[0].Asset
            : axisSubscriptions[0].Asset;
        var resolvedAxes = axisResolver.Resolve(descriptor, command.Axes, new ScaleContext(primaryAsset));

        if (axisSubscriptions.Count > 0)
        {
            var allAxes = new List<ResolvedAxis>(resolvedAxes)
            {
                new ResolvedDiscreteAxis("DataSubscriptions",
                    axisSubscriptions.Cast<object>().ToList())
            };
            resolvedAxes = allAxes;
        }

        var activeAxes = resolvedAxes
            .Where(a => a switch
            {
                ResolvedNumericAxis n => n.Values.Count > 0,
                ResolvedDiscreteAxis d => d.Values.Count > 0,
                ResolvedModuleSlotAxis m => m.Variants.Count > 0,
                _ => true
            })
            .ToList();

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
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var combinations = cartesianGenerator.Enumerate(activeAxes);
            string? strategyVersion = null;
            long processedCount = 0;

            var maxParallelism = command.MaxDegreeOfParallelism > 0
                ? command.MaxDegreeOfParallelism
                : Environment.ProcessorCount;
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
                                    var record = ExecuteTrial(
                                        command, combination, factory, fixedSubscriptions, dataCache,
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
            var optPrimarySub = GetPrimarySubscriptionDto(command);

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
                DataStart = command.StartTime,
                DataEnd = command.EndTime,
                InitialCash = command.InitialCash,
                Commission = command.CommissionPerTrade,
                SlippageTicks = checked((int)command.SlippageTicks),
                MaxParallelism = maxParallelism,
                AssetName = optPrimarySub.Asset,
                Exchange = optPrimarySub.Exchange,
                TimeFrame = optPrimarySub.TimeFrame,
                Trials = trials,
                FailedTrialDetails = failedTrialDetails,
                FilteredTrials = Interlocked.Read(ref filteredOutCount),
                FailedTrials = Interlocked.Read(ref failedTrialCount),
            };

            await runRepository.SaveOptimizationAsync(optimizationRecord);

            logger.LogInformation(
                "Optimization {RunId}: {Total} executed, {Kept} kept, {Filtered} filtered, {Failed} failed in {Duration}ms",
                optimizationRunId, processedCount, trials.Count, filteredOutCount, failedTrialCount, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Optimization {RunId} was cancelled", optimizationRunId);
            await SaveErrorOptimizationAsync(
                command, optimizationRunId, startedAt, estimatedCount, topTrials,
                failedTrials, filteredOutCount, failedTrialCount, "Run was cancelled by user.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Optimization {RunId} failed", optimizationRunId);
            await SaveErrorOptimizationAsync(
                command, optimizationRunId, startedAt, estimatedCount, topTrials,
                failedTrials, filteredOutCount, failedTrialCount, ex.Message, ex.StackTrace);
        }
        finally
        {
            await progressCache.RemoveProgressAsync(optimizationRunId);
            await progressCache.RemoveRunKeyAsync(runKey);
            cancellationRegistry.Remove(optimizationRunId);
        }
    }

    private BacktestRunRecord ExecuteTrial(
        RunOptimizationCommand command,
        ParameterCombination combination,
        IOptimizationStrategyFactory factory,
        List<DataSubscription> fixedSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        Guid optimizationRunId,
        DateTimeOffset startedAt,
        ref string? strategyVersion,
        CancellationToken token)
    {
        var trialWatch = Stopwatch.StartNew();
        var strategy = factory.Create(command.StrategyName, combination);
        Interlocked.CompareExchange(ref strategyVersion, strategy.Version, null);

        strategy.DataSubscriptions.Clear();
        foreach (var fixedSub in fixedSubscriptions)
            strategy.DataSubscriptions.Add(fixedSub);

        if (combination.Values.TryGetValue("DataSubscriptions", out var subObj) &&
            subObj is DataSubscription dataSub)
        {
            strategy.DataSubscriptions.Add(dataSub);
        }

        if (strategy.DataSubscriptions.Count == 0)
            throw new InvalidOperationException("Trial has no data subscriptions — this indicates a bug in subscription resolution.");

        var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
        for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
        {
            var sub = strategy.DataSubscriptions[i];
            var key = $"{sub.Asset.Name}|{sub.TimeFrame}";
            if (dataCache.TryGetValue(key, out var cached))
                seriesArray[i] = cached.Series;
            else
                throw new InvalidOperationException($"No pre-loaded data for subscription {key}.");
        }

        var trialAsset = strategy.DataSubscriptions[0].Asset;
        var scale = new ScaleContext(trialAsset);

        var backOptions = new BacktestOptions
        {
            InitialCash = scale.AmountToTicks(command.InitialCash),
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            CommissionPerTrade = scale.AmountToTicks(command.CommissionPerTrade),
            SlippageTicks = command.SlippageTicks
        };

        var result = engine.Run(seriesArray, strategy, backOptions, token);

        var metrics = metricsCalculator.Calculate(
            result.Fills, new EquityValueProjection(result.EquityCurve), backOptions.InitialCash,
            command.StartTime, command.EndTime);

        var scaledMetrics = MetricsScaler.ScaleDown(metrics, scale);
        trialWatch.Stop();

        var trialPrimarySub = strategy.DataSubscriptions[0];
        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = command.StrategyName,
            StrategyVersion = strategy.Version,
            Parameters = combination.Values,
            AssetName = trialPrimarySub.Asset.Name,
            Exchange = trialPrimarySub.Asset.Exchange,
            TimeFrame = TimeFrameFormatter.Format(trialPrimarySub.TimeFrame),
            InitialCash = command.InitialCash,
            Commission = command.CommissionPerTrade,
            SlippageTicks = checked((int)command.SlippageTicks),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DataStart = command.StartTime,
            DataEnd = command.EndTime,
            DurationMs = (long)trialWatch.Elapsed.TotalMilliseconds,
            TotalBars = result.TotalBarsProcessed,
            Metrics = scaledMetrics,
            EquityCurve = [],
            RunFolderPath = null,
            RunMode = RunModes.Backtest,
            OptimizationRunId = optimizationRunId,
        };
    }

    private async Task SaveErrorOptimizationAsync(
        RunOptimizationCommand command, Guid optimizationRunId,
        DateTimeOffset startedAt, long estimatedCount,
        BoundedTrialQueue topTrials, FailedTrialCollector failedTrials,
        long filteredOutCount, long failedTrialCount,
        string errorMessage, string? errorStackTrace = null)
    {
        try
        {
            var optPrimarySub = GetPrimarySubscriptionDto(command);
            var maxParallelism = command.MaxDegreeOfParallelism > 0
                ? command.MaxDegreeOfParallelism
                : Environment.ProcessorCount;

            var completedAt = DateTimeOffset.UtcNow;
            var record = new OptimizationRunRecord
            {
                Id = optimizationRunId,
                StrategyName = command.StrategyName,
                StrategyVersion = "0",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
                TotalCombinations = estimatedCount,
                SortBy = command.SortBy,
                DataStart = command.StartTime,
                DataEnd = command.EndTime,
                InitialCash = command.InitialCash,
                Commission = command.CommissionPerTrade,
                SlippageTicks = checked((int)command.SlippageTicks),
                MaxParallelism = maxParallelism,
                AssetName = optPrimarySub.Asset,
                Exchange = optPrimarySub.Exchange,
                TimeFrame = optPrimarySub.TimeFrame,
                Trials = topTrials.DrainSorted(),
                FailedTrialDetails = failedTrials.Drain(optimizationRunId),
                FilteredTrials = filteredOutCount,
                FailedTrials = failedTrialCount,
                ErrorMessage = errorMessage,
                ErrorStackTrace = errorStackTrace,
            };
            await runRepository.SaveOptimizationAsync(record);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save error record for optimization {RunId}", optimizationRunId);
        }
    }

    private static DataSubscriptionDto GetPrimarySubscriptionDto(RunOptimizationCommand command)
    {
        if (command.DataSubscriptions is { Count: > 0 })
            return command.DataSubscriptions[0];
        return command.SubscriptionAxis![0];
    }

    private async Task ResolveAndCacheAsync(
        DataSubscriptionDto sub,
        List<DataSubscription> target,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        DateOnly fromDate, DateOnly toDate,
        CancellationToken ct)
    {
        var asset = await assetRepository.GetByNameAsync(sub.Asset, sub.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{sub.Asset}' on exchange '{sub.Exchange}' not found.");

        if (!TimeSpan.TryParse(sub.TimeFrame, CultureInfo.InvariantCulture, out var timeFrame))
            throw new ArgumentException($"Invalid TimeFrame '{sub.TimeFrame}' for asset '{sub.Asset}'.");

        var subscription = new DataSubscription(asset, timeFrame);
        target.Add(subscription);

        var key = $"{sub.Asset}|{sub.TimeFrame}";
        if (!dataCache.ContainsKey(key))
        {
            var series = historyRepository.Load(subscription, fromDate, toDate);
            dataCache[key] = (asset, series);
        }
    }

    /// <summary>Zero-allocation projection: exposes <c>EquitySnapshot.Value</c> as <c>IReadOnlyList&lt;long&gt;</c>.</summary>
    private sealed class EquityValueProjection(IReadOnlyList<EquitySnapshot> source) : IReadOnlyList<long>
    {
        public long this[int index] => source[index].Value;
        public int Count => source.Count;
        public IEnumerator<long> GetEnumerator()
        {
            for (var i = 0; i < source.Count; i++)
                yield return source[i].Value;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

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
    private const int ProgressUpdateInterval = 100;

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

        var resolvedAxes = axisResolver.Resolve(descriptor, command.Axes);

        var dataSubs = command.DataSubscriptions;
        if (dataSubs is null or { Count: 0 })
            throw new ArgumentException("At least one DataSubscription must be provided.");

        var fromDate = DateOnly.FromDateTime(command.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(command.EndTime.UtcDateTime);

        var resolvedSubscriptions = new List<DataSubscription>();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        foreach (var sub in dataSubs)
        {
            var asset = await assetRepository.GetByNameAsync(sub.Asset, sub.Exchange, ct)
                ?? throw new ArgumentException($"Asset '{sub.Asset}' on exchange '{sub.Exchange}' not found.");

            if (!TimeSpan.TryParse(sub.TimeFrame, CultureInfo.InvariantCulture, out var timeFrame))
                throw new ArgumentException($"Invalid TimeFrame '{sub.TimeFrame}' for asset '{sub.Asset}'.");

            var subscription = new DataSubscription(asset, timeFrame);
            resolvedSubscriptions.Add(subscription);

            var series = historyRepository.Load(subscription, fromDate, toDate);
            var key = $"{sub.Asset}|{sub.TimeFrame}";
            dataCache[key] = (asset, series);
        }

        if (resolvedSubscriptions.Count > 1)
        {
            var allAxes = new List<ResolvedAxis>(resolvedAxes)
            {
                new ResolvedDiscreteAxis("DataSubscriptions",
                    resolvedSubscriptions.Cast<object>().ToList())
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

        var cts = new CancellationTokenSource(timeoutOptions.Value.OptimizationTimeout);
        cancellationRegistry.Register(optimizationRunId, cts);

        // 4. Start background task on a dedicated thread (coordinates long-running parallel work)
        _ = Task.Factory.StartNew(
            () => RunOptimizationAsync(
                command, resolvedSubscriptions, dataCache, activeAxes,
                estimatedCount, optimizationRunId, runKey, startedAt,
                strategyFactory, cts.Token),
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
        List<DataSubscription> resolvedSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        List<ResolvedAxis> activeAxes,
        long estimatedCount,
        Guid optimizationRunId,
        string runKey,
        DateTimeOffset startedAt,
        IOptimizationStrategyFactory factory,
        CancellationToken ct)
    {
        var results = new ConcurrentBag<(OptimizationTrialResultDto Dto, BacktestRunRecord Record)>();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var combinations = cartesianGenerator.Enumerate(activeAxes);
            string? strategyVersion = null;
            long processedCount = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = command.MaxDegreeOfParallelism > 0
                    ? command.MaxDegreeOfParallelism
                    : Environment.ProcessorCount,
                CancellationToken = ct
            };

            var trialTimeout = timeoutOptions.Value.BacktestTimeout;

            await Parallel.ForEachAsync(combinations, parallelOptions, async (combination, token) =>
            {
                try
                {
                    using var trialCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    trialCts.CancelAfter(trialTimeout);

                    var trial = ExecuteTrial(
                        command, combination, factory, resolvedSubscriptions, dataCache,
                        optimizationRunId, startedAt, ref strategyVersion, trialCts.Token);
                    results.Add(trial);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Per-trial timeout — treat as a failed trial, not a full cancellation
                    results.Add(CreateFailedTrial(
                        command, combination, resolvedSubscriptions, optimizationRunId,
                        startedAt, strategyVersion,
                        new TimeoutException($"Trial exceeded timeout of {trialTimeout}.")));
                    logger.LogWarning("Optimization {RunId}: trial timed out after {Timeout}", optimizationRunId, trialTimeout);
                }
                catch (OperationCanceledException)
                {
                    throw; // Overall cancellation — re-throw to let Parallel.ForEachAsync handle it
                }
                catch (Exception ex)
                {
                    results.Add(CreateFailedTrial(
                        command, combination, resolvedSubscriptions, optimizationRunId,
                        startedAt, strategyVersion, ex));
                    logger.LogWarning(ex, "Optimization {RunId}: trial failed", optimizationRunId);
                }

                var count = Interlocked.Increment(ref processedCount);
                if (count % ProgressUpdateInterval == 0)
                    await progressCache.SetProgressAsync(optimizationRunId, count, estimatedCount);
            });

            stopwatch.Stop();

            // Final progress flush (handles case where total % ProgressUpdateInterval != 0)
            await progressCache.SetProgressAsync(
                optimizationRunId, Interlocked.Read(ref processedCount), estimatedCount);

            var sortedResults = SortTrials(results, command.SortBy);
            var optPrimarySub = command.DataSubscriptions![0];
            var maxParallelism = command.MaxDegreeOfParallelism > 0
                ? command.MaxDegreeOfParallelism
                : Environment.ProcessorCount;

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
                Trials = sortedResults.Select(r => r.Record).ToList(),
            };

            await runRepository.SaveOptimizationAsync(optimizationRecord);

            logger.LogInformation("Optimization {RunId} completed in {Duration}ms with {Trials} trials",
                optimizationRunId, stopwatch.ElapsedMilliseconds, results.Count);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Optimization {RunId} was cancelled", optimizationRunId);
            await SaveErrorOptimizationAsync(
                command, optimizationRunId, startedAt, estimatedCount, results,
                "Run was cancelled by user.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Optimization {RunId} failed", optimizationRunId);
            await SaveErrorOptimizationAsync(
                command, optimizationRunId, startedAt, estimatedCount, results,
                ex.Message, ex.StackTrace);
        }
        finally
        {
            await progressCache.RemoveProgressAsync(optimizationRunId);
            await progressCache.RemoveRunKeyAsync(runKey);
            cancellationRegistry.Remove(optimizationRunId);
        }
    }

    private static IReadOnlyList<(OptimizationTrialResultDto Dto, BacktestRunRecord Record)> SortTrials(
        IEnumerable<(OptimizationTrialResultDto Dto, BacktestRunRecord Record)> trials, string sortBy)
    {
        return sortBy switch
        {
            SharpeRatio => trials.OrderByDescending(t => t.Dto.Metrics.SharpeRatio).ToList(),
            NetProfit => trials.OrderByDescending(t => t.Dto.Metrics.NetProfit).ToList(),
            SortinoRatio => trials.OrderByDescending(t => t.Dto.Metrics.SortinoRatio).ToList(),
            ProfitFactor => trials.OrderByDescending(t => t.Dto.Metrics.ProfitFactor).ToList(),
            WinRatePct => trials.OrderByDescending(t => t.Dto.Metrics.WinRatePct).ToList(),
            MaxDrawdownPct => trials.OrderBy(t => t.Dto.Metrics.MaxDrawdownPct).ToList(),
            _ => trials.OrderByDescending(t => t.Dto.Metrics.SharpeRatio).ToList()
        };
    }

    private (OptimizationTrialResultDto Dto, BacktestRunRecord Record) ExecuteTrial(
        RunOptimizationCommand command,
        ParameterCombination combination,
        IOptimizationStrategyFactory factory,
        List<DataSubscription> resolvedSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        Guid optimizationRunId,
        DateTimeOffset startedAt,
        ref string? strategyVersion,
        CancellationToken token)
    {
        var trialWatch = Stopwatch.StartNew();
        var strategy = factory.Create(command.StrategyName, combination);
        Interlocked.CompareExchange(ref strategyVersion, strategy.Version, null);

        if (combination.Values.TryGetValue("DataSubscriptions", out var subObj) &&
            subObj is DataSubscription dataSub)
        {
            strategy.DataSubscriptions.Clear();
            strategy.DataSubscriptions.Add(dataSub);
        }
        else
        {
            strategy.DataSubscriptions.Clear();
            strategy.DataSubscriptions.Add(resolvedSubscriptions[0]);
        }

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
        var trialScaleFactor = 1m / trialAsset.TickSize;

        var backOptions = new BacktestOptions
        {
            InitialCash = (long)(command.InitialCash * trialScaleFactor),
            Asset = trialAsset,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            CommissionPerTrade = (long)(command.CommissionPerTrade * trialScaleFactor),
            SlippageTicks = command.SlippageTicks
        };

        var result = engine.Run(seriesArray, strategy, backOptions, token);

        var equityValues = result.EquityCurve.Select(e => e.Value).ToList();
        var metrics = metricsCalculator.Calculate(
            result.Fills, equityValues, backOptions.InitialCash,
            command.StartTime, command.EndTime);

        var scaledMetrics = MetricsScaler.ScaleDown(metrics, trialScaleFactor);
        trialWatch.Stop();

        var dto = new OptimizationTrialResultDto
        {
            Parameters = combination.Values,
            Metrics = scaledMetrics,
            Duration = trialWatch.Elapsed
        };

        var equityCurve = MetricsScaler.ScaleEquityCurve(result.EquityCurve, trialScaleFactor);
        var trialPrimarySub = strategy.DataSubscriptions[0];
        var trialRecord = new BacktestRunRecord
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
            EquityCurve = equityCurve,
            RunFolderPath = null,
            RunMode = RunModes.Backtest,
            OptimizationRunId = optimizationRunId,
        };

        return (dto, trialRecord);
    }

    private static (OptimizationTrialResultDto Dto, BacktestRunRecord Record) CreateFailedTrial(
        RunOptimizationCommand command,
        ParameterCombination combination,
        List<DataSubscription> resolvedSubscriptions,
        Guid optimizationRunId,
        DateTimeOffset startedAt,
        string? strategyVersion,
        Exception ex)
    {
        var zeroMetrics = new PerformanceMetrics
        {
            TotalTrades = 0, WinningTrades = 0, LosingTrades = 0,
            NetProfit = 0, GrossProfit = 0, GrossLoss = 0, TotalCommissions = 0,
            TotalReturnPct = 0, AnnualizedReturnPct = 0,
            SharpeRatio = 0, SortinoRatio = 0, MaxDrawdownPct = 0,
            WinRatePct = 0, ProfitFactor = 0, AverageWin = 0, AverageLoss = 0,
            InitialCapital = command.InitialCash, FinalEquity = command.InitialCash,
            TradingDays = 0,
        };

        var dto = new OptimizationTrialResultDto
        {
            Parameters = combination.Values,
            Metrics = zeroMetrics,
            Duration = TimeSpan.Zero,
            ErrorMessage = ex.Message,
            ErrorStackTrace = ex.StackTrace
        };

        var record = new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = command.StrategyName,
            StrategyVersion = strategyVersion ?? "0",
            Parameters = combination.Values,
            AssetName = resolvedSubscriptions[0].Asset.Name,
            Exchange = resolvedSubscriptions[0].Asset.Exchange,
            TimeFrame = TimeFrameFormatter.Format(resolvedSubscriptions[0].TimeFrame),
            InitialCash = command.InitialCash,
            Commission = command.CommissionPerTrade,
            SlippageTicks = checked((int)command.SlippageTicks),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DataStart = command.StartTime,
            DataEnd = command.EndTime,
            DurationMs = 0,
            TotalBars = 0,
            Metrics = zeroMetrics,
            EquityCurve = [],
            RunFolderPath = null,
            RunMode = RunModes.Backtest,
            OptimizationRunId = optimizationRunId,
            ErrorMessage = ex.Message,
            ErrorStackTrace = ex.StackTrace,
        };

        return (dto, record);
    }

    private async Task SaveErrorOptimizationAsync(
        RunOptimizationCommand command, Guid optimizationRunId,
        DateTimeOffset startedAt, long estimatedCount,
        ConcurrentBag<(OptimizationTrialResultDto Dto, BacktestRunRecord Record)> results,
        string errorMessage, string? errorStackTrace = null)
    {
        try
        {
            var optPrimarySub = command.DataSubscriptions![0];
            var maxParallelism = command.MaxDegreeOfParallelism > 0
                ? command.MaxDegreeOfParallelism
                : Environment.ProcessorCount;

            var record = new OptimizationRunRecord
            {
                Id = optimizationRunId,
                StrategyName = command.StrategyName,
                StrategyVersion = "0",
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
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
                Trials = results.Select(r => r.Record).ToList(),
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
}

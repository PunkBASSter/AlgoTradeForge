using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
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
    ILogger<RunOptimizationCommandHandler> logger) : ICommandHandler<RunOptimizationCommand, OptimizationSubmissionDto>
{
    private const int ProgressUpdateInterval = 100;

    public async Task<OptimizationSubmissionDto> HandleAsync(RunOptimizationCommand command, CancellationToken ct = default)
    {
        // 1. Compute RunKey and check for dedup
        var runKey = RunKeyBuilder.Build(command);
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

        var cts = new CancellationTokenSource();
        cancellationRegistry.Register(optimizationRunId, cts);

        // 4. Start background task
        _ = Task.Run(() => RunOptimizationAsync(
            command, resolvedSubscriptions, dataCache, activeAxes,
            estimatedCount, optimizationRunId, runKey, startedAt,
            strategyFactory, cts.Token));

        return new OptimizationSubmissionDto
        {
            Id = optimizationRunId,
            TotalCombinations = estimatedCount,
        };
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
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var results = new ConcurrentBag<(OptimizationTrialResultDto Dto, BacktestRunRecord Record)>();
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

            await Parallel.ForEachAsync(combinations, parallelOptions, async (combination, token) =>
            {
                try
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

                    var scaledMetrics = metrics with
                    {
                        InitialCapital = metrics.InitialCapital / trialScaleFactor,
                        FinalEquity = metrics.FinalEquity / trialScaleFactor,
                        NetProfit = metrics.NetProfit / trialScaleFactor,
                        GrossProfit = metrics.GrossProfit / trialScaleFactor,
                        GrossLoss = metrics.GrossLoss / trialScaleFactor,
                        TotalCommissions = metrics.TotalCommissions / trialScaleFactor,
                        AverageWin = metrics.AverageWin / (double)trialScaleFactor,
                        AverageLoss = metrics.AverageLoss / (double)trialScaleFactor,
                    };

                    trialWatch.Stop();

                    var dto = new OptimizationTrialResultDto
                    {
                        Parameters = combination.Values,
                        Metrics = scaledMetrics,
                        Duration = trialWatch.Elapsed
                    };

                    var equityCurve = new EquityPoint[result.EquityCurve.Count];
                    for (var i = 0; i < result.EquityCurve.Count; i++)
                        equityCurve[i] = new EquityPoint(result.EquityCurve[i].TimestampMs, result.EquityCurve[i].Value / trialScaleFactor);

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
                        RunMode = "Backtest",
                        OptimizationRunId = optimizationRunId,
                    };

                    results.Add((dto, trialRecord));

                    var count = Interlocked.Increment(ref processedCount);
                    if (count % ProgressUpdateInterval == 0)
                        await progressCache.SetProgressAsync(optimizationRunId, count, estimatedCount);
                }
                catch (OperationCanceledException)
                {
                    throw; // Re-throw to let Parallel.ForEachAsync handle cancellation
                }
                catch (Exception ex)
                {
                    // Per-trial error handling: save failed trial with error info
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

                    var failedDto = new OptimizationTrialResultDto
                    {
                        Parameters = combination.Values,
                        Metrics = zeroMetrics,
                        Duration = TimeSpan.Zero,
                        ErrorMessage = ex.Message,
                        ErrorStackTrace = ex.StackTrace
                    };

                    var failedRecord = new BacktestRunRecord
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
                        RunMode = "Backtest",
                        OptimizationRunId = optimizationRunId,
                        ErrorMessage = ex.Message,
                        ErrorStackTrace = ex.StackTrace,
                    };

                    results.Add((failedDto, failedRecord));

                    var count = Interlocked.Increment(ref processedCount);
                    if (count % ProgressUpdateInterval == 0)
                        await progressCache.SetProgressAsync(optimizationRunId, count, estimatedCount);

                    logger.LogWarning(ex, "Optimization {RunId}: trial failed", optimizationRunId);
                }
            });

            stopwatch.Stop();

            // Final progress flush (handles case where total % ProgressUpdateInterval != 0)
            await progressCache.SetProgressAsync(
                optimizationRunId, Interlocked.Read(ref processedCount), estimatedCount);

            var sortedTrials = SortTrials(results.Select(r => r.Dto), command.SortBy);
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
                Trials = results.Select(r => r.Record).ToList(),
            };

            await runRepository.SaveOptimizationAsync(optimizationRecord);

            logger.LogInformation("Optimization {RunId} completed in {Duration}ms with {Trials} trials",
                optimizationRunId, stopwatch.ElapsedMilliseconds, results.Count);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Optimization {RunId} was cancelled", optimizationRunId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Optimization {RunId} failed", optimizationRunId);
        }
        finally
        {
            await progressCache.RemoveProgressAsync(optimizationRunId);
            await progressCache.RemoveRunKeyAsync(runKey);
            cancellationRegistry.Remove(optimizationRunId);
        }
    }

    private static IReadOnlyList<OptimizationTrialResultDto> SortTrials(
        IEnumerable<OptimizationTrialResultDto> trials, string sortBy)
    {
        return sortBy switch
        {
            SharpeRatio => trials.OrderByDescending(t => t.Metrics.SharpeRatio).ToList(),
            NetProfit => trials.OrderByDescending(t => t.Metrics.NetProfit).ToList(),
            SortinoRatio => trials.OrderByDescending(t => t.Metrics.SortinoRatio).ToList(),
            ProfitFactor => trials.OrderByDescending(t => t.Metrics.ProfitFactor).ToList(),
            WinRatePct => trials.OrderByDescending(t => t.Metrics.WinRatePct).ToList(),
            MaxDrawdownPct => trials.OrderBy(t => t.Metrics.MaxDrawdownPct).ToList(),
            _ => trials.OrderByDescending(t => t.Metrics.SharpeRatio).ToList()
        };
    }
}

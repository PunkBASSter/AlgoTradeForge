using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Optimization;

public sealed class RunOptimizationCommandHandler(
    BacktestEngine engine,
    IOptimizationStrategyFactory strategyFactory,
    IAssetRepository assetRepository,
    IHistoryRepository historyRepository,
    IMetricsCalculator metricsCalculator,
    IOptimizationSpaceProvider spaceProvider,
    OptimizationAxisResolver axisResolver,
    ICartesianProductGenerator cartesianGenerator) : ICommandHandler<RunOptimizationCommand, OptimizationResultDto>
{
    public async Task<OptimizationResultDto> HandleAsync(RunOptimizationCommand command, CancellationToken ct = default)
    {
        var descriptor = spaceProvider.GetDescriptor(command.StrategyName)
            ?? throw new ArgumentException($"Strategy '{command.StrategyName}' not found.");

        // Resolve axes from overrides
        var resolvedAxes = axisResolver.Resolve(descriptor, command.Axes);

        // Validate and resolve data subscriptions in a single pass
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
                throw new ArgumentException(
                    $"Invalid TimeFrame '{sub.TimeFrame}' for asset '{sub.Asset}'.");

            var subscription = new DataSubscription(asset, timeFrame);
            resolvedSubscriptions.Add(subscription);

            var series = historyRepository.Load(subscription, fromDate, toDate);
            var key = $"{sub.Asset}|{sub.TimeFrame}";
            dataCache[key] = (asset, series);
        }

        // Add DataSubscriptions as discrete axis if multiple
        if (resolvedSubscriptions.Count > 1)
        {
            var allAxes = new List<ResolvedAxis>(resolvedAxes)
            {
                new ResolvedDiscreteAxis("DataSubscriptions",
                    resolvedSubscriptions.Cast<object>().ToList())
            };
            resolvedAxes = allAxes;
        }

        // Filter out empty axes (omitted params use defaults)
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

        var stopwatch = Stopwatch.StartNew();
        var results = new ConcurrentBag<OptimizationTrialResultDto>();
        var combinations = cartesianGenerator.Enumerate(activeAxes);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = command.MaxDegreeOfParallelism > 0
                ? command.MaxDegreeOfParallelism
                : Environment.ProcessorCount,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(combinations, parallelOptions, (combination, token) =>
        {
            var trialWatch = Stopwatch.StartNew();

            var strategy = strategyFactory.Create(command.StrategyName, combination);

            // Determine which data subscriptions to use for this trial
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

            // Build series array matching strategy subscriptions
            var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
            for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
            {
                var sub = strategy.DataSubscriptions[i];
                var key = $"{sub.Asset.Name}|{sub.TimeFrame}";
                if (dataCache.TryGetValue(key, out var cached))
                    seriesArray[i] = cached.Series;
                else
                    throw new InvalidOperationException(
                        $"No pre-loaded data for subscription {key}.");
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
            var metrics = metricsCalculator.Calculate(
                result.Fills, result.EquityCurve, backOptions.InitialCash,
                command.StartTime, command.EndTime);

            // Scale absolute dollar values back to real units
            var scaledMetrics = metrics with
            {
                InitialCapital = metrics.InitialCapital / trialScaleFactor,
                FinalEquity = metrics.FinalEquity / trialScaleFactor,
                NetProfit = metrics.NetProfit / trialScaleFactor,
                GrossProfit = metrics.GrossProfit / trialScaleFactor,
                GrossLoss = metrics.GrossLoss / trialScaleFactor,
                AverageWin = metrics.AverageWin / (double)trialScaleFactor,
                AverageLoss = metrics.AverageLoss / (double)trialScaleFactor,
            };

            trialWatch.Stop();
            results.Add(new OptimizationTrialResultDto
            {
                Parameters = combination.Values,
                Metrics = scaledMetrics,
                Duration = trialWatch.Elapsed
            });

            return ValueTask.CompletedTask;
        });

        stopwatch.Stop();

        var sortedTrials = SortTrials(results, command.SortBy);

        return new OptimizationResultDto
        {
            StrategyName = command.StrategyName,
            TotalCombinations = estimatedCount,
            TotalDuration = stopwatch.Elapsed,
            Trials = sortedTrials
        };
    }

    private static IReadOnlyList<OptimizationTrialResultDto> SortTrials(
        IEnumerable<OptimizationTrialResultDto> trials, string sortBy)
    {
        return sortBy switch
        {
            "SharpeRatio" => trials.OrderByDescending(t => t.Metrics.SharpeRatio).ToList(),
            "NetProfit" => trials.OrderByDescending(t => t.Metrics.NetProfit).ToList(),
            "SortinoRatio" => trials.OrderByDescending(t => t.Metrics.SortinoRatio).ToList(),
            "ProfitFactor" => trials.OrderByDescending(t => t.Metrics.ProfitFactor).ToList(),
            "WinRatePct" => trials.OrderByDescending(t => t.Metrics.WinRatePct).ToList(),
            "MaxDrawdownPct" => trials.OrderBy(t => t.Metrics.MaxDrawdownPct).ToList(),
            _ => trials.OrderByDescending(t => t.Metrics.SharpeRatio).ToList()
        };
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
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

        // Add DataSubscriptions as discrete axis if provided
        if (command.DataSubscriptions is { Count: > 0 })
        {
            var subscriptions = new List<object>();
            foreach (var sub in command.DataSubscriptions)
            {
                var asset = await assetRepository.GetByNameAsync(sub.Asset, ct)
                    ?? throw new ArgumentException($"Asset '{sub.Asset}' not found.");
                var timeFrame = TimeSpan.Parse(sub.TimeFrame);
                subscriptions.Add(new DataSubscription(asset, timeFrame));
            }

            var allAxes = new List<ResolvedAxis>(resolvedAxes)
            {
                new ResolvedDiscreteAxis("DataSubscriptions", subscriptions)
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

        // Load historical data once
        var dataSubs = command.DataSubscriptions;
        if (dataSubs is null or { Count: 0 })
            throw new ArgumentException("At least one DataSubscription must be provided.");

        var fromDate = DateOnly.FromDateTime(command.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(command.EndTime.UtcDateTime);

        // Pre-load all possible subscription data
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        foreach (var sub in dataSubs)
        {
            var asset = await assetRepository.GetByNameAsync(sub.Asset, ct)
                ?? throw new ArgumentException($"Asset '{sub.Asset}' not found.");
            var timeFrame = TimeSpan.Parse(sub.TimeFrame);
            var subscription = new DataSubscription(asset, timeFrame);
            var series = historyRepository.Load(subscription, fromDate, toDate);
            var key = $"{sub.Asset}|{sub.TimeFrame}";
            dataCache[key] = (asset, series);
        }

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
            else if (dataSubs.Count > 0)
            {
                var first = dataSubs[0];
                var asset = dataCache[$"{first.Asset}|{first.TimeFrame}"].Asset;
                var timeFrame = TimeSpan.Parse(first.TimeFrame);
                strategy.DataSubscriptions.Clear();
                strategy.DataSubscriptions.Add(new DataSubscription(asset, timeFrame));
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

            var backOptions = new BacktestOptions
            {
                InitialCash = command.InitialCash,
                Asset = strategy.DataSubscriptions[0].Asset,
                StartTime = command.StartTime,
                EndTime = command.EndTime,
                CommissionPerTrade = command.CommissionPerTrade,
                SlippageTicks = command.SlippageTicks
            };

            var result = engine.Run(seriesArray, strategy, backOptions, token);
            var metrics = metricsCalculator.Calculate(result.Fills, result.EquityCurve, command.InitialCash);

            trialWatch.Stop();
            results.Add(new OptimizationTrialResultDto
            {
                Parameters = combination.Values,
                Metrics = metrics,
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

using System.Diagnostics;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Shared infrastructure for both brute-force and genetic optimization handlers:
/// data resolution, trial execution, error persistence, and progress helpers.
/// </summary>
public sealed class OptimizationSetupHelper(
    BacktestEngine engine,
    IAssetRepository assetRepository,
    IHistoryRepository historyRepository,
    IMetricsCalculator metricsCalculator,
    IOptimizationSpaceProvider spaceProvider,
    IRunRepository runRepository,
    ILogger<OptimizationSetupHelper> logger)
{
    public IOptimizationSpaceProvider SpaceProvider => spaceProvider;

    /// <summary>
    /// Resolves subscription axis groups into domain objects and pre-loads all market data.
    /// Each group is a list of subscriptions that will be used together in a single trial.
    /// </summary>
    public async Task<(List<List<DataSubscription>> AxisSubscriptionGroups,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> DataCache)>
        ResolveSubscriptionsAsync(
            List<List<DataSubscriptionDto>>? axisGroups,
            DateOnly fromDate, DateOnly toDate,
            CancellationToken ct)
    {
        if (axisGroups is not { Count: > 0 })
            throw new ArgumentException("At least one SubscriptionAxis group must be provided.");

        var axisSubscriptionGroups = new List<List<DataSubscription>>();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();

        foreach (var dtoGroup in axisGroups)
        {
            var resolvedGroup = new List<DataSubscription>();
            foreach (var sub in dtoGroup)
                await ResolveAndCacheAsync(sub, resolvedGroup, dataCache, fromDate, toDate, ct);
            axisSubscriptionGroups.Add(resolvedGroup);
        }

        return (axisSubscriptionGroups, dataCache);
    }

    /// <summary>
    /// Validates that every subscription group has exactly <paramref name="requiredSubscriptionCount"/> items.
    /// </summary>
    public static void ValidateSubscriptionCounts(
        string strategyName,
        int requiredSubscriptionCount,
        List<List<DataSubscription>> axisSubscriptionGroups)
    {
        for (var i = 0; i < axisSubscriptionGroups.Count; i++)
        {
            if (axisSubscriptionGroups[i].Count != requiredSubscriptionCount)
                throw new ArgumentException(
                    $"Strategy '{strategyName}' requires exactly {requiredSubscriptionCount} " +
                    $"subscription(s) per group, but group {i + 1} has {axisSubscriptionGroups[i].Count}.");
        }
    }

    /// <summary>
    /// Extracts RequiredSubscriptionCount from a strategy params type.
    /// </summary>
    public static int GetRequiredSubscriptionCount(Type paramsType)
    {
        if (Activator.CreateInstance(paramsType) is StrategyParamsBase instance)
            return instance.RequiredSubscriptionCount;
        return 1;
    }

    /// <summary>
    /// Appends axis subscription groups as a discrete axis and filters out empty axes.
    /// Each group is a <c>List&lt;DataSubscription&gt;</c> that becomes one axis value.
    /// </summary>
    public static List<ResolvedAxis> AppendSubscriptionAxisAndFilter(
        IReadOnlyList<ResolvedAxis> resolvedAxes,
        List<List<DataSubscription>> axisSubscriptionGroups)
    {
        return AppendSubscriptionAxisAndFilter(resolvedAxes, axisSubscriptionGroups.Count,
            axisSubscriptionGroups.Cast<object>().ToList());
    }

    /// <summary>
    /// Count-only overload: appends a placeholder subscription axis with the given group count
    /// and filters out empty axes. Used by the evaluate path where actual subscriptions
    /// are not loaded.
    /// </summary>
    public static List<ResolvedAxis> AppendSubscriptionAxisAndFilter(
        IReadOnlyList<ResolvedAxis> resolvedAxes,
        int groupCount,
        List<object>? axisValues = null)
    {
        var allAxes = new List<ResolvedAxis>(resolvedAxes);

        if (groupCount > 0)
        {
            var values = axisValues
                ?? Enumerable.Range(0, groupCount).Select(i => (object)i).ToList();
            allAxes.Add(new ResolvedDiscreteAxis("DataSubscriptions", values));
        }

        return allAxes
            .Where(a => a switch
            {
                ResolvedNumericAxis n => n.Values.Count > 0,
                ResolvedDiscreteAxis d => d.Values.Count > 0,
                ResolvedModuleSlotAxis m => m.Variants.Count > 0,
                _ => true
            })
            .ToList();
    }

    public static List<ResolvedAxis> FilterEmptyAxes(IReadOnlyList<ResolvedAxis> resolvedAxes) =>
        resolvedAxes
            .Where(a => a switch
            {
                ResolvedNumericAxis n => n.Values.Count > 0,
                ResolvedDiscreteAxis d => d.Values.Count > 0,
                ResolvedModuleSlotAxis m => m.Variants.Count > 0,
                _ => true
            })
            .ToList();

    public async Task ResolveAndCacheAsync(
        DataSubscriptionDto sub,
        List<DataSubscription> target,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        DateOnly fromDate, DateOnly toDate,
        CancellationToken ct)
    {
        var asset = await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{sub.AssetName}' on exchange '{sub.Exchange}' not found.");

        if (!TimeFrame.TryParseLiberal(sub.TimeFrame, out var timeFrame))
            throw new ArgumentException($"Invalid TimeFrame '{sub.TimeFrame}' for asset '{sub.AssetName}'.");

        var subscription = new DataSubscription(asset, timeFrame);
        target.Add(subscription);

        var key = CacheKey(asset, timeFrame);
        if (!dataCache.ContainsKey(key))
        {
            var series = historyRepository.Load(subscription, fromDate, toDate);
            dataCache[key] = (asset, series);
        }
    }

    public BacktestRunRecord ExecuteTrial(
        string strategyName,
        BacktestSettingsDto settings,
        ParameterCombination combination,
        IOptimizationStrategyFactory factory,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        Guid optimizationRunId,
        DateTimeOffset startedAt,
        ref string? strategyVersion,
        CancellationToken token)
    {
        var trialWatch = Stopwatch.StartNew();

        // 1. Extract trial subscriptions from the combination's axis group
        var trialSubscriptions = combination.Values.TryGetValue("DataSubscriptions", out var subObj)
            && subObj is List<DataSubscription> group
            ? group
            : throw new InvalidOperationException("Trial has no data subscriptions — this indicates a bug in subscription resolution.");

        // 2. Scale QuoteAsset params using this trial's actual asset
        var trialAsset = trialSubscriptions[0].Asset;
        var scale = new ScaleContext(trialAsset);
        var mutableParams = new Dictionary<string, object>(combination.Values);
        var scaledParams = ParameterScaler.ScaleQuoteAssetParams(
            spaceProvider, strategyName, mutableParams, scale);
        var scaledCombination = new ParameterCombination(
            scaledParams as IReadOnlyDictionary<string, object> ?? new Dictionary<string, object>(scaledParams!));

        // 3. Create strategy with scaled parameters
        var strategy = factory.Create(strategyName, scaledCombination);
        Interlocked.CompareExchange(ref strategyVersion, strategy.Version, null);

        strategy.DataSubscriptions.Clear();
        foreach (var sub in trialSubscriptions)
            strategy.DataSubscriptions.Add(sub);

        var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
        for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
        {
            var sub = strategy.DataSubscriptions[i];
            var key = CacheKey(sub.Asset, sub.TimeFrame);
            if (dataCache.TryGetValue(key, out var cached))
                seriesArray[i] = cached.Series;
            else
                throw new InvalidOperationException($"No pre-loaded data for subscription {key}.");
        }

        var backOptions = new BacktestOptions
        {
            InitialCash = scale.AmountToTicks(settings.InitialCash),
            StartTime = settings.StartTime,
            EndTime = settings.EndTime,
            CommissionPerTrade = settings.CommissionPerTrade,
            SlippageTicks = settings.SlippageTicks
        };

        var result = engine.Run(seriesArray, strategy, backOptions, token);

        var (metrics, trades) = metricsCalculator.Calculate(
            result.Fills, new EquityValueProjection(result.EquityCurve), backOptions.InitialCash,
            settings.StartTime, settings.EndTime);

        var scaledMetrics = MetricsScaler.ScaleDown(metrics, scale);
        var tradePnl = MetricsScaler.ScaleTradePnl(trades, scale);
        trialWatch.Stop();

        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = strategyName,
            StrategyVersion = strategy.Version,
            Parameters = combination.Values, // Store original unscaled values
            DataSubscriptions = strategy.DataSubscriptions
                .Select(s => new DataSubscriptionDto
                {
                    AssetName = AssetLookupName.From(s.Asset),
                    Exchange = s.Asset.Exchange,
                    TimeFrame = s.TimeFrame.Code,
                }).ToList(),
            BacktestSettings = settings,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = (long)trialWatch.Elapsed.TotalMilliseconds,
            TotalBars = result.TotalBarsProcessed,
            Metrics = scaledMetrics,
            EquityCurve = [], // Equity curve not persisted for optimization trials — trade P&L is sufficient
            TradePnl = tradePnl,
            RunFolderPath = null,
            RunMode = RunModes.Backtest,
            OptimizationRunId = optimizationRunId,
        };
    }

    public async Task SaveErrorOptimizationAsync(
        string strategyName,
        BacktestSettingsDto backtestSettings,
        IReadOnlyList<DataSubscriptionDto> subscriptions,
        string sortBy,
        int maxParallelism,
        Guid optimizationRunId,
        DateTimeOffset startedAt,
        long estimatedCount,
        BoundedTrialQueue topTrials,
        FailedTrialCollector failedTrials,
        long filteredOutCount,
        long failedTrialCount,
        string errorMessage,
        string? errorStackTrace = null,
        string? optimizationMethod = null,
        int? generationsCompleted = null)
    {
        try
        {
            var completedAt = DateTimeOffset.UtcNow;
            var record = new OptimizationRunRecord
            {
                Id = optimizationRunId,
                StrategyName = strategyName,
                StrategyVersion = "0",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
                TotalCombinations = estimatedCount,
                SortBy = sortBy,
                DataSubscriptions = subscriptions,
                BacktestSettings = backtestSettings,
                MaxParallelism = maxParallelism,
                Trials = topTrials.DeduplicateAndDrainSorted(),
                FailedTrialDetails = failedTrials.Drain(optimizationRunId),
                FilteredTrials = filteredOutCount,
                FailedTrials = failedTrialCount,
                ErrorMessage = errorMessage,
                ErrorStackTrace = errorStackTrace,
                OptimizationMethod = optimizationMethod,
                GenerationsCompleted = generationsCompleted,
                Status = OptimizationRunStatus.FromError(errorMessage),
            };
            await runRepository.SaveOptimizationAsync(record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist error record for optimization {RunId}", optimizationRunId);
        }
    }

    public static IReadOnlyList<DataSubscriptionDto> GetSubscriptionDtos(
        List<List<DataSubscriptionDto>>? axisGroups)
    {
        if (axisGroups is not { Count: > 0 })
            return [];

        var firstGroup = axisGroups[0];
        var groupLabel = string.Join("+", firstGroup.Select(s => s.AssetName));
        if (axisGroups.Count > 1)
            return [firstGroup[0] with { AssetName = $"{groupLabel} (+{axisGroups.Count - 1} more)" }];
        return [firstGroup[0] with { AssetName = groupLabel }];
    }

    public static string CacheKey(Asset asset, TimeSpan timeFrame) =>
        $"{asset.Name}|{asset.Settlement}|{timeFrame}";

    public async Task InsertPlaceholderAsync(OptimizationRunRecord record, CancellationToken ct = default) =>
        await runRepository.InsertOptimizationPlaceholderAsync(record, ct);

    public async Task SaveOptimizationAsync(OptimizationRunRecord record) =>
        await runRepository.SaveOptimizationAsync(record);

    /// <summary>Zero-allocation projection: exposes <c>EquitySnapshot.Value</c> as <c>IReadOnlyList&lt;long&gt;</c>.</summary>
    internal sealed class EquityValueProjection(IReadOnlyList<EquitySnapshot> source) : IReadOnlyList<long>
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

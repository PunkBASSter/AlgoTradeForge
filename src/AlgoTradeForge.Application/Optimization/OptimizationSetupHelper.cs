using System.Diagnostics;
using System.Globalization;
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
    /// Routes DataSubscription/SubscriptionAxis DTOs into fixed vs axis lists.
    /// Pure routing logic — no I/O. Used by both the execution path and the evaluate query.
    /// </summary>
    public static (List<T> Fixed, List<T> Axis) RouteSubscriptions<T>(
        List<T>? dataSubs, List<T>? axisSubs)
    {
        var hasDataSubs = dataSubs is { Count: > 0 };
        var hasAxisSubs = axisSubs is { Count: > 0 };

        if (hasAxisSubs)
            return (dataSubs ?? [], axisSubs!);

        if (hasDataSubs && dataSubs!.Count > 1)
            return ([], dataSubs);

        return (dataSubs ?? [], []);
    }

    /// <summary>
    /// Routes DataSubscription/SubscriptionAxis DTOs into resolved fixed and axis subscription lists,
    /// pre-loading data into the cache. Encapsulates the shared routing logic between brute-force
    /// and genetic optimization handlers.
    /// </summary>
    public async Task<(List<DataSubscription> FixedSubscriptions,
        List<DataSubscription> AxisSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> DataCache)>
        ResolveSubscriptionsAsync(
            List<DataSubscriptionDto>? dataSubs,
            List<DataSubscriptionDto>? axisSubs,
            DateOnly fromDate, DateOnly toDate,
            CancellationToken ct)
    {
        var hasDataSubs = dataSubs is { Count: > 0 };
        var hasAxisSubs = axisSubs is { Count: > 0 };

        if (!hasDataSubs && !hasAxisSubs)
            throw new ArgumentException("At least one DataSubscription or SubscriptionAxis entry must be provided.");

        var (fixedDtos, axisDtos) = RouteSubscriptions(dataSubs, axisSubs);

        var fixedSubscriptions = new List<DataSubscription>();
        var axisSubscriptions = new List<DataSubscription>();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();

        foreach (var sub in fixedDtos)
            await ResolveAndCacheAsync(sub, fixedSubscriptions, dataCache, fromDate, toDate, ct);
        foreach (var sub in axisDtos)
            await ResolveAndCacheAsync(sub, axisSubscriptions, dataCache, fromDate, toDate, ct);

        return (fixedSubscriptions, axisSubscriptions, dataCache);
    }

    /// <summary>
    /// Appends axis subscriptions as a discrete axis and filters out empty axes.
    /// </summary>
    public static List<ResolvedAxis> AppendSubscriptionAxisAndFilter(
        IReadOnlyList<ResolvedAxis> resolvedAxes,
        List<DataSubscription> axisSubscriptions)
    {
        return AppendSubscriptionAxisAndFilter(resolvedAxes, axisSubscriptions.Count,
            axisSubscriptions.Cast<object>().ToList());
    }

    /// <summary>
    /// Count-only overload: appends a placeholder subscription axis with the given count
    /// and filters out empty axes. Used by the evaluate path where actual subscriptions
    /// are not loaded.
    /// </summary>
    public static List<ResolvedAxis> AppendSubscriptionAxisAndFilter(
        IReadOnlyList<ResolvedAxis> resolvedAxes,
        int subscriptionAxisCount,
        List<object>? axisValues = null)
    {
        var allAxes = new List<ResolvedAxis>(resolvedAxes);

        if (subscriptionAxisCount > 0)
        {
            var values = axisValues
                ?? Enumerable.Range(0, subscriptionAxisCount).Select(i => (object)i).ToList();
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

    public async Task ResolveAndCacheAsync(
        DataSubscriptionDto sub,
        List<DataSubscription> target,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        DateOnly fromDate, DateOnly toDate,
        CancellationToken ct)
    {
        var asset = await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{sub.AssetName}' on exchange '{sub.Exchange}' not found.");

        if (!TimeSpan.TryParse(sub.TimeFrame, CultureInfo.InvariantCulture, out var timeFrame))
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
        List<DataSubscription> fixedSubscriptions,
        Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)> dataCache,
        Guid optimizationRunId,
        DateTimeOffset startedAt,
        ref string? strategyVersion,
        CancellationToken token)
    {
        var trialWatch = Stopwatch.StartNew();

        // 1. Determine trial subscriptions first to find the correct asset
        var trialSubscriptions = new List<DataSubscription>(fixedSubscriptions);
        if (combination.Values.TryGetValue("DataSubscriptions", out var subObj) &&
            subObj is DataSubscription dataSub)
        {
            trialSubscriptions.Add(dataSub);
        }

        if (trialSubscriptions.Count == 0)
            throw new InvalidOperationException("Trial has no data subscriptions — this indicates a bug in subscription resolution.");

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

        var trialPrimarySub = strategy.DataSubscriptions[0];
        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = strategyName,
            StrategyVersion = strategy.Version,
            Parameters = combination.Values, // Store original unscaled values
            DataSubscription = new DataSubscriptionDto
            {
                AssetName = AssetLookupName.From(trialPrimarySub.Asset),
                Exchange = trialPrimarySub.Asset.Exchange,
                TimeFrame = TimeFrameFormatter.Format(trialPrimarySub.TimeFrame),
            },
            BacktestSettings = settings,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = (long)trialWatch.Elapsed.TotalMilliseconds,
            TotalBars = result.TotalBarsProcessed,
            Metrics = scaledMetrics,
            EquityCurve = [],
            TradePnl = tradePnl,
            RunFolderPath = null,
            RunMode = RunModes.Backtest,
            OptimizationRunId = optimizationRunId,
        };
    }

    public async Task SaveErrorOptimizationAsync(
        string strategyName,
        BacktestSettingsDto backtestSettings,
        DataSubscriptionDto primarySub,
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
                DataSubscription = primarySub,
                BacktestSettings = backtestSettings,
                MaxParallelism = maxParallelism,
                Trials = topTrials.DrainSorted(),
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

    public static DataSubscriptionDto GetPrimarySubscriptionDto(
        List<DataSubscriptionDto>? fixedSubs, List<DataSubscriptionDto>? axisSubs)
    {
        if (fixedSubs is { Count: > 0 })
        {
            var primary = fixedSubs[0];
            if (axisSubs is { Count: > 0 })
                return primary with { AssetName = $"{primary.AssetName} (+{axisSubs.Count} more)" };
            return primary;
        }

        var first = axisSubs![0];
        if (axisSubs.Count > 1)
            return first with { AssetName = $"{first.AssetName} (+{axisSubs.Count - 1} more)" };
        return first;
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

using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Strategies;

public static class StrategyTemplateBuilder
{
    private const string DefaultAsset = "BTCUSDT";
    private const string DefaultExchange = "Binance";

    public static Dictionary<string, object> BuildBacktestTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyList<AvailableAssetInfo> availableAssets,
        int requiredSubscriptionCount = 1) => new()
    {
        ["dataSubscriptions"] = BuildSubscriptionList(availableAssets, "1h", requiredSubscriptionCount),
        ["backtestSettings"] = new Dictionary<string, object>
        {
            ["initialCash"] = 10000,
            ["startTime"] = "2025-01-01T00:00:00Z",
            ["endTime"] = "2025-12-31T23:59:59Z",
            ["commissionPerTrade"] = 0.001,
            ["slippageTicks"] = 0,
        },
        ["strategyName"] = strategyName,
        ["strategyParameters"] = ConvertToHumanReadable(paramDefaults, axes),
    };

    public static Dictionary<string, object> BuildOptimizationTemplate(
        string strategyName,
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyList<AvailableAssetInfo> availableAssets,
        int requiredSubscriptionCount = 1)
    {
        var axisOverrides = new Dictionary<string, object>();
        foreach (var axis in axes)
            axisOverrides[axis.Name] = BuildAxisOverride(axis);

        return new Dictionary<string, object>
        {
            ["strategyName"] = strategyName,
            ["backtestSettings"] = new Dictionary<string, object>
            {
                ["initialCash"] = 10000,
                ["startTime"] = "2025-01-01T00:00:00Z",
                ["endTime"] = "2025-12-31T23:59:59Z",
                ["commissionPerTrade"] = 0.001,
                ["slippageTicks"] = 0,
            },
            ["optimizationSettings"] = new Dictionary<string, object>
            {
                ["sortBy"] = "sortinoRatio",
                ["maxTrialsToKeep"] = 10000,
                ["minProfitFactor"] = 0.5,
                ["maxDrawdownPct"] = 95.0,
                ["minSharpeRatio"] = -5.0,
                ["minSortinoRatio"] = -5.0,
                ["minAnnualizedReturnPct"] = -100.0,
                ["minTradeCount"] = 30,
                ["fitnessWeights"] = new Dictionary<string, object>
                {
                    ["sharpeWeight"] = 0.5,
                    ["sortinoWeight"] = 0.2,
                    ["profitFactorWeight"] = 0.15,
                    ["annualizedReturnWeight"] = 0.15,
                    ["maxDrawdownThreshold"] = 30.0,
                    ["minTrades"] = 10,
                },
            },
            ["subscriptionAxis"] = BuildSubscriptionGroups(availableAssets, "1h", requiredSubscriptionCount),
            ["optimizationAxes"] = axisOverrides.Count > 0 ? axisOverrides : null!,
        };
    }

    public static Dictionary<string, object> BuildGeneticOptimizationTemplate(
        string strategyName,
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyList<AvailableAssetInfo> availableAssets,
        int requiredSubscriptionCount = 1)
    {
        var grid = BuildOptimizationTemplate(strategyName, axes, availableAssets, requiredSubscriptionCount);

        var geneticSettings = new Dictionary<string, object>
        {
            ["populationSize"] = 0,
            ["maxGenerations"] = 0,
            ["maxEvaluations"] = 0,
            ["eliteCount"] = 2,
            ["crossoverRate"] = 0.85,
            ["tournamentSize"] = 3,
            ["stagnationLimit"] = 20,
        };

        // Maintain section order: settings together, then axes
        return new Dictionary<string, object>
        {
            ["strategyName"] = grid["strategyName"],
            ["backtestSettings"] = grid["backtestSettings"],
            ["optimizationSettings"] = grid["optimizationSettings"],
            ["geneticSettings"] = geneticSettings,
            ["subscriptionAxis"] = grid["subscriptionAxis"],
            ["optimizationAxes"] = grid["optimizationAxes"],
        };
    }

    public static Dictionary<string, object> BuildLiveSessionTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyList<AvailableAssetInfo> availableAssets) => new()
    {
        ["strategyName"] = strategyName,
        ["initialCash"] = 10000,
        ["accountName"] = "paper",
        ["dataSubscriptions"] = BuildSubscriptions(availableAssets, "1m"),
        ["strategyParameters"] = ConvertToHumanReadable(paramDefaults, axes),
    };

    public static Dictionary<string, object> BuildDebugSessionTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyList<AvailableAssetInfo> availableAssets,
        int requiredSubscriptionCount = 1) => new()
    {
        ["dataSubscriptions"] = BuildSubscriptionList(availableAssets, "1h", requiredSubscriptionCount),
        ["backtestSettings"] = new Dictionary<string, object>
        {
            ["initialCash"] = 10000,
            ["startTime"] = "2025-01-01T00:00:00Z",
            ["endTime"] = "2025-12-31T23:59:59Z",
            ["commissionPerTrade"] = 0.001,
            ["slippageTicks"] = 0,
        },
        ["strategyName"] = strategyName,
        ["strategyParameters"] = ConvertToHumanReadable(paramDefaults, axes),
    };

    private static string FirstAssetOrDefault(IReadOnlyList<AvailableAssetInfo> assets) =>
        assets.Count > 0 ? assets[0].LookupName : DefaultAsset;

    private static string FirstExchangeOrDefault(IReadOnlyList<AvailableAssetInfo> assets) =>
        assets.Count > 0 ? assets[0].Exchange : DefaultExchange;

    private const string DefaultSecondaryAsset = "ETHUSDT";

    private static List<Dictionary<string, object>> BuildSubscriptionList(
        IReadOnlyList<AvailableAssetInfo> assets, string timeFrame, int count)
    {
        var result = new List<Dictionary<string, object>>();
        for (var i = 0; i < count; i++)
        {
            var assetName = i < assets.Count ? assets[i].LookupName
                : i == 0 ? DefaultAsset : DefaultSecondaryAsset;
            var exchange = i < assets.Count ? assets[i].Exchange : DefaultExchange;
            result.Add(Subscription(
                assetName, exchange, timeFrame,
                i == 0 ? DataFeedRole.Primary : DataFeedRole.Side));
        }
        return result;
    }

    private static List<Dictionary<string, object>> BuildSubscriptions(
        IReadOnlyList<AvailableAssetInfo> assets, string timeFrame)
    {
        if (assets.Count == 0)
            return [Subscription(DefaultAsset, DefaultExchange, timeFrame, DataFeedRole.Primary)];

        return assets
            .Select((a, i) => Subscription(
                a.LookupName, a.Exchange, timeFrame,
                i == 0 ? DataFeedRole.Primary : DataFeedRole.Side))
            .ToList();
    }

    // Role conventions in templates:
    //   • BuildSubscriptions (single backtest list): index 0 = Primary, index 1+ = Side.
    //   • Optimization axis groups: every entry is Role=Primary (fan-out candidate primaries —
    //     the optimizer runs |primaries| × |combos| per group). Side feeds in optimization
    //     templates require manual JSON edits.
    // Role is emitted as the enum name ("Primary"/"Side"), never the ordinal — the template
    // dictionary boxes values as object, so JsonStringEnumConverter cannot intervene, and the
    // frontend matches on the string.
    private static Dictionary<string, object> Subscription(
        string assetName, string exchange, string timeFrame, DataFeedRole role) => new()
    {
        ["kind"] = "TimeBar",
        ["role"] = role.ToString(),
        ["assetName"] = assetName,
        ["exchange"] = exchange,
        ["timeFrame"] = timeFrame,
    };

    /// <summary>
    /// Builds subscription axis groups for optimization templates.
    /// Always returns a 2D structure: <c>[[sub1], [sub2]]</c> for single-sub strategies,
    /// <c>[[sub1, sub2], [sub3, sub4]]</c> for multi-sub strategies.
    /// For <paramref name="groupSize"/>=2, auto-pairs by name pattern (e.g., BTCUSDT + BTCUSDT_PERP).
    /// For larger groups, chunks sequentially.
    /// </summary>
    private static List<List<Dictionary<string, object>>> BuildSubscriptionGroups(
        IReadOnlyList<AvailableAssetInfo> assets, string timeFrame, int groupSize)
    {
        if (groupSize <= 1)
        {
            return WrapAsSingletonGroups(assets, timeFrame);
        }

        if (groupSize == 2)
        {
            var pairs = PairByNamePattern(assets, timeFrame);
            if (pairs.Count > 0) return pairs;
        }

        // Fallback: sequential chunking
        var groups = new List<List<Dictionary<string, object>>>();
        for (var i = 0; i + groupSize <= assets.Count; i += groupSize)
        {
            var group = new List<Dictionary<string, object>>();
            for (var j = 0; j < groupSize; j++)
            {
                group.Add(Subscription(assets[i + j].LookupName, assets[i + j].Exchange, timeFrame, DataFeedRole.Primary));
            }
            groups.Add(group);
        }

        return groups.Count > 0
            ? groups
            : new List<List<Dictionary<string, object>>>
            {
                Enumerable.Range(0, groupSize)
                    .Select(i => Subscription(
                        i == 0 ? DefaultAsset : $"{DefaultSecondaryAsset}_PERP",
                        DefaultExchange,
                        timeFrame,
                        DataFeedRole.Primary))
                    .ToList()
            };
    }

    private static List<List<Dictionary<string, object>>> WrapAsSingletonGroups(
        IReadOnlyList<AvailableAssetInfo> assets, string timeFrame)
    {
        if (assets.Count == 0)
            return [[Subscription(DefaultAsset, DefaultExchange, timeFrame, DataFeedRole.Primary)]];

        return assets
            .Select(a => new List<Dictionary<string, object>>
            {
                Subscription(a.LookupName, a.Exchange, timeFrame, DataFeedRole.Primary),
            })
            .ToList();
    }

    private const string PerpSuffix = "_PERP";

    /// <summary>
    /// Auto-pairs assets by matching base names to their _PERP counterparts.
    /// E.g., BTCUSDT → BTCUSDT_PERP, ETHUSDT → ETHUSDT_PERP.
    /// </summary>
    private static List<List<Dictionary<string, object>>> PairByNamePattern(
        IReadOnlyList<AvailableAssetInfo> assets, string timeFrame)
    {
        var perpLookup = new Dictionary<string, AvailableAssetInfo>(StringComparer.OrdinalIgnoreCase);
        var baseAssets = new List<AvailableAssetInfo>();

        foreach (var asset in assets)
        {
            if (asset.LookupName.EndsWith(PerpSuffix, StringComparison.OrdinalIgnoreCase))
                perpLookup[asset.LookupName[..^PerpSuffix.Length]] = asset;
            else
                baseAssets.Add(asset);
        }

        var pairs = new List<List<Dictionary<string, object>>>();
        foreach (var baseAsset in baseAssets)
        {
            if (!perpLookup.TryGetValue(baseAsset.LookupName, out var perpAsset))
                continue;

            pairs.Add(
            [
                Subscription(baseAsset.LookupName, baseAsset.Exchange, timeFrame, DataFeedRole.Primary),
                Subscription(perpAsset.LookupName, perpAsset.Exchange, timeFrame, DataFeedRole.Primary),
            ]);
        }

        return pairs;
    }

    private static Dictionary<string, object> ConvertToHumanReadable(
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes)
    {
        var result = new Dictionary<string, object>(paramDefaults);
        foreach (var axis in axes)
        {
            if (axis is NumericRangeAxis n
                && n.Unit == ParamUnit.QuoteAsset
                && result.TryGetValue(n.Name, out var rawVal)
                && rawVal is IConvertible conv)
            {
                var raw = conv.ToDecimal(null);
                result[n.Name] = ToHumanReadable(raw, n.Min, n.Max);
            }
        }
        return result;
    }

    private static object BuildAxisOverride(ParameterAxis axis)
    {
        if (axis is ModuleSlotAxis m)
        {
            var variants = new Dictionary<string, object?>();
            foreach (var v in m.Variants)
            {
                if (v.Axes.Count == 0)
                {
                    variants[v.TypeKey] = null;
                }
                else
                {
                    var subAxes = new Dictionary<string, object>();
                    foreach (var sub in v.Axes)
                        subAxes[sub.Name] = BuildAxisOverride(sub);
                    variants[v.TypeKey] = subAxes;
                }
            }
            return new Dictionary<string, object?> { ["variants"] = variants };
        }

        if (axis is NumericRangeAxis n)
            return new Dictionary<string, object>
            {
                ["min"] = n.Min,
                ["max"] = n.Max,
                ["step"] = n.Step,
            };

        if (axis is DiscreteSetAxis d)
            return new Dictionary<string, object>
            {
                ["values"] = d.Values.Select(v => v.ToString()!).ToList(),
            };

        return new Dictionary<string, object>
        {
            ["min"] = 0,
            ["max"] = 1,
            ["step"] = 1,
        };
    }

    private static object ToHumanReadable(decimal rawDefault, decimal min, decimal max)
    {
        if (rawDefault <= max) return rawDefault;
        for (var scale = 10m; scale <= 1_000_000m; scale *= 10)
        {
            var human = rawDefault / scale;
            if (human >= min && human <= max) return human;
        }
        return rawDefault;
    }
}

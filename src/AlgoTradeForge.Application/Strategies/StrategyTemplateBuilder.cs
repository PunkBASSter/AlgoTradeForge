using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Strategies;

public static class StrategyTemplateBuilder
{
    private const string DefaultAsset = "BTCUSDT";
    private const string DefaultExchange = "Binance";

    public static Dictionary<string, object> BuildBacktestTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyList<AvailableAssetInfo> availableAssets) => new()
    {
        ["dataSubscription"] = new Dictionary<string, object>
        {
            ["assetName"] = FirstAssetOrDefault(availableAssets),
            ["exchange"] = FirstExchangeOrDefault(availableAssets),
            ["timeFrame"] = "01:00:00",
        },
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
        IReadOnlyList<AvailableAssetInfo> availableAssets)
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
            },
            ["subscriptionAxis"] = BuildSubscriptions(availableAssets, "01:00:00"),
            ["optimizationAxes"] = axisOverrides.Count > 0 ? axisOverrides : null!,
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
        ["dataSubscriptions"] = BuildSubscriptions(availableAssets, "00:01:00"),
        ["strategyParameters"] = ConvertToHumanReadable(paramDefaults, axes),
    };

    public static Dictionary<string, object> BuildDebugSessionTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyList<AvailableAssetInfo> availableAssets) => new()
    {
        ["dataSubscription"] = new Dictionary<string, object>
        {
            ["assetName"] = FirstAssetOrDefault(availableAssets),
            ["exchange"] = FirstExchangeOrDefault(availableAssets),
            ["timeFrame"] = "01:00:00",
        },
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

    private static List<Dictionary<string, object>> BuildSubscriptions(
        IReadOnlyList<AvailableAssetInfo> assets, string timeFrame)
    {
        if (assets.Count == 0)
            return [new() { ["asset"] = DefaultAsset, ["exchange"] = DefaultExchange, ["timeFrame"] = timeFrame }];

        return assets
            .Select(a => new Dictionary<string, object>
            {
                ["asset"] = a.LookupName,
                ["exchange"] = a.Exchange,
                ["timeFrame"] = timeFrame,
            })
            .ToList();
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

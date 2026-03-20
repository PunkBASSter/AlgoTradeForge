using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Strategies;

public static class StrategyTemplateBuilder
{
    public static Dictionary<string, object> BuildBacktestTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes) => new()
    {
        ["assetName"] = "BTCUSDT",
        ["exchange"] = "Binance",
        ["strategyName"] = strategyName,
        ["initialCash"] = 10000,
        ["startTime"] = "2025-01-01T00:00:00Z",
        ["endTime"] = "2025-12-31T23:59:59Z",
        ["commissionPerTrade"] = 0.001,
        ["slippageTicks"] = 2,
        ["timeFrame"] = "01:00:00",
        ["strategyParameters"] = ConvertToHumanReadable(paramDefaults, axes),
    };

    public static Dictionary<string, object> BuildOptimizationTemplate(
        string strategyName, IReadOnlyList<ParameterAxis> axes)
    {
        var axisOverrides = new Dictionary<string, object>();
        foreach (var axis in axes)
            axisOverrides[axis.Name] = BuildAxisOverride(axis);

        return new Dictionary<string, object>
        {
            ["strategyName"] = strategyName,
            ["dataSubscriptions"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["asset"] = "BTCUSDT",
                    ["exchange"] = "Binance",
                    ["timeFrame"] = "00:15:00",
                },
            },
            ["initialCash"] = 10000,
            ["startTime"] = "2025-01-01T00:00:00Z",
            ["endTime"] = "2025-12-31T23:59:59Z",
            ["commissionPerTrade"] = 0.001,
            ["slippageTicks"] = 2,
            ["sortBy"] = "sortinoRatio",
            ["maxTrialsToKeep"] = 10000,
            ["minProfitFactor"] = 0.5,
            ["maxDrawdownPct"] = 95.0,
            ["minSharpeRatio"] = -5.0,
            ["minSortinoRatio"] = -5.0,
            ["minAnnualizedReturnPct"] = -100.0,
            ["optimizationAxes"] = axisOverrides.Count > 0 ? axisOverrides : null!,
        };
    }

    public static Dictionary<string, object> BuildLiveSessionTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes) => new()
    {
        ["strategyName"] = strategyName,
        ["initialCash"] = 10000,
        ["accountName"] = "paper",
        ["dataSubscriptions"] = new List<Dictionary<string, object>>
        {
            new()
            {
                ["asset"] = "BTCUSDT",
                ["exchange"] = "Binance",
                ["timeFrame"] = "00:01:00",
            },
        },
        ["strategyParameters"] = ConvertToHumanReadable(paramDefaults, axes),
    };

    public static Dictionary<string, object> BuildDebugSessionTemplate(
        string strategyName,
        IReadOnlyDictionary<string, object> paramDefaults,
        IReadOnlyList<ParameterAxis> axes) => new()
    {
        ["assetName"] = "BTCUSDT",
        ["exchange"] = "Binance",
        ["strategyName"] = strategyName,
        ["initialCash"] = 10000,
        ["startTime"] = "2025-01-01T00:00:00Z",
        ["endTime"] = "2025-12-31T23:59:59Z",
        ["commissionPerTrade"] = 0.001,
        ["slippageTicks"] = 2,
        ["timeFrame"] = "01:00:00",
        ["strategyParameters"] = ConvertToHumanReadable(paramDefaults, axes),
    };

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

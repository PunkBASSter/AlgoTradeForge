using System.Globalization;
using System.Text;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
namespace AlgoTradeForge.Application.Progress;

public static class RunKeyBuilder
{
    public static string Build(RunBacktestCommand cmd)
    {
        var settings = cmd.BacktestSettings;
        var sb = new StringBuilder();
        sb.Append(cmd.StrategyName).Append('|');
        foreach (var sub in cmd.DataSubscriptions.OrderBy(BacktestInputsFormatter.Key, StringComparer.Ordinal))
            sb.Append(BacktestInputsFormatter.Key(sub)).Append(',');
        sb.Append('|');
        sb.Append(settings.StartTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.EndTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.InitialCash).Append('|');
        sb.Append(settings.CommissionPerTrade).Append('|');
        sb.Append(settings.SlippageTicks);

        if (cmd.StrategyParameters is { Count: > 0 })
        {
            sb.Append('|');
            RunKeyHasher.AppendSortedParams(sb, cmd.StrategyParameters);
        }

        return RunKeyHasher.HashString(sb.ToString());
    }

    public static string BuildGroupKey(
        string strategyName,
        BacktestSettingsDto settings,
        string optimizationMethod,
        List<List<DataFeedSubscription>>? subscriptionAxis,
        Dictionary<string, OptimizationAxisOverride>? axes)
    {
        var sb = new StringBuilder();
        sb.Append("group|");
        sb.Append(strategyName).Append('|');
        sb.Append(optimizationMethod).Append('|');
        sb.Append(settings.StartTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.EndTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.InitialCash).Append('|');
        sb.Append(settings.CommissionPerTrade).Append('|');
        sb.Append(settings.SlippageTicks);

        if (subscriptionAxis is { Count: > 0 })
        {
            sb.Append("|dss:");
            var sortedGroups = subscriptionAxis
                .Select(g => g.OrderBy(BacktestInputsFormatter.Key, StringComparer.Ordinal).ToList())
                .OrderBy(g => BacktestInputsFormatter.Key(g[0]), StringComparer.Ordinal);
            foreach (var sortedGroup in sortedGroups)
            {
                sb.Append('[');
                foreach (var sub in sortedGroup)
                    sb.Append(BacktestInputsFormatter.Key(sub)).Append(',');
                sb.Append(']');
            }
        }

        if (axes is { Count: > 0 })
        {
            sb.Append('|');
            foreach (var kvp in axes.OrderBy(k => k.Key))
                sb.Append(kvp.Key).Append('=').Append(string.Format(CultureInfo.InvariantCulture, "{0}", kvp.Value)).Append(',');
        }

        return RunKeyHasher.HashString(sb.ToString());
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Application.Optimization;
namespace AlgoTradeForge.Application.Progress;

public static class RunKeyBuilder
{
    public static string Build(RunBacktestCommand cmd)
    {
        var sub = cmd.DataSubscription;
        var settings = cmd.BacktestSettings;
        var sb = new StringBuilder();
        sb.Append(cmd.StrategyName).Append('|');
        sb.Append(sub.AssetName).Append('|');
        sb.Append(sub.Exchange).Append('|');
        sb.Append(!string.IsNullOrEmpty(sub.TimeFrame) ? sub.TimeFrame : "default").Append('|');
        sb.Append(settings.StartTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.EndTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.InitialCash).Append('|');
        sb.Append(settings.CommissionPerTrade).Append('|');
        sb.Append(settings.SlippageTicks);

        if (cmd.StrategyParameters is { Count: > 0 })
        {
            sb.Append('|');
            AppendSortedParams(sb, cmd.StrategyParameters);
        }

        return HashString(sb.ToString());
    }

    public static string Build(RunOptimizationCommand cmd)
    {
        var settings = cmd.BacktestSettings;
        var sb = new StringBuilder();
        sb.Append(cmd.StrategyName).Append('|');
        sb.Append(settings.StartTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.EndTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(settings.InitialCash).Append('|');
        sb.Append(settings.CommissionPerTrade).Append('|');
        sb.Append(settings.SlippageTicks).Append('|');
        sb.Append(cmd.MaxDegreeOfParallelism).Append('|');
        sb.Append(cmd.MaxCombinations).Append('|');
        sb.Append(cmd.SortBy);

        if (cmd.DataSubscriptions is { Count: > 0 })
        {
            sb.Append('|');
            var sorted = cmd.DataSubscriptions
                .OrderBy(d => d.AssetName)
                .ThenBy(d => d.Exchange)
                .ThenBy(d => d.TimeFrame);
            foreach (var sub in sorted)
                sb.Append(sub.AssetName).Append(':').Append(sub.Exchange).Append(':').Append(sub.TimeFrame).Append(',');
        }

        if (cmd.SubscriptionAxis is { Count: > 0 })
        {
            sb.Append("|axis:");
            var sortedAxis = cmd.SubscriptionAxis
                .OrderBy(d => d.AssetName)
                .ThenBy(d => d.Exchange)
                .ThenBy(d => d.TimeFrame);
            foreach (var sub in sortedAxis)
                sb.Append(sub.AssetName).Append(':').Append(sub.Exchange).Append(':').Append(sub.TimeFrame).Append(',');
        }

        if (cmd.Axes is { Count: > 0 })
        {
            sb.Append('|');
            foreach (var kvp in cmd.Axes.OrderBy(k => k.Key))
                sb.Append(kvp.Key).Append('=').Append(string.Format(CultureInfo.InvariantCulture, "{0}", kvp.Value)).Append(',');
        }

        return HashString(sb.ToString());
    }

    public static string Build(StartLiveSessionCommand cmd)
    {
        var sb = new StringBuilder();
        sb.Append(cmd.StrategyName);

        if (cmd.StrategyParameters is { Count: > 0 })
        {
            sb.Append('|');
            AppendSortedParams(sb, cmd.StrategyParameters);
        }

        if (cmd.DataSubscriptions is { Count: > 0 })
        {
            sb.Append('|');
            var sorted = cmd.DataSubscriptions
                .OrderBy(d => d.AssetName)
                .ThenBy(d => d.Exchange)
                .ThenBy(d => d.TimeFrame);
            foreach (var sub in sorted)
                sb.Append(sub.AssetName).Append(':').Append(sub.Exchange).Append(':').Append(sub.TimeFrame).Append(',');
        }

        return HashString(sb.ToString());
    }

    private static void AppendSortedParams(StringBuilder sb, IDictionary<string, object> parameters)
    {
        var first = true;
        foreach (var kvp in parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            sb.Append(kvp.Key).Append('=').Append(string.Format(CultureInfo.InvariantCulture, "{0}", kvp.Value));
            first = false;
        }
    }

    private static string HashString(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}

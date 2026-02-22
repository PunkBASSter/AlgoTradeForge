using System.Security.Cryptography;
using System.Text;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Optimization;

namespace AlgoTradeForge.Application.Progress;

public static class RunKeyBuilder
{
    public static string Build(RunBacktestCommand cmd)
    {
        var sb = new StringBuilder();
        sb.Append(cmd.StrategyName).Append('|');
        sb.Append(cmd.AssetName).Append('|');
        sb.Append(cmd.Exchange).Append('|');
        sb.Append(cmd.TimeFrame?.ToString() ?? "default").Append('|');
        sb.Append(cmd.StartTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(cmd.EndTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(cmd.InitialCash).Append('|');
        sb.Append(cmd.CommissionPerTrade).Append('|');
        sb.Append(cmd.SlippageTicks);

        if (cmd.StrategyParameters is { Count: > 0 })
        {
            sb.Append('|');
            AppendSortedParams(sb, cmd.StrategyParameters);
        }

        return HashString(sb.ToString());
    }

    public static string Build(RunOptimizationCommand cmd)
    {
        var sb = new StringBuilder();
        sb.Append(cmd.StrategyName).Append('|');
        sb.Append(cmd.StartTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(cmd.EndTime.ToUniversalTime().ToString("O")).Append('|');
        sb.Append(cmd.InitialCash).Append('|');
        sb.Append(cmd.CommissionPerTrade).Append('|');
        sb.Append(cmd.SlippageTicks).Append('|');
        sb.Append(cmd.MaxDegreeOfParallelism).Append('|');
        sb.Append(cmd.MaxCombinations).Append('|');
        sb.Append(cmd.SortBy);

        if (cmd.DataSubscriptions is { Count: > 0 })
        {
            sb.Append('|');
            var sorted = cmd.DataSubscriptions
                .OrderBy(d => d.Asset)
                .ThenBy(d => d.Exchange)
                .ThenBy(d => d.TimeFrame);
            foreach (var sub in sorted)
                sb.Append(sub.Asset).Append(':').Append(sub.Exchange).Append(':').Append(sub.TimeFrame).Append(',');
        }

        if (cmd.Axes is { Count: > 0 })
        {
            sb.Append('|');
            foreach (var kvp in cmd.Axes.OrderBy(k => k.Key))
                sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append(',');
        }

        return HashString(sb.ToString());
    }

    private static void AppendSortedParams(StringBuilder sb, IDictionary<string, object> parameters)
    {
        var first = true;
        foreach (var kvp in parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            sb.Append(kvp.Key).Append('=').Append(kvp.Value);
            first = false;
        }
    }

    private static string HashString(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}

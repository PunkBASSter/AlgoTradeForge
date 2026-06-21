using System.Text;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.LiveHost.Application.Live;

public static class LiveRunKeyBuilder
{
    public static string Build(StartLiveSessionCommand cmd)
    {
        var sb = new StringBuilder();
        sb.Append(cmd.StrategyName);

        if (cmd.StrategyParameters is { Count: > 0 })
        {
            sb.Append('|');
            RunKeyHasher.AppendSortedParams(sb, cmd.StrategyParameters);
        }

        if (cmd.DataSubscriptions is { Count: > 0 })
        {
            sb.Append('|');
            var sorted = cmd.DataSubscriptions
                .OrderBy(BacktestInputsFormatter.Key, StringComparer.Ordinal);
            foreach (var sub in sorted)
                sb.Append(BacktestInputsFormatter.Key(sub)).Append(',');
        }

        return RunKeyHasher.HashString(sb.ToString());
    }
}

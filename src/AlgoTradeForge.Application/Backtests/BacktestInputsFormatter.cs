using System.Text;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Stable string formatting for <see cref="DataFeedSubscription"/> and <see cref="BacktestInputs"/>.
/// Centralized so diagnostic labels, trial keys, run keys, and persistence indexes stay aligned.
/// </summary>
public static class BacktestInputsFormatter
{
    /// <summary>Compact form: <c>asset/exchange/feed</c>.</summary>
    public static string Format(DataFeedSubscription sub)
    {
        var feed = sub switch
        {
            TimeBarSubscription tb => tb.TimeFrame.Code,
            AltBarSubscription ab => ab.FeedId,
            TickSubscription => "ticks",
            SideFeedSubscription s => s.FeedId,
            _ => sub.GetType().Name,
        };
        return $"{sub.AssetName}/{sub.Exchange}/{feed}";
    }

    /// <summary>Comma-separated subscriptions, primary first.</summary>
    public static string Format(BacktestInputs inputs)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var sub in inputs.Subscriptions)
        {
            if (!first) sb.Append(", ");
            sb.Append(Format(sub));
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Hash-friendly key: <c>asset:exchange:feed:role</c>. Role is the integer ordinal so the
    /// persisted hash is decoupled from the JSON wire form (which uses string enums).
    /// </summary>
    public static string Key(DataFeedSubscription sub)
    {
        var feed = sub switch
        {
            TimeBarSubscription tb => tb.TimeFrame.Code,
            AltBarSubscription ab => ab.FeedId,
            TickSubscription => "ticks",
            SideFeedSubscription s => s.FeedId,
            _ => sub.GetType().Name,
        };
        return $"{sub.AssetName}:{sub.Exchange}:{feed}:{(int)sub.Role}";
    }
}

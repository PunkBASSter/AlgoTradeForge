using System.Text;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Stable string formatting for <see cref="DataFeedSubscription"/> and <see cref="BacktestInputs"/>.
/// Used for diagnostic labels (DSS labels), trial keys, run keys (where stable hashing matters),
/// and persistence indexes. Centralizing the logic keeps the format consistent across all of
/// these surfaces — when one needs to change, they all change together.
/// </summary>
public static class BacktestInputsFormatter
{
    /// <summary>
    /// Compact form: <c>asset/exchange/feed</c>. The feed segment is the canonical TimeFrame
    /// code for time bars, the FeedId for alt-bars and side feeds, or <c>"ticks"</c> for
    /// tick subscriptions.
    /// </summary>
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

    /// <summary>
    /// Comma-separated <see cref="Format(DataFeedSubscription)"/> across <see cref="BacktestInputs.All"/>.
    /// Primary first, side feeds in declaration order.
    /// </summary>
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
    /// Hash-friendly key for one subscription: <c>asset:exchange:feed:role</c>. Includes the
    /// role suffix so a subscription that appears in both Primary and Side positions across
    /// different inputs hashes distinctly.
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
        return $"{sub.AssetName}:{sub.Exchange}:{feed}:{sub.Role}";
    }
}

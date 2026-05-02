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
    /// different inputs hashes distinctly. The role is rendered as the integer ordinal
    /// (Primary=0, Side=1) — pinning it to ordinals here decouples persisted run-key hashes
    /// from the wire shape (the JSON layer renders <c>DataFeedRole</c> as <c>"Primary"</c>/
    /// <c>"Side"</c> via <c>JsonStringEnumConverter</c>; this key intentionally does not).
    /// </summary>
    /// <remarks>
    /// Including <c>role</c> means cross-input cache reuse only matches when role agrees, but
    /// this is safe for intra-run optimization caches: a subscription's role is fixed for the
    /// duration of one run (same <c>ctx.Subscriptions</c> reused across all trials), so the
    /// role-bearing key never aliases or causes redundant loads inside a single optimization.
    /// </remarks>
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

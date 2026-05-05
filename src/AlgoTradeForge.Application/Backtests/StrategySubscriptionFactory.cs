using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Converts a polymorphic <see cref="DataFeedSubscription"/> into the strategy-side
/// <see cref="DataSubscription"/>. For alt-bar / tick primaries the TimeFrame is a placeholder
/// derived from the source granularity — strategies must use bar timestamps for time arithmetic
/// since bar duration is variable.
/// </summary>
public static class StrategySubscriptionFactory
{
    /// <summary>Synthesizes the strategy-side subscription for a primary feed.</summary>
    public static DataSubscription FromPrimary(DataFeedSubscription sub, Asset asset) => sub switch
    {
        TimeBarSubscription tb => new DataSubscription(asset, tb.TimeFrame),
        AltBarSubscription ab  => new DataSubscription(
            asset, ResolveSourceTimeFrame(ab.FeedId), FeedKey: ab.FeedId),
        TickSubscription       => new DataSubscription(
            asset, TimeFrame.Parse("1m"), FeedKey: "ticks"),
        SideFeedSubscription   => throw new InvalidOperationException(
            "SideFeedSubscription cannot be converted to a primary DataSubscription. " +
            "Side feeds are FeedSeries, bound via IFeedContext / FeedContextBuilder."),
        _ => throw new ArgumentOutOfRangeException(nameof(sub),
            $"Unknown DataFeedSubscription subtype: {sub.GetType().Name}"),
    };

    /// <summary>
    /// Parses an alt-bar feed-id (<c>&lt;TypeCode&gt;_&lt;SourceCode&gt;_&lt;Threshold&gt;</c>)
    /// and returns the source TimeFrame. Tick-sourced alt-bars fall back to the <c>1m</c>
    /// sentinel since ticks have no native TimeFrame.
    /// </summary>
    public static TimeFrame ResolveSourceTimeFrame(string altBarFeedId)
    {
        var parts = altBarFeedId.Split('_');
        if (parts.Length != 3)
            throw new ArgumentException(
                $"Invalid alt-bar feed-id '{altBarFeedId}': expected positional grammar " +
                "'<TypeCode>_<SourceCode>_<Threshold>'.", nameof(altBarFeedId));

        var sourceCode = parts[1];
        if (sourceCode == "ticks")
            return TimeFrame.Parse("1m");

        try
        {
            return TimeFrame.Parse(sourceCode);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Invalid source code '{sourceCode}' in alt-bar feed-id '{altBarFeedId}'. " +
                "Source code must be a TimeFrame code (e.g. '1m', '1h') or 'ticks'.",
                nameof(altBarFeedId), ex);
        }
    }
}

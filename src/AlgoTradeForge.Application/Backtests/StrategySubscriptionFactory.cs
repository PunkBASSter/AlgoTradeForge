using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Phase 4 (TRD §9.3) bridge: converts a polymorphic <see cref="DataFeedSubscription"/>
/// into the strategy-side <see cref="DataSubscription"/> shape that
/// <c>IInt64BarStrategy.DataSubscriptions</c> consumes.
/// </summary>
/// <remarks>
/// <para>
/// For non-TimeBar primaries the strategy-side <c>TimeFrame</c> is a <em>placeholder</em>
/// derived from the alt-bar's source granularity (or the canonical <c>1m</c> sentinel for
/// tick-sourced bars). Strategies consuming alt-bar / tick primaries MUST use bar
/// timestamps for time arithmetic — <c>DataSubscription.TimeFrame.Duration</c> is not
/// authoritative for variable-duration bars. The <c>FeedKey</c> field carries the alt-bar
/// identity so the engine and run records can disambiguate.
/// </para>
/// <para>
/// Side feeds are FeedSeries (consumed via <c>IFeedContext</c>), not OHLCV bar series, and
/// are rejected by this factory. The fail-fast prevents a silent populate-then-fail-later
/// downstream where the loader would also reject.
/// </para>
/// </remarks>
public static class StrategySubscriptionFactory
{
    /// <summary>
    /// Synthesizes the strategy-side <see cref="DataSubscription"/> for a primary feed.
    /// Throws on <see cref="SideFeedSubscription"/> — side feeds don't enter the
    /// strategy's <c>DataSubscriptions</c> list.
    /// </summary>
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
    /// Extracts the placeholder TimeFrame from an alt-bar feed-id. Per TRD §3.3 the grammar
    /// is positional (<c>&lt;TypeCode&gt;_&lt;SourceCode&gt;_&lt;Threshold&gt;</c>); the
    /// source-code component drives the placeholder. Tick-sourced alt-bars fall back to
    /// the canonical <c>1m</c> sentinel because there is no source TimeFrame for ticks.
    /// </summary>
    public static TimeFrame ResolveSourceTimeFrame(string altBarFeedId)
    {
        var parts = altBarFeedId.Split('_');
        if (parts.Length != 3)
            throw new ArgumentException(
                $"Invalid alt-bar feed-id '{altBarFeedId}': expected positional grammar " +
                "'<TypeCode>_<SourceCode>_<Threshold>' (TRD §3.3).", nameof(altBarFeedId));

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

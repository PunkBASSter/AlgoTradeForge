using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

/// <summary>
/// Maps a <see cref="DataFeedSubscription"/> to its bar source: time bars → venue-published
/// <see cref="KlineVenueBarSource"/>, alt bars → tick-aggregated <see cref="TickAggregationBarSource"/>
/// with the threshold FROZEN from the feed-id (M6 parity), raw ticks → no bar source.
/// </summary>
public sealed class BarSourceResolver(
    BinanceWebSocketManager ws,
    IReplaySource replaySource,
    IBackfillRequester backfill,
    IInt64BarLoader warmupLoader,
    CatchupOptions options) : IBarSourceResolver
{
    public IBarSource? Resolve(
        string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        ArgumentException.ThrowIfNullOrEmpty(instrument);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(onBar);

        return subscription switch
        {
            TimeBarSubscription tb =>
                new KlineVenueBarSource(ws, instrument, tb.TimeFrame.Code, scale, onBar),

            AltBarSubscription ab => ResolveAltBar(ab, scale, onBar),

            // Raw-tick path: ticks reach strategies directly, no bar aggregation.
            TickSubscription => null,

            _ => throw new NotSupportedException(
                $"No live bar source for subscription kind '{subscription.GetType().Name}'."),
        };
    }

    private TickAggregationBarSource ResolveAltBar(
        AltBarSubscription ab, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        var feedId = AltBarFeedId.Parse(ab.FeedId);
        var frozenThreshold = ThresholdResolver.ResolveParsed(feedId.TypeCode, feedId.Threshold, scale);

        // Renko catch-up is disabled: cross-bar _pendingVolume is not reconstructed by
        // replay-from-last-completed-bar, so live Renko starts cold. Tracked follow-up.
        if (feedId.TypeCode == "Renko")
            return new TickAggregationBarSource(feedId.TypeCode, frozenThreshold, scale, onBar);

        var asset = ab.RequireAsset();
        var assetDir = AssetDirectoryName.From(asset);

        var policy = new RecoveryPolicy(options.BackfillBudget, options.PollInterval);
        var coordinator = new CatchupCoordinator(replaySource, backfill, policy);
        var request = new ReplayRequest(asset, "binance", SourceFeedId: "ticks", FromTs: 0);
        var altBarFeed = new DataFeedDescriptor(options.DataRoot, "binance", assetDir, ab.FeedId, DataFeedKind.AltBar);

        var plan = new CatchupPlan(coordinator, request, warmupLoader, altBarFeed, options.WarmupBarCount);
        return new TickAggregationBarSource(feedId.TypeCode, frozenThreshold, scale, onBar, catchup: plan);
    }
}

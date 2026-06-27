using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed class IbBarSourceResolver(
    IIbMarketDataSession session,
    IIbContractResolver contractResolver,
    IReplaySource replaySource,
    IBackfillRequester backfill,
    IInt64BarLoader warmupLoader,
    IbDataPlaneOptions options,
    CatchupOptions catchupOptions) : IBarSourceResolver
{
    public IBarSource? Resolve(
        string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        ArgumentException.ThrowIfNullOrEmpty(instrument);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(onBar);

        return subscription switch
        {
            TimeBarSubscription => BuildVenueBar(subscription.RequireAsset(), scale, onBar),
            AltBarSubscription ab => ResolveAltBar(ab, scale, onBar),
            TickSubscription => null,
            _ => throw new NotSupportedException(
                $"No IB live bar source for '{subscription.GetType().Name}'."),
        };
    }

    private IbVenueBarSource BuildVenueBar(Asset asset, ScaleContext scale, Action<Int64Bar, bool> onBar) =>
        new(session, contractResolver, asset.ToIbContract(), scale, onBar);

    private TickAggregationBarSource ResolveAltBar(
        AltBarSubscription ab, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        var feedId = AltBarFeedId.Parse(ab.FeedId);
        var frozenThreshold = ThresholdResolver.ResolveParsed(feedId.TypeCode, feedId.Threshold, scale);

        // Renko catch-up is disabled: cross-bar _pendingVolume is not reconstructed by replay.
        if (feedId.TypeCode == "Renko")
            return new TickAggregationBarSource(feedId.TypeCode, frozenThreshold, scale, onBar);

        var asset = ab.RequireAsset();
        var assetDir = AssetDirectoryName.From(asset);

        var policy = new RecoveryPolicy(catchupOptions.BackfillBudget, catchupOptions.PollInterval);
        var coordinator = new CatchupCoordinator(replaySource, backfill, policy);
        var request = new ReplayRequest(asset, "ib", SourceFeedId: "ticks", FromTs: 0);
        var altBarFeed = new DataFeedDescriptor(catchupOptions.DataRoot, "ib", assetDir, ab.FeedId, DataFeedKind.AltBar);

        var plan = new CatchupPlan(coordinator, request, warmupLoader, altBarFeed, catchupOptions.WarmupBarCount);
        return new TickAggregationBarSource(
            feedId.TypeCode, frozenThreshold, scale, onBar,
            catchup: plan, gate: new TimeWatermarkGate(options.MaxGapMs));
    }
}

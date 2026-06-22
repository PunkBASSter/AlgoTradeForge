using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class BarSourceResolverTests
{
    private static BinanceWebSocketManager FakeWs() =>
        // Constructor only stores config — no socket opens until Start()/SubscribeKline.
        new("wss://example.invalid", TimeSpan.FromSeconds(1), 1, NullLogger.Instance);

    private static BarSourceResolver Resolver() => new(FakeWs());

    [Fact]
    public void Resolve_AltBar_ReturnsTickFedSource_WithFrozenThreshold()
    {
        // 0.5-base (500m) is only valid where the asset's quantity step admits it (QuantityScale>=2).
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.001m);
        var sub = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");

        var source = Resolver().Resolve("BTCUSDT", sub, scale, (_, _) => { });

        var tickFed = Assert.IsType<TickAggregationBarSource>(source);
        Assert.IsAssignableFrom<ITickDrivenBarSource>(tickFed);

        // Threshold is FROZEN to exactly what the feed-id encodes (M6 parity).
        var expected = ThresholdResolver.ResolveParsed("EqV", new ThresholdValue(500L, 'm'), scale);
        Assert.Equal(500L, expected); // 0.5 base × QuantityScale(1000)
    }

    [Fact]
    public void Resolve_AltBar_EqD_FreezesQuoteAssetThreshold()
    {
        var scale = new ScaleContext(0.01m);
        var sub = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqD_1h_2M");

        var source = Resolver().Resolve("BTCUSDT", sub, scale, (_, _) => { });

        Assert.IsType<TickAggregationBarSource>(source);
        // EqD → quote_asset: $2M × ScaleFactor(100) = 200,000,000.
        Assert.Equal(200_000_000L, ThresholdResolver.ResolveParsed("EqD", new ThresholdValue(2L, 'M'), scale));
    }

    [Fact]
    public void Resolve_Tick_ReturnsNull()
    {
        var scale = new ScaleContext(0.01m);
        var sub = new TickSubscription("BTC", "ex", DataFeedRole.Primary);

        var source = Resolver().Resolve("BTCUSDT", sub, scale, (_, _) => { });

        Assert.Null(source);
    }

    [Fact]
    public void Resolve_TimeBar_ReturnsNonTickFedBarSource()
    {
        var scale = new ScaleContext(0.01m);
        var sub = new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse("1m"));

        var source = Resolver().Resolve("BTCUSDT", sub, scale, (_, _) => { });

        Assert.IsType<KlineVenueBarSource>(source);
        Assert.IsNotAssignableFrom<ITickDrivenBarSource>(source);
    }

    [Fact]
    public void Resolve_UnknownSubscriptionKind_Throws()
    {
        var scale = new ScaleContext(0.01m);
        var sub = new SideFeedSubscription("BTC", "ex", DataFeedRole.Side, "funding");

        Assert.Throws<NotSupportedException>(() => Resolver().Resolve("BTCUSDT", sub, scale, (_, _) => { }));
    }
}

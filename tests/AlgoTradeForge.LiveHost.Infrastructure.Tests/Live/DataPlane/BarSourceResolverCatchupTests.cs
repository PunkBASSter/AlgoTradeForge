using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class BarSourceResolverCatchupTests
{
    [Fact]
    public void AltBar_subscription_resolves_a_catchup_aware_tick_source()
    {
        var resolver = BarSourceResolverTestFactory.Create();
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.001m);

        var sub = new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_1m_500m")
        {
            Asset = Btc()
        };
        var source = resolver.Resolve("BTCUSDT", sub, scale, (_, _) => { });

        Assert.NotNull(source);
        Assert.IsType<TickAggregationBarSource>(source);
        // Catch-up wiring is exercised end-to-end in Task 10; here we assert the resolver produces
        // the tick-aggregation source for an alt-bar sub without throwing when catch-up deps are present.
    }

    private static Asset Btc() =>
        CryptoPerpetualAsset.Create("BTCUSDT", "binance", decimalDigits: 2);
}

internal static class BarSourceResolverTestFactory
{
    public static BarSourceResolver Create()
    {
        var ws = new BinanceWebSocketManager("wss://example.invalid", TimeSpan.FromSeconds(1), 1, NullLogger.Instance);
        return CreateWithWs(ws);
    }

    public static BarSourceResolver CreateWithWs(BinanceWebSocketManager ws)
    {
        var replaySource = Substitute.For<IReplaySource>();
        var backfill = Substitute.For<IBackfillRequester>();
        var warmupLoader = Substitute.For<IInt64BarLoader>();

        var options = new CatchupOptions
        {
            RelayKeyPrefix = "live-md",
            DataRoot = Path.GetTempPath(),
        };

        return new BarSourceResolver(ws, replaySource, backfill, warmupLoader, options);
    }
}

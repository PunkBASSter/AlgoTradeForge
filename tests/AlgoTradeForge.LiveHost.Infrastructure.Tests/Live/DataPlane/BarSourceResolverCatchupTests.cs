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

    // Fence test: Renko alt-bar starts cold (no catch-up plan), EqV starts warm (warmup bars seeded).
    // Asserts the resolver fence in BarSourceResolver.ResolveAltBar: Renko → no CatchupPlan,
    // all other alt-bar families → CatchupPlan with warmup seeding.
    [Fact]
    public async Task Renko_altbar_starts_cold_whereas_EqV_seeds_Recent()
    {
        var warmupBar = new Int64Bar(TimestampMs: 1_000, Open: 100, High: 110, Low: 90, Close: 105, Volume: 50);
        var warmupSeries = new TimeSeries<Int64Bar>(1);
        warmupSeries.Add(warmupBar);

        var replaySource = Substitute.For<IReplaySource>();
        replaySource.Replay(Arg.Any<ReplayRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable());

        var backfill = Substitute.For<IBackfillRequester>();
        var warmupLoader = Substitute.For<IInt64BarLoader>();
        warmupLoader.Load(Arg.Any<DataFeedDescriptor>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(warmupSeries));

        var ws = new BinanceWebSocketManager("wss://example.invalid", TimeSpan.FromSeconds(1), 1, NullLogger.Instance);
        var options = new CatchupOptions { RelayKeyPrefix = "live-md", DataRoot = Path.GetTempPath() };
        var resolver = new BarSourceResolver(ws, replaySource, backfill, warmupLoader, options);
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.001m);

        var renkoSub = new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "Renko_1m_50")
        {
            Asset = Btc()
        };
        var eqvSub = new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_1m_500m")
        {
            Asset = Btc()
        };

        var renkoSource = (TickAggregationBarSource)resolver.Resolve("BTCUSDT", renkoSub, scale, (_, _) => { })!;
        var eqvSource   = (TickAggregationBarSource)resolver.Resolve("BTCUSDT", eqvSub,   scale, (_, _) => { })!;

        var ct = TestContext.Current.CancellationToken;
        await renkoSource.Start(ct);
        await eqvSource.Start(ct);

        // Renko fence: cold start — no warmup bars seeded into Recent.
        Assert.Empty(renkoSource.Recent);

        // EqV contrast: warmup loader returned one bar → Recent is non-empty after Start.
        Assert.NotEmpty(eqvSource.Recent);
    }

    private static Asset Btc() =>
        CryptoPerpetualAsset.Create("BTCUSDT", "binance", decimalDigits: 2);

    private static async IAsyncEnumerable<TradeTick> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }
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

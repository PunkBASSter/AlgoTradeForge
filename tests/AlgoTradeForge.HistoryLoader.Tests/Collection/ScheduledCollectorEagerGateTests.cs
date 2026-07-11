using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

// Post-3a the scheduled cycle iterates ICollectionPlanSource.Current.Assets and collects a feed
// only when its plan collect value is "eager" (the lazy/on-demand feeds are archive-materialized
// on demand, not eagerly polled).
public sealed class ScheduledCollectorEagerGateTests
{
    private static (SymbolCollector Collector, IFeedCollector CandleCollector) BuildSymbolCollector()
    {
        var candleCollector = Substitute.For<IFeedCollector>();
        candleCollector.FeedName.Returns("candles");
        candleCollector.SupportsSpot.Returns(true);

        // Empty registry → CoverFromArchive is always a no-op (returns fromMs unchanged).
        var archiveBackfill = new ArchiveBackfillService(
            new ArchiveMaterializerRegistry([]),
            Substitute.For<IMonthCoverageCalculator>(),
            Substitute.For<IFeedStatusStore>(),
            Substitute.For<IHistoryIndex>(),
            new CollectionChangeNotifier(),
            new TestClock(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<ArchiveBackfillService>.Instance);

        var symbolCollector = new SymbolCollector(
            [candleCollector],
            archiveBackfill,
            Substitute.For<IHistoryIndex>(),
            new CollectionChangeNotifier(),
            NullLogger<SymbolCollector>.Instance);

        return (symbolCollector, candleCollector);
    }

    private static string TestDir() => Path.GetTempPath();

    private static (KlineCollectorService Service, IFeedCollector CandleCollector) Build(string collect)
    {
        var (symbolCollector, candleCollector) = BuildSymbolCollector();

        var holder = new CollectionPlanHolder();
        holder.Publish(new CollectionPlan(
            [CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed("candles", "1h", collect))],
            [], []));

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = TestDir() });

        var breaker = Substitute.For<ICollectionCircuitBreaker>();
        breaker.IsTripped.Returns(false);

        var service = new KlineCollectorService(
            symbolCollector, holder, breaker,
            Substitute.For<IHttpClientFactory>(), options,
            NullLogger<KlineCollectorService>.Instance);
        return (service, candleCollector);
    }

    [Fact]
    public async Task LazyFeed_OnDemand_IsSkippedByScheduledCycle()
    {
        var (service, candleCollector) = Build(collect: "on-demand");
        await service.CollectCycleAsync(TestContext.Current.CancellationToken);
        await candleCollector.DidNotReceiveWithAnyArgs().Collect(
            default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EagerFeed_IsCollected()
    {
        var (service, candleCollector) = Build(collect: "eager");
        await service.CollectCycleAsync(TestContext.Current.CancellationToken);
        await candleCollector.ReceivedWithAnyArgs(1).Collect(
            default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EmptyPlan_CollectsNothing()
    {
        var (symbolCollector, candleCollector) = BuildSymbolCollector();
        var holder = new CollectionPlanHolder(); // starts Empty

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = TestDir() });
        var breaker = Substitute.For<ICollectionCircuitBreaker>();
        breaker.IsTripped.Returns(false);

        var service = new KlineCollectorService(
            symbolCollector, holder, breaker,
            Substitute.For<IHttpClientFactory>(), options,
            NullLogger<KlineCollectorService>.Instance);

        await service.CollectCycleAsync(TestContext.Current.CancellationToken);

        await candleCollector.DidNotReceiveWithAnyArgs().Collect(
            default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }
}

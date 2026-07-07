using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

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
            Substitute.For<ISettingsWriter>(),
            new TestClock(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<ArchiveBackfillService>.Instance);

        var symbolCollector = new SymbolCollector(
            [candleCollector],
            archiveBackfill,
            Substitute.For<ISettingsWriter>(),
            NullLogger<SymbolCollector>.Instance);

        return (symbolCollector, candleCollector);
    }

    private static ArchiveMaterializerRegistry RegistryWithCandlesMaterializer()
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns("binance");
        m.FeedName.Returns("candles");
        m.Supports(Arg.Any<string>()).Returns(true);
        return new ArchiveMaterializerRegistry([m]);
    }

    private static string TestDir() => Path.GetTempPath();

    private static (KlineCollectorService Service, IFeedCollector CandleCollector) Build(
        bool eager, ArchiveMaterializerRegistry policyRegistry, string dataRoot)
    {
        var (symbolCollector, candleCollector) = BuildSymbolCollector();
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions
        {
            DataRoot = dataRoot,
            Assets =
            [
                new AssetCollectionConfig
                {
                    Symbol = "BTCUSDT", Type = "perpetual",
                    Feeds = [new FeedCollectionConfig { Name = "candles", Interval = "1h", Eager = eager }],
                },
            ],
        });
        var breaker = Substitute.For<ICollectionCircuitBreaker>();
        breaker.IsTripped.Returns(false);
        var service = new KlineCollectorService(
            symbolCollector, new CollectionPolicy(policyRegistry), breaker,
            Substitute.For<IHttpClientFactory>(), options,
            NullLogger<KlineCollectorService>.Instance);
        return (service, candleCollector);
    }

    [Fact]
    public async Task ReplenishableFeed_Lazy_IsSkippedByScheduledCycle()
    {
        var registry = RegistryWithCandlesMaterializer();
        var (service, candleCollector) = Build(eager: false, registry, dataRoot: TestDir());
        await service.CollectCycleAsync(TestContext.Current.CancellationToken);
        await candleCollector.DidNotReceiveWithAnyArgs().CollectAsync(
            default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReplenishableFeed_EagerOverride_IsCollected()
    {
        var registry = RegistryWithCandlesMaterializer();
        var (service, candleCollector) = Build(eager: true, registry, dataRoot: TestDir());
        await service.CollectCycleAsync(TestContext.Current.CancellationToken);
        await candleCollector.ReceivedWithAnyArgs(1).CollectAsync(
            default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IrreplaceableFeed_NoMaterializer_IsCollectedWithoutOverride()
    {
        var (service, candleCollector) = Build(
            eager: false, new ArchiveMaterializerRegistry([]), dataRoot: TestDir());
        await service.CollectCycleAsync(TestContext.Current.CancellationToken);
        await candleCollector.ReceivedWithAnyArgs(1).CollectAsync(
            default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }
}

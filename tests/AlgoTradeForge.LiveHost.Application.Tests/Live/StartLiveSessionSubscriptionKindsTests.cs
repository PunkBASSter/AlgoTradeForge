using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Application.Live;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live;

public class StartLiveSessionSubscriptionKindsTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2,
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private sealed class Harness
    {
        public StartLiveSessionCommandHandler Handler { get; set; } = null!;
        public LiveSessionConfig? Captured { get; set; }
    }

    private static ICollectionConfigStore CreateCollectionStore(params DataFeedSubscription[] collectedFeeds)
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig(collectedFeeds), "etag"));
        return store;
    }

    private static Harness NewHarness(params DataFeedSubscription[] collectedFeeds)
    {
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.Version.Returns("1.0");
        strategy.DataSubscriptions.Returns(new List<DataFeedSubscription>());

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create(Arg.Any<string>(), Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var harness = new Harness();

        var connector = Substitute.For<ILiveConnector>();
        connector.AddSessionAsync(Arg.Do<LiveSessionConfig>(c => harness.Captured = c), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(connector);

        var assetRepo = Substitute.For<IAssetRepository>();
        assetRepo.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>()).Returns(BtcUsdt);

        var spaceProvider = Substitute.For<IOptimizationSpaceProvider>();

        harness.Handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, new InMemoryLiveSessionStore(), assetRepo, spaceProvider,
            CreateCollectionStore(collectedFeeds));
        return harness;
    }

    [Fact]
    public async Task HandleAsync_AcceptsTickSubscription()
    {
        // Submits [Tick BTCUSDT Binance Primary] — collected set must contain a Tick for BTCUSDT.
        var h = NewHarness(new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Primary));
        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold",
            AccountName = "paper",
            DataSubscriptions = [new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Primary)],
        };

        var result = await h.Handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.SessionId);
        Assert.NotNull(h.Captured);
        var resolved = Assert.Single(h.Captured!.Subscriptions);
        Assert.Equal("ticks", resolved.FeedKey());
        Assert.Equal(BtcUsdt, resolved.Asset);
    }

    [Fact]
    public async Task HandleAsync_AcceptsAltBarSubscription()
    {
        // Submits [AltBar EqV_1m_500] — SourceCode="1m" -> collected set must contain TimeBar 1m.
        var h = NewHarness(new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m")));
        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold",
            AccountName = "paper",
            DataSubscriptions = [new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_500")],
        };

        var result = await h.Handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.SessionId);
        Assert.NotNull(h.Captured);
        var resolved = Assert.Single(h.Captured!.Subscriptions);
        Assert.Equal("EqV_1m_500", resolved.FeedKey());
    }

    [Fact]
    public async Task HandleAsync_ResolvesSubscriptions_PreservingKindAndOrder()
    {
        // Submits [TimeBar 1m, AltBar EqV_1m_500 (root=1m), Tick].
        // Collected must cover: TimeBar 1m (satisfies both TimeBar and EqV_1m_500 root) + Tick.
        var h = NewHarness(
            new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
            new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Primary));

        DataFeedSubscription[] raw =
        [
            new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
            new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Side, "EqV_1m_500"),
            new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Side),
        ];
        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold",
            AccountName = "paper",
            DataSubscriptions = raw,
        };

        await h.Handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.NotNull(h.Captured);
        Assert.Equal(raw.Length, h.Captured!.Subscriptions.Count);
        // One resolved list preserves the input kind + order; each now carries its resolved Asset.
        for (var i = 0; i < raw.Length; i++)
        {
            Assert.Equal(raw[i].GetType(), h.Captured.Subscriptions[i].GetType());
            Assert.Equal(raw[i].AssetName, h.Captured.Subscriptions[i].AssetName);
            Assert.NotNull(h.Captured.Subscriptions[i].Asset);
        }

        Assert.Equal(TimeFrame.Parse("1m"), ((TimeBarSubscription)h.Captured.Subscriptions[0]).TimeFrame);
        Assert.Equal("EqV_1m_500", h.Captured.Subscriptions[1].FeedKey());
        Assert.Equal("ticks", h.Captured.Subscriptions[2].FeedKey());
    }

    [Fact]
    public async Task HandleAsync_AssetNotFound_Throws()
    {
        var strategyFactory = Substitute.For<IStrategyFactory>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        var assetRepo = Substitute.For<IAssetRepository>();
        assetRepo.GetByNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Asset?)null);
        var spaceProvider = Substitute.For<IOptimizationSpaceProvider>();

        // Asset-not-found throws before collection check fires; any store works.
        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, new InMemoryLiveSessionStore(), assetRepo, spaceProvider,
            CreateCollectionStore());

        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold",
            AccountName = "paper",
            DataSubscriptions = [new TickSubscription("NOPE", "Binance", DataFeedRole.Primary)],
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
        Assert.Contains("not found", ex.Message);
    }
}

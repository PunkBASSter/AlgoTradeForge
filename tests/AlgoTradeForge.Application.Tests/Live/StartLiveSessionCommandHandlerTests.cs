using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Live;

public class StartLiveSessionCommandHandlerTests
{
    private static readonly Asset BtcUsdt = Asset.Crypto("BTCUSDT", "Binance",
        decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1),
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private static IInt64BarStrategy CreateStrategyWithSubscriptions(params DataSubscription[] subs)
    {
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription>(subs));
        return strategy;
    }

    [Fact]
    public async Task HandleAsync_CreatesSession_WithAccountName()
    {
        var strategy = CreateStrategyWithSubscriptions(
            new DataSubscription(BtcUsdt, TimeSpan.FromMinutes(1)));

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var sessionStore = new InMemoryLiveSessionStore();

        var handler = new StartLiveSessionCommandHandler(strategyFactory, accountManager, sessionStore);

        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold",
            InitialCash = 10000m,
            AccountName = "paper",
        };

        var result = await handler.HandleAsync(command);

        Assert.NotEqual(Guid.Empty, result.SessionId);
        await accountManager.Received(1).GetOrCreateAsync("paper", Arg.Any<CancellationToken>());
        await connector.Received(1).AddSessionAsync(
            Arg.Is<LiveSessionConfig>(c =>
                c.SessionId == result.SessionId &&
                c.AccountName == "paper" &&
                c.PrimaryAsset == BtcUsdt),
            Arg.Any<CancellationToken>());

        var entry = sessionStore.Get(result.SessionId);
        Assert.NotNull(entry);
        Assert.Equal("paper", entry.AccountName);
        Assert.Same(connector, entry.Connector);
    }

    [Fact]
    public async Task HandleAsync_SameAccountName_ReusesConnector()
    {
        var strategy = CreateStrategyWithSubscriptions(
            new DataSubscription(BtcUsdt, TimeSpan.FromMinutes(1)));

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var sessionStore = new InMemoryLiveSessionStore();
        var handler = new StartLiveSessionCommandHandler(strategyFactory, accountManager, sessionStore);

        var cmd1 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "paper",
        };
        var cmd2 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 5000m, AccountName = "paper",
        };

        var r1 = await handler.HandleAsync(cmd1);
        var r2 = await handler.HandleAsync(cmd2);

        Assert.NotEqual(r1.SessionId, r2.SessionId);
        await accountManager.Received(2).GetOrCreateAsync("paper", Arg.Any<CancellationToken>());
        Assert.Same(connector, sessionStore.Get(r1.SessionId)!.Connector);
        Assert.Same(connector, sessionStore.Get(r2.SessionId)!.Connector);
    }

    [Fact]
    public async Task HandleAsync_DifferentAccountNames_CreatesSeparateConnectors()
    {
        var strategy = CreateStrategyWithSubscriptions(
            new DataSubscription(BtcUsdt, TimeSpan.FromMinutes(1)));

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var paperConnector = Substitute.For<ILiveConnector>();
        var liveConnector = Substitute.For<ILiveConnector>();

        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(paperConnector);
        accountManager.GetOrCreateAsync("live", Arg.Any<CancellationToken>())
            .Returns(liveConnector);

        var sessionStore = new InMemoryLiveSessionStore();
        var handler = new StartLiveSessionCommandHandler(strategyFactory, accountManager, sessionStore);

        var cmd1 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "paper",
        };
        var cmd2 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "live",
        };

        var r1 = await handler.HandleAsync(cmd1);
        var r2 = await handler.HandleAsync(cmd2);

        Assert.Same(paperConnector, sessionStore.Get(r1.SessionId)!.Connector);
        Assert.Same(liveConnector, sessionStore.Get(r2.SessionId)!.Connector);
        Assert.NotSame(paperConnector, liveConnector);
    }

    [Fact]
    public async Task HandleAsync_NoSubscriptions_Throws()
    {
        var strategy = CreateStrategyWithSubscriptions(); // empty

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var handler = new StartLiveSessionCommandHandler(
            strategyFactory,
            Substitute.For<ILiveAccountManager>(),
            new InMemoryLiveSessionStore());

        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m,
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));
        Assert.Contains("at least one data subscription", ex.Message);
    }

    [Fact]
    public async Task HandleAsync_UsesPrimaryAssetFromFirstSubscription()
    {
        var ethUsdt = Asset.Crypto("ETHUSDT", "Binance", decimalDigits: 2);
        var strategy = CreateStrategyWithSubscriptions(
            new DataSubscription(ethUsdt, TimeSpan.FromMinutes(5)),
            new DataSubscription(BtcUsdt, TimeSpan.FromMinutes(1)));

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("MultiAsset", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, new InMemoryLiveSessionStore());

        var command = new StartLiveSessionCommand
        {
            StrategyName = "MultiAsset", InitialCash = 10000m,
        };

        await handler.HandleAsync(command);

        await connector.Received(1).AddSessionAsync(
            Arg.Is<LiveSessionConfig>(c => c.PrimaryAsset == ethUsdt),
            Arg.Any<CancellationToken>());
    }
}

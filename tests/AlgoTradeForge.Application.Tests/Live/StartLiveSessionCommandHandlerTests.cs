using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Live;

public class StartLiveSessionCommandHandlerTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1),
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private static readonly List<DataSubscriptionDto> DefaultSubscriptions =
    [
        new() { Asset = "BTCUSDT", Exchange = "Binance", TimeFrame = "00:01:00" },
    ];

    private static IInt64BarStrategy CreateStrategyWithSubscriptions(
        string version = "1.0", params DataSubscription[] subs)
    {
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.Version.Returns(version);
        strategy.DataSubscriptions.Returns(new List<DataSubscription>(subs));
        return strategy;
    }

    private static (IAssetRepository assetRepo, IOptimizationSpaceProvider spaceProvider) CreateDeps()
    {
        var assetRepo = Substitute.For<IAssetRepository>();
        assetRepo.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(BtcUsdt);

        var spaceProvider = Substitute.For<IOptimizationSpaceProvider>();
        return (assetRepo, spaceProvider);
    }

    [Fact]
    public async Task HandleAsync_CreatesSession_WithAccountName()
    {
        var strategy = CreateStrategyWithSubscriptions("1.0");

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var (assetRepo, spaceProvider) = CreateDeps();
        var sessionStore = new InMemoryLiveSessionStore();

        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, sessionStore, assetRepo, spaceProvider);

        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold",
            InitialCash = 10000m,
            AccountName = "paper",
            DataSubscriptions = DefaultSubscriptions,
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
        Assert.Equal("BuyAndHold", entry.StrategyName);
        Assert.Equal("1.0", entry.StrategyVersion);
    }

    [Fact]
    public async Task HandleAsync_AddsSubscriptionsFromCommand()
    {
        var strategy = CreateStrategyWithSubscriptions("1.0"); // empty subscriptions

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var (assetRepo, spaceProvider) = CreateDeps();
        var sessionStore = new InMemoryLiveSessionStore();

        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, sessionStore, assetRepo, spaceProvider);

        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold",
            InitialCash = 10000m,
            DataSubscriptions = DefaultSubscriptions,
        };

        await handler.HandleAsync(command);

        Assert.Single(strategy.DataSubscriptions);
        Assert.Equal(BtcUsdt, strategy.DataSubscriptions[0].Asset);
        Assert.Equal(TimeSpan.FromMinutes(1), strategy.DataSubscriptions[0].TimeFrame);
    }

    [Fact]
    public async Task HandleAsync_NoSubscriptions_Throws()
    {
        var handler = new StartLiveSessionCommandHandler(
            Substitute.For<IStrategyFactory>(),
            Substitute.For<ILiveAccountManager>(),
            new InMemoryLiveSessionStore(),
            Substitute.For<IAssetRepository>(),
            Substitute.For<IOptimizationSpaceProvider>());

        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m,
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));
        Assert.Contains("At least one data subscription", ex.Message);
    }

    [Fact]
    public async Task HandleAsync_ScalesQuoteAssetParams()
    {
        var strategy = CreateStrategyWithSubscriptions("1.0");

        var strategyFactory = Substitute.For<IStrategyFactory>();
        IDictionary<string, object>? capturedParams = null;
        strategyFactory.Create("TestStrategy", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(ci =>
            {
                capturedParams = ci.ArgAt<IDictionary<string, object>?>(2);
                return strategy;
            });

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var (assetRepo, spaceProvider) = CreateDeps();

        // Set up descriptor with a QuoteAsset axis
        var descriptor = Substitute.For<IOptimizationSpaceDescriptor>();
        descriptor.Axes.Returns(new List<ParameterAxis>
        {
            new NumericRangeAxis("MinimumThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset),
            new NumericRangeAxis("DzzDepth", 1, 10, 1, typeof(decimal)),
        });
        spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, new InMemoryLiveSessionStore(), assetRepo, spaceProvider);

        var command = new StartLiveSessionCommand
        {
            StrategyName = "TestStrategy",
            InitialCash = 10000m,
            DataSubscriptions = DefaultSubscriptions,
            StrategyParameters = new Dictionary<string, object>
            {
                ["MinimumThreshold"] = 100m,   // human-readable: $100
                ["DzzDepth"] = 5m,             // Raw param, not QuoteAsset
            },
        };

        await handler.HandleAsync(command);

        Assert.NotNull(capturedParams);
        // $100 / 0.01 tickSize = 10000L (tick units)
        Assert.Equal(10000L, capturedParams["MinimumThreshold"]);
        // DzzDepth should be unchanged (not QuoteAsset)
        Assert.Equal(5m, capturedParams["DzzDepth"]);
    }

    [Fact]
    public async Task HandleAsync_DifferentParams_AllowsBothSessions()
    {
        var strategy1 = CreateStrategyWithSubscriptions("1.0");
        var strategy2 = CreateStrategyWithSubscriptions("1.0");

        var callCount = 0;
        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(ci => callCount++ == 0 ? strategy1 : strategy2);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var (assetRepo, spaceProvider) = CreateDeps();
        var sessionStore = new InMemoryLiveSessionStore();
        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, sessionStore, assetRepo, spaceProvider);

        var cmd1 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "paper",
            DataSubscriptions = DefaultSubscriptions,
            StrategyParameters = new Dictionary<string, object> { ["lookback"] = 10 },
        };
        var cmd2 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 5000m, AccountName = "paper",
            DataSubscriptions = DefaultSubscriptions,
            StrategyParameters = new Dictionary<string, object> { ["lookback"] = 20 },
        };

        var r1 = await handler.HandleAsync(cmd1);
        var r2 = await handler.HandleAsync(cmd2);

        Assert.NotEqual(r1.SessionId, r2.SessionId);
        Assert.Same(connector, sessionStore.Get(r1.SessionId)!.Connector);
        Assert.Same(connector, sessionStore.Get(r2.SessionId)!.Connector);
    }

    [Fact]
    public async Task HandleAsync_DuplicateConfig_ThrowsInvalidOperationException()
    {
        var strategy = CreateStrategyWithSubscriptions("1.0");

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var (assetRepo, spaceProvider) = CreateDeps();
        var sessionStore = new InMemoryLiveSessionStore();
        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, sessionStore, assetRepo, spaceProvider);

        var command = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "paper",
            DataSubscriptions = DefaultSubscriptions,
        };

        await handler.HandleAsync(command);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(command));
        Assert.Contains("already running", ex.Message);
    }

    [Fact]
    public async Task HandleAsync_DifferentAccountNames_SameConfig_RejectsDuplicate()
    {
        var strategy = CreateStrategyWithSubscriptions("1.0");

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

        var (assetRepo, spaceProvider) = CreateDeps();
        var sessionStore = new InMemoryLiveSessionStore();
        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, sessionStore, assetRepo, spaceProvider);

        var cmd1 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "paper",
            DataSubscriptions = DefaultSubscriptions,
        };
        var cmd2 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "live",
            DataSubscriptions = DefaultSubscriptions,
        };

        await handler.HandleAsync(cmd1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(cmd2));
        Assert.Contains("already running", ex.Message);
    }

    [Fact]
    public async Task HandleAsync_DifferentStrategies_DifferentAccounts_AllowsBoth()
    {
        var strategy1 = CreateStrategyWithSubscriptions("1.0");
        var strategy2 = CreateStrategyWithSubscriptions("2.0");

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("BuyAndHold", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy1);
        strategyFactory.Create("MeanReversion", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy2);

        var paperConnector = Substitute.For<ILiveConnector>();
        var liveConnector = Substitute.For<ILiveConnector>();

        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(paperConnector);
        accountManager.GetOrCreateAsync("live", Arg.Any<CancellationToken>())
            .Returns(liveConnector);

        var (assetRepo, spaceProvider) = CreateDeps();
        var sessionStore = new InMemoryLiveSessionStore();
        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, sessionStore, assetRepo, spaceProvider);

        var cmd1 = new StartLiveSessionCommand
        {
            StrategyName = "BuyAndHold", InitialCash = 10000m, AccountName = "paper",
            DataSubscriptions = DefaultSubscriptions,
        };
        var cmd2 = new StartLiveSessionCommand
        {
            StrategyName = "MeanReversion", InitialCash = 10000m, AccountName = "live",
            DataSubscriptions = DefaultSubscriptions,
        };

        var r1 = await handler.HandleAsync(cmd1);
        var r2 = await handler.HandleAsync(cmd2);

        Assert.Same(paperConnector, sessionStore.Get(r1.SessionId)!.Connector);
        Assert.Same(liveConnector, sessionStore.Get(r2.SessionId)!.Connector);
    }

    [Fact]
    public async Task HandleAsync_UsesPrimaryAssetFromFirstSubscription()
    {
        var strategy = CreateStrategyWithSubscriptions("1.0");

        var ethUsdt = CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2);

        var strategyFactory = Substitute.For<IStrategyFactory>();
        strategyFactory.Create("MultiAsset", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        accountManager.GetOrCreateAsync("paper", Arg.Any<CancellationToken>())
            .Returns(connector);

        var assetRepo = Substitute.For<IAssetRepository>();
        assetRepo.GetByNameAsync("ETHUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(ethUsdt);
        assetRepo.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(BtcUsdt);

        var spaceProvider = Substitute.For<IOptimizationSpaceProvider>();

        var handler = new StartLiveSessionCommandHandler(
            strategyFactory, accountManager, new InMemoryLiveSessionStore(), assetRepo, spaceProvider);

        var command = new StartLiveSessionCommand
        {
            StrategyName = "MultiAsset", InitialCash = 10000m,
            DataSubscriptions =
            [
                new() { Asset = "ETHUSDT", Exchange = "Binance", TimeFrame = "00:05:00" },
                new() { Asset = "BTCUSDT", Exchange = "Binance", TimeFrame = "00:01:00" },
            ],
        };

        await handler.HandleAsync(command);

        await connector.Received(1).AddSessionAsync(
            Arg.Is<LiveSessionConfig>(c => c.PrimaryAsset == ethUsdt),
            Arg.Any<CancellationToken>());
    }
}

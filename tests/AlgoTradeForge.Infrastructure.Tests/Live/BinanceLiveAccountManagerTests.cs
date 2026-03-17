using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Infrastructure.Live.Binance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live;

public class BinanceLiveAccountManagerTests
{
    private static BinanceLiveOptions CreateOptions(params string[] accountNames)
    {
        var options = new BinanceLiveOptions();
        foreach (var name in accountNames)
        {
            options.Accounts[name] = new BinanceAccountConfig
            {
                RestUrl = "https://testnet.binance.vision",
                MarketStreamUrl = "wss://stream.testnet.binance.vision",
                WebSocketApiUrl = "wss://ws-api.testnet.binance.vision/ws-api/v3",
                ApiKey = "test-key",
                ApiSecret = "test-secret",
            };
        }
        return options;
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        var options = Options.Create(CreateOptions("paper"));
        var manager = new BinanceLiveAccountManager(
            options, Substitute.For<IOrderValidator>(), NullLoggerFactory.Instance);

        Assert.Null(manager.Get("nonexistent"));
    }

    [Fact]
    public async Task GetOrCreateAsync_UnconfiguredAccount_Throws()
    {
        var options = Options.Create(CreateOptions("paper"));
        var manager = new BinanceLiveAccountManager(
            options, Substitute.For<IOrderValidator>(), NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.GetOrCreateAsync("nonexistent", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GetActiveAccountNames_Empty_Initially()
    {
        var options = Options.Create(CreateOptions("paper"));
        var manager = new BinanceLiveAccountManager(
            options, Substitute.For<IOrderValidator>(), NullLoggerFactory.Instance);

        Assert.Empty(manager.GetActiveAccountNames());
    }

    [Fact]
    public async Task GetOrCreateAsync_DisposesOldConnector_WhenReplacingErrored()
    {
        var options = Options.Create(CreateOptions("paper"));
        var manager = new BinanceLiveAccountManager(
            options, Substitute.For<IOrderValidator>(), NullLoggerFactory.Instance);

        var erroredConnector = Substitute.For<ILiveConnector>();
        erroredConnector.Status.Returns(LiveSessionStatus.Error);
        erroredConnector.AccountName.Returns("paper");

        var newConnector = Substitute.For<ILiveConnector>();
        newConnector.Status.Returns(LiveSessionStatus.Running);
        newConnector.AccountName.Returns("paper");

        var callCount = 0;
        manager.ConnectorFactory = (name, config) =>
        {
            callCount++;
            return callCount == 1 ? erroredConnector : newConnector;
        };

        // First call creates errored connector
        erroredConnector.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var first = await manager.GetOrCreateAsync("paper", TestContext.Current.CancellationToken);
        Assert.Same(erroredConnector, first);

        // Simulate status changing to Error after connect
        erroredConnector.Status.Returns(LiveSessionStatus.Error);
        newConnector.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Second call should dispose the errored connector and create a new one
        var second = await manager.GetOrCreateAsync("paper", TestContext.Current.CancellationToken);
        Assert.Same(newConnector, second);
        await erroredConnector.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task GetOrCreateAsync_ConcurrentCalls_OnlyCreatesOneConnector()
    {
        var options = Options.Create(CreateOptions("paper"));
        var manager = new BinanceLiveAccountManager(
            options, Substitute.For<IOrderValidator>(), NullLoggerFactory.Instance);

        var factoryCallCount = 0;
        var connector = Substitute.For<ILiveConnector>();
        connector.Status.Returns(LiveSessionStatus.Running);
        connector.AccountName.Returns("paper");
        connector.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(50); // simulate connection latency
            });

        manager.ConnectorFactory = (name, config) =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return connector;
        };

        // Fire 10 concurrent calls
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => manager.GetOrCreateAsync("paper"))
            .ToList();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, factoryCallCount);
        Assert.All(results, r => Assert.Same(connector, r));
    }
}

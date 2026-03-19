using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain.Live;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Live;

public class StopLiveSessionCommandHandlerTests
{
    private static SessionDetails MakeDetails(string account, ILiveConnector connector) =>
        new(account, connector, "TestStrategy", "1.0", "Binance", "BTCUSDT", Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Stop_ExistingSession_ReturnsTrue()
    {
        var store = new InMemoryLiveSessionStore();
        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        var sessionId = Guid.NewGuid();
        store.TryAdd(sessionId, MakeDetails("paper", connector));

        var handler = new StopLiveSessionCommandHandler(store, accountManager);

        var result = await handler.HandleAsync(new StopLiveSessionCommand(sessionId), TestContext.Current.CancellationToken);

        Assert.True(result);
        await connector.Received(1).RemoveSessionAsync(sessionId, Arg.Any<CancellationToken>());
        Assert.Null(store.Get(sessionId));
    }

    [Fact]
    public async Task Stop_NonExistent_ReturnsFalse()
    {
        var store = new InMemoryLiveSessionStore();
        var accountManager = Substitute.For<ILiveAccountManager>();
        var handler = new StopLiveSessionCommandHandler(store, accountManager);

        var result = await handler.HandleAsync(new StopLiveSessionCommand(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task Stop_LastSession_DisposesConnector()
    {
        var store = new InMemoryLiveSessionStore();
        var connector = Substitute.For<ILiveConnector>();
        connector.SessionCount.Returns(0);
        var accountManager = Substitute.For<ILiveAccountManager>();
        var sessionId = Guid.NewGuid();
        store.TryAdd(sessionId, MakeDetails("paper", connector));

        var handler = new StopLiveSessionCommandHandler(store, accountManager);

        await handler.HandleAsync(new StopLiveSessionCommand(sessionId), TestContext.Current.CancellationToken);

        await accountManager.Received(1).TryRemoveAsync("paper", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_NotLastSession_KeepsConnector()
    {
        var store = new InMemoryLiveSessionStore();
        var connector = Substitute.For<ILiveConnector>();
        connector.SessionCount.Returns(1);
        var accountManager = Substitute.For<ILiveAccountManager>();
        var sessionId = Guid.NewGuid();
        store.TryAdd(sessionId, MakeDetails("paper", connector));

        var handler = new StopLiveSessionCommandHandler(store, accountManager);

        await handler.HandleAsync(new StopLiveSessionCommand(sessionId), TestContext.Current.CancellationToken);

        await accountManager.DidNotReceive().TryRemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

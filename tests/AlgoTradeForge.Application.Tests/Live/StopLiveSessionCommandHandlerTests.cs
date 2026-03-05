using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain.Live;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Live;

public class StopLiveSessionCommandHandlerTests
{
    [Fact]
    public async Task Stop_ExistingSession_ReturnsTrue()
    {
        var store = new InMemoryLiveSessionStore();
        var connector = Substitute.For<ILiveConnector>();
        var accountManager = Substitute.For<ILiveAccountManager>();
        var sessionId = Guid.NewGuid();
        store.Add(sessionId, "paper", connector);

        var handler = new StopLiveSessionCommandHandler(store, accountManager);

        var result = await handler.HandleAsync(new StopLiveSessionCommand(sessionId));

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

        var result = await handler.HandleAsync(new StopLiveSessionCommand(Guid.NewGuid()));

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
        store.Add(sessionId, "paper", connector);

        var handler = new StopLiveSessionCommandHandler(store, accountManager);

        await handler.HandleAsync(new StopLiveSessionCommand(sessionId));

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
        store.Add(sessionId, "paper", connector);

        var handler = new StopLiveSessionCommandHandler(store, accountManager);

        await handler.HandleAsync(new StopLiveSessionCommand(sessionId));

        await accountManager.DidNotReceive().TryRemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

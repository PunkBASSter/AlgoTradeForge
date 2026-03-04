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
        var sessionId = Guid.NewGuid();
        store.Add(sessionId, connector);

        var handler = new StopLiveSessionCommandHandler(store);

        var result = await handler.HandleAsync(new StopLiveSessionCommand(sessionId));

        Assert.True(result);
        await connector.Received(1).StopAsync(Arg.Any<CancellationToken>());
        Assert.Null(store.Get(sessionId));
    }

    [Fact]
    public async Task Stop_NonExistent_ReturnsFalse()
    {
        var store = new InMemoryLiveSessionStore();
        var handler = new StopLiveSessionCommandHandler(store);

        var result = await handler.HandleAsync(new StopLiveSessionCommand(Guid.NewGuid()));

        Assert.False(result);
    }
}

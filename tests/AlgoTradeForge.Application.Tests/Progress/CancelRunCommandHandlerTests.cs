using AlgoTradeForge.Application.Progress;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Progress;

public class CancelRunCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_RegisteredRun_ReturnsTrueAndCancels()
    {
        var registry = new InMemoryRunCancellationRegistry();
        var handler = new CancelRunCommandHandler(registry);
        var id = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        registry.Register(id, cts);

        var result = await handler.HandleAsync(new CancelRunCommand(id));

        Assert.True(result);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task HandleAsync_UnknownRun_ReturnsFalse()
    {
        var registry = new InMemoryRunCancellationRegistry();
        var handler = new CancelRunCommandHandler(registry);

        var result = await handler.HandleAsync(new CancelRunCommand(Guid.NewGuid()));

        Assert.False(result);
    }
}

using AlgoTradeForge.Domain.Events;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Events;

public class NullEventBusTests
{
    [Fact]
    public void Instance_IsNotNull()
    {
        Assert.NotNull(NullEventBus.Instance);
    }

    [Fact]
    public void Instance_ReturnsSameReference()
    {
        Assert.Same(NullEventBus.Instance, NullEventBus.Instance);
    }

    [Fact]
    public void Emit_DoesNotThrow()
    {
        var bus = NullEventBus.Instance;
        var evt = new BarEvent(
            DateTimeOffset.UtcNow, "test", "BTCUSDT", "1m",
            100, 110, 90, 105, 1000, true);

        var ex = Record.Exception(() => bus.Emit(evt));
        Assert.Null(ex);
    }

    [Fact]
    public void Instance_ImplementsIEventBus()
    {
        Assert.IsAssignableFrom<IEventBus>(NullEventBus.Instance);
    }
}

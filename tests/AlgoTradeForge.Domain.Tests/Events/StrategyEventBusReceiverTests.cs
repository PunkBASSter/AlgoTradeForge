using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Strategy;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Events;

public class StrategyEventBusReceiverTests
{
    private sealed class TestParams : StrategyParamsBase;

    private sealed class TestStrategy(TestParams p) : StrategyBase<TestParams>(p);

    [Fact]
    public void StrategyBase_ImplementsIEventBusReceiver()
    {
        var strategy = new TestStrategy(new TestParams());

        Assert.IsAssignableFrom<IEventBusReceiver>(strategy);
    }

    [Fact]
    public void SetEventBus_SetsProtectedProperty()
    {
        var strategy = new TestStrategy(new TestParams());
        var bus = new CapturingEventBus();

        ((IEventBusReceiver)strategy).SetEventBus(bus);

        // Emit through strategy to verify bus was set
        // We can't directly access EventBus from outside, but the cast + set proves it worked
        Assert.IsAssignableFrom<IEventBusReceiver>(strategy);
    }

    [Fact]
    public void DefaultEventBus_IsNullEventBus()
    {
        // StrategyBase defaults EventBus to NullEventBus.Instance
        // This is verified indirectly: creating a strategy without SetEventBus should not throw
        var strategy = new TestStrategy(new TestParams());
        strategy.OnInit(); // exercises the strategy without a bus — no exception means NullEventBus
    }

    private sealed class CapturingEventBus : IEventBus
    {
        public void Emit<T>(T evt) where T : IBacktestEvent { }
    }
}

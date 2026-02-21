using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Events;

public class StrategyEventBusReceiverTests
{
    private sealed class TestParams : StrategyParamsBase;

    private sealed class TestStrategy(TestParams p) : StrategyBase<TestParams>(p)
    {
        public override string Version => "1.0.0";
    }

    private sealed class SignalEmittingStrategy(TestParams p) : StrategyBase<TestParams>(p)
    {
        public override string Version => "1.0.0";
        public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            EmitSignal(bar.Timestamp, "BuySignal", subscription.Asset.Name, "Long", 0.85m, "MA crossover");
        }
    }

    [Fact]
    public void StrategyBase_ImplementsIEventBusReceiver()
    {
        var strategy = new TestStrategy(new TestParams());

        Assert.IsAssignableFrom<IEventBusReceiver>(strategy);
    }

    [Fact]
    public void SetEventBus_EmitSignal_RoutesToBus()
    {
        var bus = new CapturingEventBus();
        var asset = Asset.Equity("TEST", "XTEST");
        var sub = new DataSubscription(asset, TimeSpan.FromMinutes(1));
        var strategy = new SignalEmittingStrategy(new TestParams { DataSubscriptions = [sub] });

        ((IEventBusReceiver)strategy).SetEventBus(bus);

        var bar = new Int64Bar(0, 100, 110, 90, 105, 1000);
        strategy.OnBarComplete(bar, sub, null!);

        var signal = Assert.Single(bus.Events.OfType<SignalEvent>());
        Assert.Equal("BuySignal", signal.SignalName);
        Assert.Equal("TEST", signal.AssetName);
        Assert.Equal("Long", signal.Direction);
        Assert.Equal(0.85m, signal.Strength);
        Assert.Equal("MA crossover", signal.Reason);
        Assert.Equal(nameof(SignalEmittingStrategy), signal.Source);
    }

    [Fact]
    public void EmitSignal_WithNullReason_SetsReasonNull()
    {
        var bus = new CapturingEventBus();
        var asset = Asset.Equity("TEST", "XTEST");
        var sub = new DataSubscription(asset, TimeSpan.FromMinutes(1));

        // Use a strategy that emits without a reason
        var strategy = new NoReasonSignalStrategy(new TestParams { DataSubscriptions = [sub] });
        ((IEventBusReceiver)strategy).SetEventBus(bus);

        var bar = new Int64Bar(0, 100, 110, 90, 105, 1000);
        strategy.OnBarComplete(bar, sub, null!);

        var signal = Assert.Single(bus.Events.OfType<SignalEvent>());
        Assert.Null(signal.Reason);
    }

    [Fact]
    public void DefaultEventBus_IsNullEventBus()
    {
        // StrategyBase defaults EventBus to NullEventBus.Instance
        // EmitSignal should be a no-op — no exception means NullEventBus works
        var asset = Asset.Equity("TEST", "XTEST");
        var sub = new DataSubscription(asset, TimeSpan.FromMinutes(1));
        var strategy = new SignalEmittingStrategy(new TestParams { DataSubscriptions = [sub] });

        var bar = new Int64Bar(0, 100, 110, 90, 105, 1000);
        strategy.OnBarComplete(bar, sub, null!); // no bus set → NullEventBus → no-op
    }

    private sealed class NoReasonSignalStrategy(TestParams p) : StrategyBase<TestParams>(p)
    {
        public override string Version => "1.0.0";
        public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            EmitSignal(bar.Timestamp, "SellSignal", subscription.Asset.Name, "Short", 0.5m);
        }
    }

}

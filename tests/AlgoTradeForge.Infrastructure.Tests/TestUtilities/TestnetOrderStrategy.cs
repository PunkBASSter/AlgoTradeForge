using System.Collections.Concurrent;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Infrastructure.Tests.TestUtilities;

public sealed class TestnetOrderStrategyParams : StrategyParamsBase;

public sealed class TestnetOrderStrategy(TestnetOrderStrategyParams p)
    : StrategyBase<TestnetOrderStrategyParams>(p)
{
    public override string Version => "1.0.0";

    public ConcurrentBag<(Int64Bar Bar, DataSubscription Subscription)> ReceivedBars { get; } = [];
    public ConcurrentBag<Fill> ReceivedFills { get; } = [];

    public Action<IOrderContext>? OnNextBar;
    public TaskCompletionSource<Fill> NextFillTcs { get; private set; } = new();
    public TaskCompletionSource<Int64Bar> NextBarTcs { get; private set; } = new();

    public void ResetFillTcs() => NextFillTcs = new TaskCompletionSource<Fill>();
    public void ResetBarTcs() => NextBarTcs = new TaskCompletionSource<Int64Bar>();

    public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        ReceivedBars.Add((bar, subscription));
        NextBarTcs.TrySetResult(bar);

        var action = Interlocked.Exchange(ref OnNextBar, null);
        action?.Invoke(orders);
    }

    public override void OnTrade(Fill fill, Order order, IOrderContext orders)
    {
        ReceivedFills.Add(fill);
        NextFillTcs.TrySetResult(fill);
    }
}

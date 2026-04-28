using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Infrastructure.Tests.TestUtilities;

internal sealed class BuyOnFirstBarParams : StrategyParamsBase;

internal sealed class BuyOnFirstBarStrategy(BuyOnFirstBarParams p) : StrategyBase<BuyOnFirstBarParams>(p)
{
    public override string Version => "1.0.0";
    private bool _submitted;

    protected override void OnBarStartInner(Int64Bar bar, DataSubscription subscription)
    {
        if (_submitted) return;
        _submitted = true;
        Orders.Submit(new Order
        {
            Id = 0,
            Asset = subscription.Asset,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 1m,
        });
    }
}

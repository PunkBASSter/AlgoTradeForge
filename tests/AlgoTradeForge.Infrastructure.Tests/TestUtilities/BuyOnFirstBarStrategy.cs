using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Infrastructure.Tests.TestUtilities;

internal sealed class BuyOnFirstBarParams : StrategyParamsBase;

internal sealed class BuyOnFirstBarStrategy(BuyOnFirstBarParams p) : StrategyBase<BuyOnFirstBarParams>(p)
{
    public override string Version => "1.0.0";
    private bool _submitted;

    public override void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        if (_submitted) return;
        _submitted = true;
        orders.Submit(new Order
        {
            Id = 0,
            Asset = subscription.Asset,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 1m,
        });
    }
}

using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.BuyAndHold;

[StrategyKey("BuyAndHold")]
public sealed class BuyAndHoldStrategy(BuyAndHoldParams parameters, IIndicatorFactory? indicators = null)
    : StrategyBase<BuyAndHoldParams>(parameters, indicators)
{
    public override string Version => "1.0.0";

    private bool _entered;

    public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        if (_entered)
            return;

        _entered = true;

        var quantity = Math.Clamp(Params.Quantity,
            subscription.Asset.MinOrderQuantity,
            subscription.Asset.MaxOrderQuantity);
        quantity = subscription.Asset.RoundQuantityDown(quantity);

        if (quantity < subscription.Asset.MinOrderQuantity)
            return;

        orders.Submit(new Order
        {
            Id = 0,
            Asset = subscription.Asset,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = quantity
        });
    }
}

using AlgoTradeForge.Domain.Strategy.Subscriptions;
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

    protected override void OnBarCompleteInner(Int64Bar bar, DataFeedSubscription subscription)
    {
        if (_entered)
            return;

        _entered = true;

        var quantity = Math.Clamp(Params.Quantity,
            subscription.RequireAsset().MinOrderQuantity,
            subscription.RequireAsset().MaxOrderQuantity);
        quantity = subscription.RequireAsset().RoundQuantityDown(quantity);

        if (quantity < subscription.RequireAsset().MinOrderQuantity)
            return;

        Orders.Submit(new Order
        {
            Id = 0,
            Asset = subscription.RequireAsset(),
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = quantity
        });
    }
}

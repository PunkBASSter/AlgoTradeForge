using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public sealed class OrderValidator : IOrderValidator
{
    public string? ValidateSubmission(Order order)
    {
        var asset = order.Asset;
        if (order.Quantity < asset.MinOrderQuantity)
            return $"Quantity {order.Quantity} below minimum {asset.MinOrderQuantity}";
        if (order.Quantity > asset.MaxOrderQuantity)
            return $"Quantity {order.Quantity} above maximum {asset.MaxOrderQuantity}";
        if (asset.QuantityStepSize > 0m && order.Quantity % asset.QuantityStepSize != 0m)
            return $"Quantity {order.Quantity} not aligned to step size {asset.QuantityStepSize}";
        return null;
    }

    public string? ValidateSettlement(Order order, long fillPrice, Portfolio portfolio, BacktestOptions options)
    {
        if (order.Side == OrderSide.Buy)
        {
            var cost = (long)(fillPrice * order.Quantity * order.Asset.Multiplier) + options.CommissionPerTrade;
            if (cost > portfolio.Cash)
                return "Insufficient cash";
        }
        else
        {
            var currentPosition = portfolio.GetPosition(order.Asset.Name);
            var currentQty = currentPosition?.Quantity ?? 0m;
            var shortQuantity = Math.Max(0m, order.Quantity - Math.Max(0m, currentQty));
            if (shortQuantity > 0m)
            {
                var marginRequired = (long)(shortQuantity * fillPrice * order.Asset.Multiplier * order.Asset.ShortMarginRate)
                    + options.CommissionPerTrade;
                if (marginRequired > portfolio.Cash)
                    return "Insufficient margin for short";
            }
        }

        return null;
    }
}

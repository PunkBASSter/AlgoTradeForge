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
        return order.Asset.GetSettlementCalculator().ValidateSettlement(order, fillPrice, portfolio, options.CommissionPerTrade);
    }
}

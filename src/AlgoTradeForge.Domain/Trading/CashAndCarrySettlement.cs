namespace AlgoTradeForge.Domain.Trading;

/// <summary>
/// Spot/equity settlement: full notional exchange on buy/sell.
/// </summary>
public sealed class CashAndCarrySettlement : ISettlementCalculator
{
    public static readonly CashAndCarrySettlement Instance = new();

    public long ComputeCashDelta(Fill fill, long fillRealizedPnl)
    {
        var direction = fill.Side == OrderSide.Buy ? -1 : 1;
        return MoneyConvert.ToLong(fill.Price * fill.Quantity * fill.Asset.Multiplier * direction) - fill.Commission;
    }

    public long ComputePositionValue(Position position, long currentPrice) =>
        MoneyConvert.ToLong(position.Quantity * currentPrice * position.Asset.Multiplier);

    public string? ValidateSettlement(Order order, long fillPrice, Portfolio portfolio, long commission)
    {
        if (order.Side == OrderSide.Buy)
        {
            var cost = MoneyConvert.ToLong(fillPrice * order.Quantity * order.Asset.Multiplier) + commission;
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
                var marginRequired = MoneyConvert.ToLong(
                    shortQuantity * fillPrice * order.Asset.Multiplier * order.Asset.ShortMarginRate) + commission;
                if (marginRequired > portfolio.Cash)
                    return "Insufficient margin for short";
            }
        }

        return null;
    }
}

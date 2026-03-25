namespace AlgoTradeForge.Domain.Trading;

/// <summary>
/// Futures/perpetual settlement: only commission on open, realized PnL on close.
/// Shorts are symmetric with longs (same margin requirement).
/// </summary>
public sealed class MarginSettlement : ISettlementCalculator
{
    public static readonly MarginSettlement Instance = new();

    public long ComputeCashDelta(Fill fill, long fillRealizedPnl) =>
        fillRealizedPnl - fill.Commission;

    public long ComputePositionValue(Position position, long currentPrice) =>
        position.UnrealizedPnl(currentPrice);

    public string? ValidateSettlement(Order order, long fillPrice, Portfolio portfolio, long commission,
        IReadOnlyDictionary<string, long> lastPrices)
    {
        var marginRate = (order.Asset as IMarginAsset)?.MarginRequirement ?? 1.0m;

        // Only the portion that increases absolute exposure requires new margin.
        // Closing or reducing a position releases margin — it doesn't consume more.
        var currentPosition = portfolio.GetPosition(order.Asset.Name);
        var currentQty = currentPosition?.Quantity ?? 0m;
        var orderDirection = order.Side == OrderSide.Buy ? 1 : -1;
        var newQty = currentQty + order.Quantity * orderDirection;
        var increasingQty = Math.Max(0m, Math.Abs(newQty) - Math.Abs(currentQty));

        var marginRequired = MoneyConvert.ToLong(
            fillPrice * increasingQty * order.Asset.Multiplier * marginRate) + commission;
        if (marginRequired > portfolio.AvailableMargin(lastPrices))
            return "Insufficient margin";

        return null;
    }
}

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

    public string? ValidateSettlement(Order order, long fillPrice, Portfolio portfolio, long commission)
    {
        var marginRate = (order.Asset as IMarginAsset)?.MarginRequirement ?? 1.0m;
        var marginRequired = MoneyConvert.ToLong(
            fillPrice * order.Quantity * order.Asset.Multiplier * marginRate) + commission;
        if (marginRequired > portfolio.Cash)
            return "Insufficient margin";

        return null;
    }
}

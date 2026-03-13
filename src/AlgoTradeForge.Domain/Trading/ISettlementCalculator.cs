namespace AlgoTradeForge.Domain.Trading;

public interface ISettlementCalculator
{
    /// <summary>
    /// Computes the change in cash resulting from a fill.
    /// CashAndCarry: full notional exchange. Margin: only realized PnL flows.
    /// </summary>
    long ComputeCashDelta(Fill fill, long fillRealizedPnl);

    /// <summary>
    /// Computes the value of a position for equity calculation.
    /// CashAndCarry: notional value (qty * price * multiplier).
    /// Margin: unrealized PnL only.
    /// </summary>
    long ComputePositionValue(Position position, long currentPrice);

    /// <summary>
    /// Validates whether a fill can be settled. Returns null if valid, or an error message.
    /// </summary>
    string? ValidateSettlement(Order order, long fillPrice, Portfolio portfolio, long commission,
        IReadOnlyDictionary<string, long> lastPrices);
}

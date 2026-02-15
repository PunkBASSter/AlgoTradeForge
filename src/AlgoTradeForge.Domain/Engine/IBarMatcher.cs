using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public interface IBarMatcher
{
    Fill? TryFill(Order order, Int64Bar bar, BacktestOptions options);

    /// <summary>
    /// Evaluates SL/TP levels against a bar for a filled entry order.
    /// Returns an SL or TP fill if triggered, or null if neither is hit.
    /// When both SL and TP are in range, worst-case (SL) is returned by default.
    /// </summary>
    Fill? EvaluateSlTp(
        Order originalOrder,
        decimal entryPrice,
        decimal remainingQuantity,
        int nextTpIndex,
        Int64Bar bar,
        BacktestOptions options,
        out int hitTpIndex);
}

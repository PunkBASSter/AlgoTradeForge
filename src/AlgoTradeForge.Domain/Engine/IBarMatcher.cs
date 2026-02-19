using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public interface IBarMatcher
{
    long? GetFillPrice(Order order, Int64Bar bar, BacktestOptions options);

    /// <summary>
    /// Evaluates SL/TP levels against a bar for a filled entry order.
    /// Returns an SL or TP match result if triggered, or null if neither is hit.
    /// When both SL and TP are in range, worst-case (SL) is returned by default.
    /// </summary>
    SlTpMatchResult? EvaluateSlTp(
        Order originalOrder,
        long entryPrice,
        int nextTpIndex,
        Int64Bar bar,
        BacktestOptions options);
}

public readonly record struct SlTpMatchResult(
    long Price, decimal ClosurePercentage, bool IsStopLoss, int TpIndex);

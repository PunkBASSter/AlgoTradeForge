using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// Computes cash adjustments for auto-apply feed types (funding rates, swap rates, dividends).
/// All formulas operate in Domain long-money space — the <c>double</c> rate from the feed is
/// cast to <c>decimal</c> at the boundary and the result is rounded via <see cref="MoneyConvert.ToLong"/>.
/// </summary>
public static class AutoApplyHandler
{
    /// <summary>
    /// Returns the portfolio cash delta for a single auto-apply event.
    /// Positive = cash received, negative = cash paid.
    /// </summary>
    public static long ComputeCashDelta(AutoApplyType type, double rate, Position position, long lastPrice)
    {
        if (position.Quantity == 0m) return 0L;

        return type switch
        {
            AutoApplyType.FundingRate => ComputeFundingRate(rate, position, lastPrice),
            AutoApplyType.SwapRate => ComputeSwapRate(rate, position, lastPrice),
            AutoApplyType.Dividend => ComputeDividend(rate, position),
            AutoApplyType.MarkToMarket => 0L, // Requires previous mark tracking — future enhancement
            _ => 0L,
        };
    }

    /// <summary>
    /// Funding rate: cash delta = -(qty × lastPrice × rate × multiplier).
    /// Long pays when rate &gt; 0, short receives. Sign-symmetric.
    /// </summary>
    private static long ComputeFundingRate(double rate, Position position, long lastPrice) =>
        MoneyConvert.ToLong(-position.Quantity * (decimal)lastPrice * (decimal)rate * position.Asset.Multiplier);

    /// <summary>
    /// Swap rate: same as funding but annualized (÷ 365).
    /// </summary>
    private static long ComputeSwapRate(double rate, Position position, long lastPrice) =>
        MoneyConvert.ToLong(-position.Quantity * (decimal)lastPrice * (decimal)rate * position.Asset.Multiplier / 365m);

    /// <summary>
    /// Dividend: cash delta = qty × dividendPerShare.
    /// Long receives, short pays (Quantity is signed).
    /// </summary>
    private static long ComputeDividend(double rate, Position position) =>
        MoneyConvert.ToLong(position.Quantity * (decimal)rate);
}

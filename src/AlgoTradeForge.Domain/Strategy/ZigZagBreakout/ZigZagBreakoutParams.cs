using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.ZigZagBreakout;

public class ZigZagBreakoutParams : StrategyParamsBase
{
    [Optimizable(Min = 1, Max = 20, Step = 0.5)]
    public decimal DzzDepth { get; init; } = 5m;

    [Optimizable(Min = 5_000, Max = 50_000, Step = 5_000)]
    public long MinimumThreshold { get; init; } = 10_000L;

    [Optimizable(Min = 0.5, Max = 3, Step = 0.5)]
    public decimal RiskPercentPerTrade { get; init; } = 1m;

    [Optimizable(Min = 5, Max = 50, Step = 1)]
    public int AtrPeriod { get; init; } = 14;

    /// <summary>Minimum ATR in tick units. 0 means no minimum.</summary>
    [Optimizable(Min = 0, Max = 5000, Step = 50)]
    public long AtrMin { get; init; } = 0;

    /// <summary>Maximum ATR in tick units. 0 means no maximum.</summary>
    [Optimizable(Min = 0, Max = 50000, Step = 500)]
    public long AtrMax { get; init; } = 0;
}

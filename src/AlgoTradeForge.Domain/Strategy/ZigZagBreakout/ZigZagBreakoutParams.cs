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
}

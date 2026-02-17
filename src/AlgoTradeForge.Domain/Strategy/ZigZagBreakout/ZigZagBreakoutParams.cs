using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.ZigZagBreakout;

public class ZigZagBreakoutParams : StrategyParamsBase
{
    [Optimizable(Min = 1, Max = 20, Step = 0.5)]
    public decimal DzzDepth { get; init; } = 5m;

    [Optimizable(Min = 10, Max = 100, Step = 10)]
    public long MinimumThreshold { get; init; } = 10L;

    [Optimizable(Min = 0.5, Max = 3, Step = 0.5)]
    public decimal RiskPercentPerTrade { get; init; } = 1m;

    public decimal MinPositionSize { get; init; } = 0.01m;
    public decimal MaxPositionSize { get; init; } = 1000m;
    public decimal InitialCash { get; init; } = 10_000m;
}

namespace AlgoTradeForge.Domain.Strategy.ZigZagBreakout;

public class ZigZagBreakoutParams : StrategyParamsBase
{
    public decimal DzzDepth { get; init; } = 5m;
    public long MinimumThreshold { get; init; } = 10L;
    public decimal RiskPercentPerTrade { get; init; } = 1m;
    public decimal MinPositionSize { get; init; } = 0.01m;
    public decimal MaxPositionSize { get; init; } = 1000m;
    public decimal InitialCash { get; init; } = 10_000m;
}

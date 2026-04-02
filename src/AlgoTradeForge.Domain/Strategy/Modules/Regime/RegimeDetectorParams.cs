using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.Regime;

public sealed class RegimeDetectorParams : ModuleParamsBase
{
    [Optimizable(Min = 7, Max = 28, Step = 7)]
    public int AdxPeriod { get; init; } = 14;

    [Optimizable(Min = 15, Max = 35, Step = 5)]
    public double TrendThreshold { get; init; } = 25.0;
}

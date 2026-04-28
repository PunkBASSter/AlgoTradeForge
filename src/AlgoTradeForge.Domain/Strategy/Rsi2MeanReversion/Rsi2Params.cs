using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Filter;

namespace AlgoTradeForge.Domain.Strategy.Rsi2MeanReversion;

public sealed class Rsi2Params : ModularStrategyParamsBase
{
    [Optimizable(Min = 2, Max = 14, Step = 1)]
    public int RsiPeriod { get; init; } = 2;

    [Optimizable(Min = 5, Max = 30, Step = 5)]
    public double OversoldThreshold { get; init; } = 10;

    [Optimizable(Min = 70, Max = 95, Step = 5)]
    public double OverboughtThreshold { get; init; } = 90;

    [Optimizable(Min = 50, Max = 200, Step = 25)]
    public int TrendFilterPeriod { get; init; } = 200;

    public AtrVolatilityFilterParams AtrFilter { get; init; } = new();

    [Optimizable(Min = 5, Max = 50, Step = 5)]
    public int AtrPeriod { get; init; } = 14;

    [Optimizable(Min = 10, Max = 80, Step = 10)]
    public int SignalThreshold { get; init; } = 30;

    [Optimizable(Min = 1.0, Max = 5.0, Step = 0.5)]
    public double AtrStopMultiplier { get; init; } = 2.0;
}

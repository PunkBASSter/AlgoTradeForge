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
}

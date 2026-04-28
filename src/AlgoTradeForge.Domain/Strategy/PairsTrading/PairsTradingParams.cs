using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;

namespace AlgoTradeForge.Domain.Strategy.PairsTrading;

public sealed class PairsTradingParams : ModularStrategyParamsBase
{
    public override int RequiredSubscriptionCount => 2;

    public CrossAssetParams CrossAsset { get; init; } = new();

    [Optimizable(Min = 5, Max = 50, Step = 5)]
    public int AtrPeriod { get; init; } = 14;

    [Optimizable(Min = 10, Max = 80, Step = 10)]
    public int SignalThreshold { get; init; } = 30;

    [Optimizable(Min = 1.0, Max = 5.0, Step = 0.5)]
    public double AtrStopMultiplier { get; init; } = 3.0;
}

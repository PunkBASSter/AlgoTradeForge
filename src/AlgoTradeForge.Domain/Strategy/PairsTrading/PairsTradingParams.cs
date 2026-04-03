using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;

namespace AlgoTradeForge.Domain.Strategy.PairsTrading;

public sealed class PairsTradingParams : ModularStrategyParamsBase
{
    public CrossAssetParams CrossAsset { get; init; } = new();

    [Optimizable(Min = 5, Max = 50, Step = 5)]
    public int AtrPeriod { get; init; } = 14;
}

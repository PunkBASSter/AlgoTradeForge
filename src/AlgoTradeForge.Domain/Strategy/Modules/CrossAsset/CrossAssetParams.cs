using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;

public sealed class CrossAssetParams : ModuleParamsBase
{
    [Optimizable(Min = 20, Max = 120, Step = 10)]
    public int LookbackPeriod { get; init; } = 60;

    [Optimizable(Min = 1.0, Max = 3.0, Step = 0.25)]
    public double ZScoreEntryThreshold { get; init; } = 2.0;

    [Optimizable(Min = 0.0, Max = 1.5, Step = 0.25)]
    public double ZScoreExitThreshold { get; init; } = 0.5;
}

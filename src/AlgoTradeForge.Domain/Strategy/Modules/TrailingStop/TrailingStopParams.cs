using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;

public sealed class TrailingStopParams : ModuleParamsBase
{
    [Optimizable(Include = ["Atr", "Chandelier", "Donchian"])]
    public TrailingStopVariant Variant { get; init; } = TrailingStopVariant.Atr;

    [Optimizable(Min = 1.0, Max = 5.0, Step = 0.5)]
    public double AtrMultiplier { get; init; } = 2.0;

    [Optimizable(Min = 5, Max = 50, Step = 5)]
    public int AtrPeriod { get; init; } = 14;

    [Optimizable(Min = 5, Max = 50, Step = 5)]
    public int DonchianPeriod { get; init; } = 20;
}

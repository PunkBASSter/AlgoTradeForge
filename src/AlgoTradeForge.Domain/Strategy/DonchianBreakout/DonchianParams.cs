using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;

namespace AlgoTradeForge.Domain.Strategy.DonchianBreakout;

public sealed class DonchianParams : ModularStrategyParamsBase
{
    [Optimizable(Min = 10, Max = 55, Step = 5)]
    public int EntryPeriod { get; init; } = 20;

    [Optimizable(Min = 5, Max = 20, Step = 5)]
    public int ExitPeriod { get; init; } = 10;

    [Optimizable(Min = 5, Max = 50, Step = 5)]
    public int AtrPeriod { get; init; } = 14;

    [Optimizable(Min = 1.0, Max = 5.0, Step = 0.5)]
    public double AtrStopMultiplier { get; init; } = 2.0;

    public TrailingStopParams TrailingStopConfig { get; init; } = new();
    public RegimeDetectorParams RegimeDetectorConfig { get; init; } = new();
}

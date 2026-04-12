using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

public sealed class TimeBasedExitParams : ModuleParamsBase
{
    /// <summary>
    /// Maximum bars to hold a position before forcing exit. 0 = disabled.
    /// </summary>
    [Optimizable(Min = 5, Max = 100, Step = 5)]
    public int MaxHoldBars { get; init; } = 0;
}

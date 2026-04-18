namespace AlgoTradeForge.Domain.Strategy.Modules.MaxHoldBars;

public sealed class MaxHoldBarsParams : ModuleParamsBase
{
    public bool Enabled { get; init; }
    public int MaxBars { get; init; } = 20;
}

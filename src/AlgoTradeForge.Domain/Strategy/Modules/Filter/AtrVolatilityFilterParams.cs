using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.Filter;

public sealed class AtrVolatilityFilterParams : ModuleParamsBase
{
    [Optimizable(Min = 5, Max = 50, Step = 1)]
    public int Period { get; init; } = 14;

    // Tick units. 0 means no minimum.
    [Optimizable(Min = 0, Max = 5000, Step = 50)]
    public long MinAtr { get; init; } = 0;

    // Tick units. 0 means no maximum.
    [Optimizable(Min = 0, Max = 50000, Step = 500)]
    public long MaxAtr { get; init; } = 0;
}

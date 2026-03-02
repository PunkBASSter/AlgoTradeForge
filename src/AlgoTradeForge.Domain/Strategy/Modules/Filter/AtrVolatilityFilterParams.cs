using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.Filter;

public sealed class AtrVolatilityFilterParams : ModuleParamsBase
{
    [Optimizable(Min = 5, Max = 50, Step = 1)]
    public int Period { get; init; } = 14;

    // Quote-asset units. 0 means no minimum.
    [Optimizable(Min = 0, Max = 50, Step = 0.5, Unit = ParamUnit.QuoteAsset)]
    public long MinAtr { get; init; } = 0;

    // Quote-asset units. 0 means no maximum.
    [Optimizable(Min = 0, Max = 500, Step = 5, Unit = ParamUnit.QuoteAsset)]
    public long MaxAtr { get; init; } = 0;
}

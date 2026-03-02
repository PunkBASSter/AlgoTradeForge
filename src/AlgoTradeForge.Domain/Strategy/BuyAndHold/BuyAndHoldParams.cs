using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.BuyAndHold;

public class BuyAndHoldParams : StrategyParamsBase
{
    [Optimizable(Min = 0.1, Max = 10, Step = 0.1)]
    public decimal Quantity { get; init; } = 1m;
}

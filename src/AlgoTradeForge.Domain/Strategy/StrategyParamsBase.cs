namespace AlgoTradeForge.Domain.Strategy;

public class StrategyParamsBase
{
    public virtual IList<DataSubscription> DataSubscriptions { get; init; } = [];
}
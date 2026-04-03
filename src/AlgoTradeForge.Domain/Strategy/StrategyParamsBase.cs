namespace AlgoTradeForge.Domain.Strategy;

public class StrategyParamsBase
{
    public virtual IList<DataSubscription> DataSubscriptions { get; init; } = [];

    /// <summary>
    /// Minimum number of data subscriptions this strategy requires.
    /// Override in multi-asset strategies (e.g., pairs trading needs 2).
    /// </summary>
    public virtual int RequiredSubscriptionCount => 1;
}

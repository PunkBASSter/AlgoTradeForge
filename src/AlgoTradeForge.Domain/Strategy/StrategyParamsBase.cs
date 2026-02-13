namespace AlgoTradeForge.Domain.Strategy;

public class StrategyParamsBase
{
    //TODO: Resolve real history data by subscription and take period from backtest options
    public virtual IList<DataSubscription> DataSubscriptions { get; init; } = [];
}
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public class StrategyContextBase
{
    public Int64Bar CurrentBar { get; private set; }
    public DataFeedSubscription CurrentSubscription { get; private set; } = null!;

    public long Equity { get; private set; }
    public long Cash { get; private set; }

    internal void Update(Int64Bar bar, DataFeedSubscription subscription, IOrderContext orders)
    {
        CurrentBar = bar;
        CurrentSubscription = subscription;
        Cash = orders.Cash;
        Equity = Cash + orders.UsedMargin;
    }
}

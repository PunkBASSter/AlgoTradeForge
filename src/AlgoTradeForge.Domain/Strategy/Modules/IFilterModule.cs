using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public interface IFilterModule : IStrategyModule
{
    void Initialize(IIndicatorFactory factory, DataSubscription subscription);
    void Update(IReadOnlyList<Int64Bar> barHistory) { }
    int Evaluate(Int64Bar bar, OrderSide proposedSide);
}

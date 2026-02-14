using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

public interface IIntBarStrategy
{
    IList<DataSubscription> DataSubscriptions { get; }

    void OnBarComplete(TimeSeries<Int64Bar> context);
}

using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

public interface IIntBarStrategy
{
    void OnBarComplete(TimeSeries<Int64Bar> context);
}

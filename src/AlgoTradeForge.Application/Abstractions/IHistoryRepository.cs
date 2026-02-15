using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Abstractions;

public interface IHistoryRepository
{
    TimeSeries<Int64Bar> Load(DataSubscription subscription, DateOnly from, DateOnly to);
}

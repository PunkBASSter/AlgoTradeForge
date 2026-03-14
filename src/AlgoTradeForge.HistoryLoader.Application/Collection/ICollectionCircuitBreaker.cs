namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public interface ICollectionCircuitBreaker
{
    bool IsTripped { get; }
    void Trip(string reason);
    void Reset();
}

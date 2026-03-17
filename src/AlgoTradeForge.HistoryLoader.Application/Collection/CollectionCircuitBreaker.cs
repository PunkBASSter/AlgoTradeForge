using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class CollectionCircuitBreaker(ILogger<CollectionCircuitBreaker> logger) : ICollectionCircuitBreaker
{
    private int _tripped;

    public bool IsTripped => Volatile.Read(ref _tripped) == 1;

    public void Trip(string reason)
    {
        if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0)
            logger.LogCritical("Collection circuit breaker tripped: {Reason}", reason);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _tripped, 0);
        logger.LogInformation("Collection circuit breaker reset");
    }
}

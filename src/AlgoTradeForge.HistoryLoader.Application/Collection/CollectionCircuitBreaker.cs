using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class CollectionCircuitBreaker(ILogger<CollectionCircuitBreaker> logger) : ICollectionCircuitBreaker
{
    private volatile bool _tripped;

    public bool IsTripped => _tripped;

    public void Trip(string reason)
    {
        if (_tripped)
            return;

        _tripped = true;
        logger.LogCritical("Collection circuit breaker tripped: {Reason}", reason);
    }

    public void Reset()
    {
        _tripped = false;
        logger.LogInformation("Collection circuit breaker reset");
    }
}

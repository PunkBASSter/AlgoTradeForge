using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class CollectionCircuitBreaker(ILogger<CollectionCircuitBreaker> logger) : ICollectionCircuitBreaker
{
    private int _tripped;
    private TripReason? _reason;

    public bool IsTripped => Volatile.Read(ref _tripped) == 1;

    public TripReason? Reason => IsTripped ? _reason : null;

    public void Trip(string reason, TripReason kind = TripReason.Ban)
    {
        if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0)
        {
            _reason = kind;
            logger.LogCritical("Collection circuit breaker tripped ({Kind}): {Reason}", kind, reason);
        }
        else if (kind == TripReason.Ban && _reason == TripReason.Network)
        {
            // Upgrade from Network to Ban — ban takes precedence
            _reason = TripReason.Ban;
            logger.LogCritical("Circuit breaker upgraded to Ban: {Reason}", reason);
        }
    }

    public void Reset()
    {
        _reason = null;
        Interlocked.Exchange(ref _tripped, 0);
        logger.LogInformation("Collection circuit breaker reset");
    }
}

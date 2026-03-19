namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public enum TripReason
{
    /// <summary>IP ban (HTTP 418) — requires manual reset.</summary>
    Ban,

    /// <summary>Network unreachable (DNS/connection failure) — auto-resettable via probe.</summary>
    Network
}

public interface ICollectionCircuitBreaker
{
    bool IsTripped { get; }
    TripReason? Reason { get; }
    bool IsAutoResettable => Reason == TripReason.Network;
    void Trip(string reason, TripReason kind = TripReason.Ban);
    void Reset();
}

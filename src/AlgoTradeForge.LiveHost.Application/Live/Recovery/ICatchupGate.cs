using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Venue-agnostic catch-up gate: admits ticks, dedupes the replay→live overlap, and flags a gap
/// (the disconnect signal). Time-based — exposes only <see cref="LastTimestampMs"/>, never a
/// sequence — so the coordinator stays venue-neutral. HOW a gap is detected is the impl's concern
/// (sequence for crypto, connection events for IB).
/// </summary>
public interface ICatchupGate
{
    bool Seeded { get; }
    long LastTimestampMs { get; }
    TickAdmission Admit(in TradeTick tick);
    void Reseed(in TradeTick tick);
}

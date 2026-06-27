using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Time-venue <see cref="ICatchupGate"/> for venues without a contiguous per-instrument sequence
/// (IB tick-by-tick). Dedupes strictly-older ticks (the replay→live overlap) and reports a
/// <see cref="TickAdmission.Gap"/> when the inter-tick time jump exceeds <paramref name="maxGapMs"/>
/// — the disconnect signal. A quiet market can false-positive; the historical-backfill requester
/// makes a spurious gap harmless (it re-fetches and dedupes to nothing). Single-threaded: the
/// owning bar source serializes admission.
/// </summary>
public sealed class TimeWatermarkGate(long maxGapMs) : ICatchupGate
{
    private long _lastTs;
    public bool Seeded { get; private set; }
    public long LastTimestampMs => _lastTs;

    public TickAdmission Admit(in TradeTick tick)
    {
        if (!Seeded) { Seeded = true; _lastTs = tick.TimestampMs; return TickAdmission.Accept; }
        if (tick.TimestampMs < _lastTs) return TickAdmission.Duplicate;
        if (tick.TimestampMs - _lastTs > maxGapMs) return TickAdmission.Gap;
        _lastTs = tick.TimestampMs;
        return TickAdmission.Accept;
    }

    public void Reseed(in TradeTick tick) { Seeded = true; _lastTs = tick.TimestampMs; }
}

using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Sequence-venue <see cref="ICatchupGate"/>: monotonic dedupe + gap detection on venue aggId
/// (<see cref="TradeTick.Sequence"/>), reusable by any venue with contiguous per-instrument ids
/// (Binance today). Both replayed and live ticks pass through one instance so the replay→live
/// overlap self-dedupes; a non-contiguous jump is reported as <see cref="TickAdmission.Gap"/>.
/// Single-threaded: the owning bar source serializes admission on the processing path.
/// </summary>
public sealed class SequenceWatermarkGate : ICatchupGate
{
    private long _last;
    public bool Seeded { get; private set; }
    public long LastSequence => _last;
    public long LastTimestampMs { get; private set; }

    public TickAdmission Admit(in TradeTick tick)
    {
        if (!Seeded)
        {
            Seeded = true;
            _last = tick.Sequence;
            LastTimestampMs = tick.TimestampMs;
            return TickAdmission.Accept;
        }
        if (tick.Sequence <= _last) return TickAdmission.Duplicate;
        if (tick.Sequence == _last + 1)
        {
            _last = tick.Sequence;
            LastTimestampMs = tick.TimestampMs;
            return TickAdmission.Accept;
        }
        return TickAdmission.Gap;
    }

    public void Reseed(in TradeTick tick)
    {
        Seeded = true;
        _last = tick.Sequence;
        LastTimestampMs = tick.TimestampMs;
    }
}

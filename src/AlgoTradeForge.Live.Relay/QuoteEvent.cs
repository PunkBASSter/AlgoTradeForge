using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed record QuoteEvent(string Instrument, QuoteTick Quote) : IMarketEvent
{
    public long TimestampMs => Quote.TimestampMs;
}

using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed record TradeEvent(string Instrument, TradeTick Tick) : IMarketEvent
{
    public long TimestampMs => Tick.TimestampMs;
}

namespace AlgoTradeForge.Live.Relay;

public interface IVenueConnector
{
    string Venue { get; }
    MarketDataSessionPolicy SessionPolicy { get; }
    (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument);
    IAsyncEnumerable<IMarketEvent> Stream(IReadOnlyList<string> instruments, CancellationToken ct = default);
}

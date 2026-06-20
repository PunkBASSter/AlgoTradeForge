namespace AlgoTradeForge.Live.Relay;

public interface IVenueConnector
{
    string Venue { get; }
    MarketDataSessionPolicy SessionPolicy { get; }
    IAsyncEnumerable<IMarketEvent> Stream(IReadOnlyList<string> instruments, CancellationToken ct = default);
}

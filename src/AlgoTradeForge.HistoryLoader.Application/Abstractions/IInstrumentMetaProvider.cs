namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IInstrumentMetaProvider
{
    /// <summary>Fetches exchangeInfo (spot + futures) and upserts instrument_meta when the last
    /// fetch is older than 24h. In-memory last-fetch timestamps make repeat calls free — a group
    /// symbol absent from a fresh response stays absent (blocked) until the next TTL expiry.</summary>
    Task EnsureFresh(string exchange, CancellationToken ct = default);
}

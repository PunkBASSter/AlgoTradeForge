using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ISpotDataFetcher
{
    string[]? CandleExtColumns { get; }

    IAsyncEnumerable<CandleRecord> FetchKlinesAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);
}

using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ICandleFetcher
{
    string[]? CandleExtColumns { get; }

    IAsyncEnumerable<CandleRecord> FetchCandlesAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);
}

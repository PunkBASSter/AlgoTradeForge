using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IMarkPriceCandleFetcher
{
    IAsyncEnumerable<CandleRecord> FetchMarkPriceCandlesAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);
}

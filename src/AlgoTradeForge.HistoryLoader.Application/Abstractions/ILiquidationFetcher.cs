using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ILiquidationFetcher
{
    IAsyncEnumerable<FeedRecord> FetchLiquidationsAsync(
        string symbol, long fromMs, long toMs, CancellationToken ct);
}

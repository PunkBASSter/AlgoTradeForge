using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFuturesDataFetcher
{
    IAsyncEnumerable<KlineRecord> FetchKlinesAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<KlineRecord> FetchMarkPriceKlinesAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchFundingRatesAsync(
        string symbol, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchOpenInterestAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchGlobalLongShortRatioAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchTopAccountRatioAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchTakerVolumeAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchTopPositionRatioAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct);

    IAsyncEnumerable<FeedRecord> FetchLiquidationsAsync(
        string symbol, long fromMs, long toMs, CancellationToken ct);
}

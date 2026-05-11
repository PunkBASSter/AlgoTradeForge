using System.Runtime.CompilerServices;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    /// <summary>
    /// Fetches index price klines (the underlying spot composite index Binance uses to
    /// compute mark price and funding) for the given <paramref name="symbol"/> pair and
    /// <paramref name="interval"/> over [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// Returns <see cref="FeedRecord"/> with OHLC as doubles (no volume).
    /// </summary>
    /// <remarks>
    /// The endpoint takes a <c>pair</c> query param (the underlying spot symbol, e.g. BTCUSDT)
    /// rather than a contract symbol. Caller passes the bare pair as <paramref name="symbol"/>;
    /// for USDT-M perps the contract symbol equals the pair, so this is a no-op mapping today.
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchIndexPriceFeedAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cursor = fromMs;

        while (cursor < toMs)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await FetchIndexPriceKlineBatchWithRetryAsync(symbol, interval, cursor, toMs, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < KlineLimit)
                yield break;

            cursor = batch[^1].TimestampMs + 1;
        }
    }

    private Task<FeedRecord[]> FetchIndexPriceKlineBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildIndexPriceKlineUrl(symbol, interval, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, KlineWeight, ParseMarkPriceKlineBatch, ct);
    }

    private string BuildIndexPriceKlineUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/fapi/v1/indexPriceKlines" +
        $"?pair={symbol}&interval={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}";
}

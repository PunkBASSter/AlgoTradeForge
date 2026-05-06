using System.Runtime.CompilerServices;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    /// <summary>
    /// Fetches premium index klines (mark - index) from the Binance USDT-M Futures API
    /// for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the half-open time range [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// Returns <see cref="FeedRecord"/> with OHLC as doubles (no volume), parsed via
    /// <see cref="ParseMarkPriceKlineBatch"/> since the response shape is identical.
    /// </summary>
    public async IAsyncEnumerable<FeedRecord> FetchPremiumIndexFeedAsync(
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

            var batch = await FetchPremiumIndexKlineBatchWithRetryAsync(symbol, interval, cursor, toMs, ct)
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

    private Task<FeedRecord[]> FetchPremiumIndexKlineBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildPremiumIndexKlineUrl(symbol, interval, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, KlineWeight, ParseMarkPriceKlineBatch, ct);
    }

    private string BuildPremiumIndexKlineUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/fapi/v1/premiumIndexKlines" +
        $"?symbol={symbol}&interval={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}";
}

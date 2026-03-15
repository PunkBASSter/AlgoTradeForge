using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    // -------------------------------------------------------------------------
    // Mark price klines share the same limit/weight as regular klines.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches mark price klines from the Binance USDT-M Futures API
    /// for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the half-open time range [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// Returns <see cref="FeedRecord"/> with OHLC as doubles (no volume).
    /// </summary>
    public async IAsyncEnumerable<FeedRecord> FetchMarkPriceFeedAsync(
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

            var batch = await FetchMarkPriceKlineBatchWithRetryAsync(symbol, interval, cursor, toMs, ct)
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

    // -------------------------------------------------------------------------
    // Private helpers — mark price klines
    // -------------------------------------------------------------------------

    private Task<FeedRecord[]> FetchMarkPriceKlineBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildMarkPriceKlineUrl(symbol, interval, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, KlineWeight, ParseMarkPriceKlineBatch, ct);
    }

    private string BuildMarkPriceKlineUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/fapi/v1/markPriceKlines" +
        $"?symbol={symbol}&interval={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}";

    private static FeedRecord[] ParseMarkPriceKlineBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var row = element.EnumerateArray().ToArray();

            long timestampMs = row[0].GetInt64();

            if (!BinanceJsonHelper.TryParseDouble(row[1], out var open))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(row[2], out var high))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(row[3], out var low))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(row[4], out var close))
                continue;

            records[i++] = new FeedRecord(timestampMs, [open, high, low, close]);
        }

        return records.AsSpan(0, i).ToArray();
    }
}

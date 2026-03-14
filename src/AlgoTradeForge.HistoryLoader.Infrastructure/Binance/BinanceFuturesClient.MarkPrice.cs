using System.Globalization;
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
    /// </summary>
    /// <remarks>
    /// Each <see cref="CandleRecord"/> contains mark price OHLC values.
    /// Volume is always 0 and <see cref="CandleRecord.ExtValues"/> is null
    /// for mark price klines.
    /// </remarks>
    public async IAsyncEnumerable<CandleRecord> FetchMarkPriceKlinesAsync(
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

    private Task<CandleRecord[]> FetchMarkPriceKlineBatchWithRetryAsync(
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

    private static CandleRecord[] ParseMarkPriceKlineBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new CandleRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var row = element.EnumerateArray().ToArray();

            long    timestampMs = row[0].GetInt64();
            decimal open        = decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture);
            decimal high        = decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture);
            decimal low         = decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture);
            decimal close       = decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture);
            // Volume fields are always "0" for mark price klines.

            records[i++] = new CandleRecord(
                timestampMs, open, high, low, close, Volume: 0m);
        }

        return records;
    }
}

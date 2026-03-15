using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int OiLimit = 500;
    private const int OiWeight = 1;

    /// <summary>
    /// Fetches open interest history from the Binance USDT-M Futures data API
    /// for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the time range [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    /// <remarks>
    /// Each <see cref="FeedRecord"/> contains two values:
    /// sumOpenInterest (index 0) and sumOpenInterestValue (index 1).
    /// Columns: <c>["oi", "oi_usd"]</c>.
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchOpenInterestAsync(
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

            var batch = await FetchOiBatchWithRetryAsync(symbol, interval, cursor, toMs, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < OiLimit)
                yield break;

            cursor = batch[^1].TimestampMs + 1;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — open interest
    // -------------------------------------------------------------------------

    private Task<FeedRecord[]> FetchOiBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildOiUrl(symbol, interval, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, OiWeight, ParseOiBatch, ct);
    }

    private string BuildOiUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/futures/data/openInterestHist" +
        $"?symbol={symbol}&period={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={OiLimit}";

    private static FeedRecord[] ParseOiBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            long timestamp = element.GetProperty("timestamp").GetInt64();
            double sumOi = double.Parse(
                BinanceJsonHelper.ParseRequiredString(element, "sumOpenInterest"),
                CultureInfo.InvariantCulture);
            double sumOiValue = double.Parse(
                BinanceJsonHelper.ParseRequiredString(element, "sumOpenInterestValue"),
                CultureInfo.InvariantCulture);

            records[i++] = new FeedRecord(timestamp, [sumOi, sumOiValue]);
        }

        return records;
    }
}

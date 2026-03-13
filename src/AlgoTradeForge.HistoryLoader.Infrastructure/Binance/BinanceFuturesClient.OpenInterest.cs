using System.Globalization;
using System.Net;
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
    /// over the time range [<paramref name="fromMs"/>, <paramref name="toMs"/>].
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

        while (cursor <= toMs)
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

    private async Task<FeedRecord[]> FetchOiBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await rateLimiter.AcquireAsync(OiWeight, ct).ConfigureAwait(false);
            await Task.Delay(options.RequestDelayMs, ct).ConfigureAwait(false);

            var url = BuildOiUrl(symbol, interval, fromMs, toMs);
            using var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == MaxRetries)
                    throw new HttpRequestException($"Binance rate limit exceeded after {MaxRetries} retries (HTTP 429).");

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == (HttpStatusCode)418)
                throw new HttpRequestException("IP banned by Binance (HTTP 418).");

            if ((int)response.StatusCode >= 500 && (int)response.StatusCode <= 599)
            {
                if (attempt == MaxRetries)
                    throw new HttpRequestException(
                        $"Binance server error after {MaxRetries} retries (HTTP {(int)response.StatusCode}).");

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseOiBatch(json);
        }

        // Unreachable — loop always returns or throws within MaxRetries iterations.
        throw new InvalidOperationException("Unexpected state in FetchOiBatchWithRetryAsync.");
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
                element.GetProperty("sumOpenInterest").GetString()!,
                CultureInfo.InvariantCulture);
            double sumOiValue = double.Parse(
                element.GetProperty("sumOpenInterestValue").GetString()!,
                CultureInfo.InvariantCulture);

            records[i++] = new FeedRecord(timestamp, [sumOi, sumOiValue]);
        }

        return records;
    }
}

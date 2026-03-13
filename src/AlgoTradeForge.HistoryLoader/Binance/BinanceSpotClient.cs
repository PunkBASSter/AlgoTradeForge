using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.RateLimiting;

namespace AlgoTradeForge.HistoryLoader.Binance;

internal sealed class BinanceSpotClient(HttpClient httpClient, BinanceOptions options, SourceRateLimiter rateLimiter)
{
    private const int KlineLimit = 1000;
    private const int KlineWeight = 2;
    private const int MaxRetries = 3;

    // -------------------------------------------------------------------------
    // Klines
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches klines (candlestick bars) from the Binance Spot API
    /// for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the half-open time range [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    public async IAsyncEnumerable<KlineRecord> FetchKlinesAsync(
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

            var batch = await FetchKlineBatchWithRetryAsync(symbol, interval, cursor, toMs, ct)
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
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<KlineRecord[]> FetchKlineBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await rateLimiter.AcquireAsync(KlineWeight, ct).ConfigureAwait(false);
            await Task.Delay(options.RequestDelayMs, ct).ConfigureAwait(false);

            var url = BuildKlineUrl(symbol, interval, fromMs, toMs);
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
            return ParseKlineBatch(json);
        }

        // Unreachable — loop always returns or throws within MaxRetries iterations.
        throw new InvalidOperationException("Unexpected state in FetchKlineBatchWithRetryAsync.");
    }

    private string BuildKlineUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.SpotBaseUrl}/api/v3/klines" +
        $"?symbol={symbol}&interval={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}";

    private static KlineRecord[] ParseKlineBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new KlineRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var row = element.EnumerateArray().ToArray();

            long timestampMs = row[0].GetInt64();
            decimal open     = decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture);
            decimal high     = decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture);
            decimal low      = decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture);
            decimal close    = decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture);
            decimal volume   = decimal.Parse(row[5].GetString()!, CultureInfo.InvariantCulture);
            // row[6] = close time (unused directly)
            decimal quoteVolume      = decimal.Parse(row[7].GetString()!, CultureInfo.InvariantCulture);
            int     tradeCount       = row[8].GetInt32();
            decimal takerBuyVolume   = decimal.Parse(row[9].GetString()!,  CultureInfo.InvariantCulture);
            decimal takerBuyQuoteVol = decimal.Parse(row[10].GetString()!, CultureInfo.InvariantCulture);

            records[i++] = new KlineRecord(
                timestampMs, open, high, low, close, volume,
                quoteVolume, tradeCount, takerBuyVolume, takerBuyQuoteVol);
        }

        return records;
    }
}

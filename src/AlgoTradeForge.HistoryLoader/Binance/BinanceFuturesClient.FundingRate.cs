using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.RateLimiting;

namespace AlgoTradeForge.HistoryLoader.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int FundingRateLimit = 1000;
    private const int FundingRateWeight = 1;

    /// <summary>
    /// Fetches funding rates from the Binance USDT-M Futures API for the given
    /// <paramref name="symbol"/> over the time range
    /// [<paramref name="fromMs"/>, <paramref name="toMs"/>].
    /// </summary>
    /// <remarks>
    /// Each <see cref="FeedRecord"/> contains two values: fundingRate (index 0)
    /// and markPrice (index 1).
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchFundingRatesAsync(
        string symbol,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cursor = fromMs;

        while (cursor <= toMs)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await FetchFundingRateBatchWithRetryAsync(symbol, cursor, toMs, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < FundingRateLimit)
                yield break;

            cursor = batch[^1].TimestampMs + 1;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — funding rate
    // -------------------------------------------------------------------------

    private async Task<FeedRecord[]> FetchFundingRateBatchWithRetryAsync(
        string symbol,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await rateLimiter.AcquireAsync(FundingRateWeight, ct).ConfigureAwait(false);
            await Task.Delay(options.RequestDelayMs, ct).ConfigureAwait(false);

            var url = BuildFundingRateUrl(symbol, fromMs, toMs);
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
            return ParseFundingRateBatch(json);
        }

        // Unreachable — loop always returns or throws within MaxRetries iterations.
        throw new InvalidOperationException("Unexpected state in FetchFundingRateBatchWithRetryAsync.");
    }

    private string BuildFundingRateUrl(string symbol, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/fapi/v1/fundingRate" +
        $"?symbol={symbol}&startTime={fromMs}&endTime={toMs}&limit={FundingRateLimit}";

    private static FeedRecord[] ParseFundingRateBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            long fundingTime  = element.GetProperty("fundingTime").GetInt64();
            double fundingRate = double.Parse(
                element.GetProperty("fundingRate").GetString()!,
                CultureInfo.InvariantCulture);
            double markPrice   = double.Parse(
                element.GetProperty("markPrice").GetString()!,
                CultureInfo.InvariantCulture);

            records[i++] = new FeedRecord(fundingTime, [fundingRate, markPrice]);
        }

        return records;
    }
}

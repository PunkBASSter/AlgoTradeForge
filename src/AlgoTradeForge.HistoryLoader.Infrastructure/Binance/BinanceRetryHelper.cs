using System.Net;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal static class BinanceRetryHelper
{
    private const int MaxRetries = 3;

    public static async Task<T[]> FetchWithRetryAsync<T>(
        HttpClient httpClient,
        SourceRateLimiter rateLimiter,
        int requestDelayMs,
        string url,
        int weight,
        Func<string, T[]> parser,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await rateLimiter.AcquireAsync(weight, ct).ConfigureAwait(false);
            await Task.Delay(requestDelayMs, ct).ConfigureAwait(false);

            using var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == MaxRetries)
                    throw new HttpRequestException(
                        $"Binance rate limit exceeded after {MaxRetries} retries (HTTP 429).");

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
            return parser(json);
        }

        throw new InvalidOperationException("Unexpected state in FetchWithRetryAsync.");
    }
}

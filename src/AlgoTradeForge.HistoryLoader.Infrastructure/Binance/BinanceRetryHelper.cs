using System.Net;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
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

            HttpResponseMessage response;
            try
            {
                response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                if (attempt == MaxRetries)
                    throw;

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
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

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw ParseApiException(body, response.StatusCode);
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return parser(json);
            }
        }

        throw new InvalidOperationException("Unexpected state in FetchWithRetryAsync.");
    }

    private static HttpRequestException ParseApiException(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var codeProp)
                && root.TryGetProperty("msg", out var msgProp)
                && codeProp.TryGetInt32(out var code))
            {
                var msg = msgProp.GetString() ?? "";
                return new DataSourceApiException(code, msg, statusCode,
                    isDateRangeError: IsBinanceDateRangeError(msg));
            }
        }
        catch (JsonException) { }

        return new HttpRequestException(
            $"HTTP {(int)statusCode}: {body}", inner: null, statusCode);
    }

    /// <summary>
    /// Binance-specific: determines whether an error message indicates a time-range
    /// issue (startTime too old, invalid period) vs a parameter/endpoint error.
    /// </summary>
    private static bool IsBinanceDateRangeError(string msg) =>
        msg.Contains("startTime", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("endTime", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("Invalid period", StringComparison.OrdinalIgnoreCase);
}

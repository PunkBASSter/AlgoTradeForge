using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.CandleIngestor.DataSourceAdapters;

public sealed class BinanceAdapter(HttpClient httpClient, AdapterOptions options) : IDataAdapter
{
    private readonly RateLimiter _rateLimiter = new(options.RateLimitPerMinute, options.RequestDelayMs);

    public async IAsyncEnumerable<RawCandle> FetchCandlesAsync(
        string symbol,
        TimeSpan interval,
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var intervalStr = ToIntervalString(interval);
        var currentStart = from;

        while (currentStart < to)
        {
            await _rateLimiter.WaitAsync(ct);

            var url = $"{options.BaseUrl}/api/v3/klines?symbol={symbol}&interval={intervalStr}" +
                      $"&startTime={currentStart.ToUnixTimeMilliseconds()}" +
                      $"&endTime={to.ToUnixTimeMilliseconds()}" +
                      $"&limit=1000";

            JsonElement[]? candles = null;
            var retries = 0;

            while (retries < 3)
            {
                var response = await httpClient.GetAsync(url, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, retries + 1));
                    await Task.Delay(backoff, ct);
                    retries++;
                    continue;
                }

                if (response.StatusCode == (HttpStatusCode)418)
                    throw new HttpRequestException("IP banned by Binance (HTTP 418).");

                if ((int)response.StatusCode >= 500)
                {
                    retries++;
                    if (retries >= 3)
                        throw new HttpRequestException($"Binance server error after 3 retries: {response.StatusCode}");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retries)), ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                candles = JsonSerializer.Deserialize<JsonElement[]>(json);
                break;
            }

            if (candles is null || candles.Length == 0)
                yield break;

            DateTimeOffset lastTimestamp = default;

            foreach (var k in candles)
            {
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64());
                var open = decimal.Parse(k[1].GetString()!, CultureInfo.InvariantCulture);
                var high = decimal.Parse(k[2].GetString()!, CultureInfo.InvariantCulture);
                var low = decimal.Parse(k[3].GetString()!, CultureInfo.InvariantCulture);
                var close = decimal.Parse(k[4].GetString()!, CultureInfo.InvariantCulture);
                var volume = decimal.Parse(k[5].GetString()!, CultureInfo.InvariantCulture);

                lastTimestamp = timestamp;
                yield return new RawCandle(timestamp, open, high, low, close, volume);
            }

            if (candles.Length < 1000)
                yield break;

            currentStart = lastTimestamp + interval;
        }
    }

    internal static string ToIntervalString(TimeSpan interval) => interval.TotalMinutes switch
    {
        1 => "1m",
        3 => "3m",
        5 => "5m",
        15 => "15m",
        30 => "30m",
        60 => "1h",
        120 => "2h",
        240 => "4h",
        360 => "6h",
        480 => "8h",
        720 => "12h",
        1440 => "1d",
        _ => throw new ArgumentException($"Unsupported interval: {interval}")
    };
}

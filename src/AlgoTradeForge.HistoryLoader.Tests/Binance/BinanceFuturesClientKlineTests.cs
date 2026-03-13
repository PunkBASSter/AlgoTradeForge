using System.Net;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientKlineTests
{
    // -------------------------------------------------------------------------
    // Fake HTTP handler
    // -------------------------------------------------------------------------

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } = _ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Handler(request);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static BinanceFuturesClient BuildClient(
        FakeHandler handler,
        BinanceOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var opts = options ?? new BinanceOptions { RequestDelayMs = 0 };
        var limiter = new SourceRateLimiter(
            new WeightedRateLimiter(maxWeightPerMinute: 2400, budgetPercent: 100),
            opts.FuturesBaseUrl);
        return new BinanceFuturesClient(httpClient, opts, limiter);
    }

    /// <summary>
    /// Builds a JSON array-of-arrays representing Binance kline API response rows.
    /// Each record maps to the 11-element inner array format documented by Binance.
    /// </summary>
    private static string BuildKlineJson(
        params (long ts, string o, string h, string l, string c,
                string v, string qv, int tc, string tbv, string tbqv)[] records)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = records[i];
            // [openTime, open, high, low, close, volume, closeTime, quoteVolume,
            //  tradeCount, takerBuyBaseVol, takerBuyQuoteVol, ignore]
            sb.Append($"[{r.ts},\"{r.o}\",\"{r.h}\",\"{r.l}\",\"{r.c}\"," +
                      $"\"{r.v}\",{r.ts + 59999},\"{r.qv}\",{r.tc},\"{r.tbv}\",\"{r.tbqv}\",\"0\"]");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    // -------------------------------------------------------------------------
    // 1. FetchKlinesAsync_ParsesResponse_ReturnsKlineRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchKlinesAsync_ParsesResponse_ReturnsKlineRecords()
    {
        var json = BuildKlineJson(
            (1_700_000_000_000L, "50000.50", "51000.75", "49500.25", "50500.00",
             "123.45", "6172500.00", 3000, "60.00", "3000000.00"),
            (1_700_000_060_000L, "50500.00", "52000.00", "50000.00", "51500.00",
             "200.00", "10300000.00", 4500, "100.00", "5150000.00"));

        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchKlinesAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_120_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(50000.50m, first.Open);
        Assert.Equal(51000.75m, first.High);
        Assert.Equal(49500.25m, first.Low);
        Assert.Equal(50500.00m, first.Close);
        Assert.Equal(123.45m, first.Volume);
        Assert.Equal(6172500.00m, first.QuoteVolume);
        Assert.Equal(3000, first.TradeCount);
        Assert.Equal(60.00m, first.TakerBuyVolume);
        Assert.Equal(3000000.00m, first.TakerBuyQuoteVolume);

        var second = records[1];
        Assert.Equal(1_700_000_060_000L, second.TimestampMs);
        Assert.Equal(50500.00m, second.Open);
        Assert.Equal(51500.00m, second.Close);
    }

    // -------------------------------------------------------------------------
    // 2. FetchKlinesAsync_Pagination_MakesMultipleRequests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchKlinesAsync_Pagination_MakesMultipleRequests()
    {
        // Build a first batch of exactly 1500 records (triggers pagination).
        var firstBatchRecords = Enumerable.Range(0, 1500)
            .Select(i => (
                ts: 1_700_000_000_000L + i * 60_000L,
                o: "100.00", h: "101.00", l: "99.00", c: "100.50",
                v: "10.00", qv: "1005.00", tc: 100, tbv: "5.00", tbqv: "502.50"))
            .ToArray();

        var firstBatchJson = BuildKlineJson(firstBatchRecords);

        // Second batch has 3 records — signals end of data.
        var secondBatchJson = BuildKlineJson(
            (1_700_000_000_000L + 1500 * 60_000L, "100.50", "102.00", "99.50", "101.00",
             "8.00", "808.00", 80, "4.00", "404.00"),
            (1_700_000_000_000L + 1501 * 60_000L, "101.00", "103.00", "100.00", "102.00",
             "6.00", "612.00", 60, "3.00", "306.00"),
            (1_700_000_000_000L + 1502 * 60_000L, "102.00", "104.00", "101.00", "103.00",
             "5.00", "515.00", 50, "2.50", "257.50"));

        int requestCount = 0;
        var handler = new FakeHandler
        {
            Handler = _ =>
            {
                requestCount++;
                var responseJson = requestCount == 1 ? firstBatchJson : secondBatchJson;
                return Task.FromResult(JsonResponse(responseJson));
            }
        };

        var client = BuildClient(handler);
        long endMs = 1_700_000_000_000L + 2000 * 60_000L;
        var records = await client
            .FetchKlinesAsync("BTCUSDT", "1m", 1_700_000_000_000L, endMs, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal(1503, records.Count);

        // Verify first record of first batch
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        // Verify first record of second batch
        Assert.Equal(1_700_000_000_000L + 1500 * 60_000L, records[1500].TimestampMs);
        // Verify last record of second batch
        Assert.Equal(1_700_000_000_000L + 1502 * 60_000L, records[1502].TimestampMs);
    }

    // -------------------------------------------------------------------------
    // 3. FetchKlinesAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchKlinesAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchKlinesAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 4. FetchKlinesAsync_Http429_RetriesWithBackoff
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchKlinesAsync_Http429_RetriesWithBackoff()
    {
        var json = BuildKlineJson(
            (1_700_000_000_000L, "100.00", "101.00", "99.00", "100.50",
             "10.00", "1005.00", 100, "5.00", "502.50"));

        int callCount = 0;
        var handler = new FakeHandler
        {
            Handler = _ =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                return Task.FromResult(JsonResponse(json));
            }
        };

        // Use very short delays so the test does not take long.
        var opts = new BinanceOptions { RequestDelayMs = 0 };
        var httpClient = new HttpClient(handler);
        // Override the backoff by patching isn't possible here — the test must
        // tolerate a short real delay (2^1 = 2 s) unless we restructure.
        // For test speed we use a CancellationToken with generous timeout.
        var client = BuildClient(handler, opts);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var records = await client
            .FetchKlinesAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, cts.Token)
            .ToListAsync(cts.Token);

        Assert.Equal(2, callCount);
        Assert.Single(records);
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
    }

    // -------------------------------------------------------------------------
    // 5. FetchKlinesAsync_Http418_ThrowsHttpRequestException
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchKlinesAsync_Http418_ThrowsHttpRequestException()
    {
        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)418))
        };

        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.FetchKlinesAsync(
                "BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, CancellationToken.None))
            {
                // consume the enumerable to trigger the HTTP call
            }
        });

        Assert.Contains("418", ex.Message);
    }
}

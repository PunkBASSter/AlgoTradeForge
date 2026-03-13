using System.Net;
using System.Text;
using AlgoTradeForge.HistoryLoader.Binance;
using AlgoTradeForge.HistoryLoader.RateLimiting;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientTakerTests
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
    /// Builds a JSON array of taker buy/sell volume objects as returned by
    /// <c>GET /futures/data/takeBuySellVol</c>.
    /// </summary>
    private static string BuildTakerVolumeJson(
        params (long timestamp, string buyVol, string sellVol, string buySellRatio)[] records)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = records[i];
            sb.Append($"{{\"timestamp\":{r.timestamp}," +
                      $"\"buyVol\":\"{r.buyVol}\"," +
                      $"\"sellVol\":\"{r.sellVol}\"," +
                      $"\"buySellRatio\":\"{r.buySellRatio}\"}}");
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
    // 1. FetchTakerVolumeAsync_ParsesResponse_ReturnsFeedRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTakerVolumeAsync_ParsesResponse_ReturnsFeedRecords()
    {
        var json = BuildTakerVolumeJson(
            (1_700_000_000_000L, "123456789.00", "98765432.00", "1.2500"),
            (1_700_000_300_000L, "200000000.00", "180000000.00", "1.1111"));

        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchTakerVolumeAsync(
                "BTCUSDT",
                "5m",
                1_700_000_000_000L,
                1_700_000_600_000L,
                CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(3, first.Values.Length);
        Assert.Equal(123456789.0,  first.Values[0], precision: 5);  // buyVol
        Assert.Equal(98765432.0,   first.Values[1], precision: 5);  // sellVol
        Assert.Equal(1.25,         first.Values[2], precision: 10); // buySellRatio

        var second = records[1];
        Assert.Equal(1_700_000_300_000L, second.TimestampMs);
        Assert.Equal(200000000.0, second.Values[0], precision: 5);
        Assert.Equal(180000000.0, second.Values[1], precision: 5);
        Assert.Equal(1.1111,      second.Values[2], precision: 4);
    }

    // -------------------------------------------------------------------------
    // 2. FetchTakerVolumeAsync_UsesCorrectEndpointWithPeriodParam
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTakerVolumeAsync_UsesCorrectEndpointWithPeriodParam()
    {
        string? capturedUrl = null;

        var handler = new FakeHandler
        {
            Handler = req =>
            {
                capturedUrl = req.RequestUri?.ToString();
                return Task.FromResult(JsonResponse("[]"));
            }
        };

        var client = BuildClient(handler);
        await client
            .FetchTakerVolumeAsync("BTCUSDT", "5m", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.NotNull(capturedUrl);
        Assert.Contains("/futures/data/takeBuySellVol", capturedUrl);
        Assert.Contains("period=5m", capturedUrl);
        Assert.DoesNotContain("interval=", capturedUrl);
    }

    // -------------------------------------------------------------------------
    // 3. FetchTakerVolumeAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTakerVolumeAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchTakerVolumeAsync(
                "BTCUSDT",
                "5m",
                1_700_000_000_000L,
                1_700_000_600_000L,
                CancellationToken.None)
            .ToListAsync();

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 4. FetchTakerVolumeAsync_Pagination_MakesMultipleRequests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTakerVolumeAsync_Pagination_MakesMultipleRequests()
    {
        var firstBatchRecords = Enumerable.Range(0, 500)
            .Select(i => (
                timestamp: 1_700_000_000_000L + i * 300_000L,
                buyVol: "100000000.0",
                sellVol: "90000000.0",
                buySellRatio: "1.1111"))
            .ToArray();

        var firstBatchJson = BuildTakerVolumeJson(firstBatchRecords);

        var secondBatchJson = BuildTakerVolumeJson(
            (1_700_000_000_000L + 500 * 300_000L, "110000000.0", "95000000.0", "1.1579"),
            (1_700_000_000_000L + 501 * 300_000L, "105000000.0", "100000000.0", "1.0500"));

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
        long endMs = 1_700_000_000_000L + 1000 * 300_000L;
        var records = await client
            .FetchTakerVolumeAsync("BTCUSDT", "5m", 1_700_000_000_000L, endMs, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal(502, records.Count);
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        Assert.Equal(1_700_000_000_000L + 501 * 300_000L, records[501].TimestampMs);
    }
}

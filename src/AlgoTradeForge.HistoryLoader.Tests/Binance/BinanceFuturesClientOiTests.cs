using System.Net;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientOiTests
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
    /// Builds a JSON array of open interest history objects as returned by
    /// <c>GET /futures/data/openInterestHist</c>.
    /// </summary>
    private static string BuildOiJson(
        params (long timestamp, string sumOi, string sumOiValue)[] records)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = records[i];
            sb.Append($"{{\"timestamp\":{r.timestamp}," +
                      $"\"sumOpenInterest\":\"{r.sumOi}\"," +
                      $"\"sumOpenInterestValue\":\"{r.sumOiValue}\"}}");
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
    // 1. FetchOpenInterestAsync_ParsesResponse_ReturnsFeedRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchOpenInterestAsync_ParsesResponse_ReturnsFeedRecords()
    {
        var json = BuildOiJson(
            (1_700_000_000_000L, "12345.678", "617283900.0"),
            (1_700_000_300_000L, "12500.000", "625000000.0"));

        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchOpenInterestAsync(
                "BTCUSDT",
                "5m",
                1_700_000_000_000L,
                1_700_000_600_000L,
                CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(2, first.Values.Length);
        Assert.Equal(12345.678, first.Values[0], precision: 10);    // sumOpenInterest
        Assert.Equal(617283900.0, first.Values[1], precision: 5);   // sumOpenInterestValue

        var second = records[1];
        Assert.Equal(1_700_000_300_000L, second.TimestampMs);
        Assert.Equal(12500.0, second.Values[0], precision: 10);
        Assert.Equal(625000000.0, second.Values[1], precision: 5);
    }

    // -------------------------------------------------------------------------
    // 2. FetchOpenInterestAsync_Pagination_MakesMultipleRequests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchOpenInterestAsync_Pagination_MakesMultipleRequests()
    {
        // First batch: exactly 500 records — triggers pagination.
        var firstBatchRecords = Enumerable.Range(0, 500)
            .Select(i => (
                timestamp: 1_700_000_000_000L + i * 300_000L, // 5-minute intervals
                sumOi: "10000.0",
                sumOiValue: "500000000.0"))
            .ToArray();

        var firstBatchJson = BuildOiJson(firstBatchRecords);

        // Second batch: 3 records — signals end of data.
        var secondBatchJson = BuildOiJson(
            (1_700_000_000_000L + 500 * 300_000L, "10100.0", "505000000.0"),
            (1_700_000_000_000L + 501 * 300_000L, "10200.0", "510000000.0"),
            (1_700_000_000_000L + 502 * 300_000L, "10300.0", "515000000.0"));

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
            .FetchOpenInterestAsync("BTCUSDT", "5m", 1_700_000_000_000L, endMs, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal(503, records.Count);

        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        Assert.Equal(1_700_000_000_000L + 500 * 300_000L, records[500].TimestampMs);
        Assert.Equal(1_700_000_000_000L + 502 * 300_000L, records[502].TimestampMs);
    }

    // -------------------------------------------------------------------------
    // 3. FetchOpenInterestAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchOpenInterestAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchOpenInterestAsync(
                "BTCUSDT",
                "5m",
                1_700_000_000_000L,
                1_700_000_600_000L,
                CancellationToken.None)
            .ToListAsync();

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 4. FetchOpenInterestAsync_UsesCorrectEndpointWithPeriodParam
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchOpenInterestAsync_UsesCorrectEndpointWithPeriodParam()
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
            .FetchOpenInterestAsync("BTCUSDT", "5m", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.NotNull(capturedUrl);
        Assert.Contains("/futures/data/openInterestHist", capturedUrl);
        Assert.Contains("period=5m", capturedUrl);
        Assert.DoesNotContain("interval=", capturedUrl);
    }
}

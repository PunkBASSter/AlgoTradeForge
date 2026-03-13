using System.Net;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientRatioTests
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
    /// Builds a JSON array of long/short ratio objects as returned by the Binance
    /// global or top-account ratio endpoints.
    /// </summary>
    private static string BuildRatioJson(
        params (long timestamp, string longAccount, string shortAccount, string longShortRatio)[] records)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = records[i];
            sb.Append($"{{\"timestamp\":{r.timestamp}," +
                      $"\"longAccount\":\"{r.longAccount}\"," +
                      $"\"shortAccount\":\"{r.shortAccount}\"," +
                      $"\"longShortRatio\":\"{r.longShortRatio}\"}}");
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
    // 1. FetchGlobalLongShortRatioAsync_ParsesResponse_ReturnsFeedRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchGlobalLongShortRatioAsync_ParsesResponse_ReturnsFeedRecords()
    {
        var json = BuildRatioJson(
            (1_700_000_000_000L, "0.6000", "0.4000", "1.5000"),
            (1_700_000_300_000L, "0.5500", "0.4500", "1.2222"));

        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchGlobalLongShortRatioAsync(
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
        Assert.Equal(0.6,    first.Values[0], precision: 10);  // longAccount
        Assert.Equal(0.4,    first.Values[1], precision: 10);  // shortAccount
        Assert.Equal(1.5,    first.Values[2], precision: 10);  // longShortRatio

        var second = records[1];
        Assert.Equal(1_700_000_300_000L, second.TimestampMs);
        Assert.Equal(0.55,   second.Values[0], precision: 10);
        Assert.Equal(0.45,   second.Values[1], precision: 10);
        Assert.Equal(1.2222, second.Values[2], precision: 4);
    }

    // -------------------------------------------------------------------------
    // 2. FetchGlobalLongShortRatioAsync_UsesCorrectEndpoint
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchGlobalLongShortRatioAsync_UsesCorrectEndpoint()
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
            .FetchGlobalLongShortRatioAsync("BTCUSDT", "5m", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.NotNull(capturedUrl);
        Assert.Contains("/futures/data/globalLongShortAccountRatio", capturedUrl);
        Assert.Contains("period=5m", capturedUrl);
    }

    // -------------------------------------------------------------------------
    // 3. FetchTopAccountRatioAsync_ParsesResponse_ReturnsFeedRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTopAccountRatioAsync_ParsesResponse_ReturnsFeedRecords()
    {
        var json = BuildRatioJson(
            (1_700_000_000_000L, "0.7000", "0.3000", "2.3333"),
            (1_700_000_300_000L, "0.6500", "0.3500", "1.8571"));

        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchTopAccountRatioAsync(
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
        Assert.Equal(0.7,    first.Values[0], precision: 10);  // longAccount
        Assert.Equal(0.3,    first.Values[1], precision: 10);  // shortAccount
        Assert.Equal(2.3333, first.Values[2], precision: 4);   // longShortRatio
    }

    // -------------------------------------------------------------------------
    // 4. FetchTopAccountRatioAsync_UsesCorrectEndpoint
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTopAccountRatioAsync_UsesCorrectEndpoint()
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
            .FetchTopAccountRatioAsync("BTCUSDT", "5m", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.NotNull(capturedUrl);
        Assert.Contains("/futures/data/topLongShortAccountRatio", capturedUrl);
        Assert.Contains("period=5m", capturedUrl);
    }

    // -------------------------------------------------------------------------
    // 5. FetchGlobalLongShortRatioAsync_Pagination_MakesMultipleRequests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchGlobalLongShortRatioAsync_Pagination_MakesMultipleRequests()
    {
        var firstBatchRecords = Enumerable.Range(0, 500)
            .Select(i => (
                timestamp: 1_700_000_000_000L + i * 300_000L,
                longAccount: "0.6000",
                shortAccount: "0.4000",
                longShortRatio: "1.5000"))
            .ToArray();

        var firstBatchJson = BuildRatioJson(firstBatchRecords);

        var secondBatchJson = BuildRatioJson(
            (1_700_000_000_000L + 500 * 300_000L, "0.5800", "0.4200", "1.3810"),
            (1_700_000_000_000L + 501 * 300_000L, "0.5600", "0.4400", "1.2727"));

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
            .FetchGlobalLongShortRatioAsync("BTCUSDT", "5m", 1_700_000_000_000L, endMs, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal(502, records.Count);
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        Assert.Equal(1_700_000_000_000L + 501 * 300_000L, records[501].TimestampMs);
    }

    // -------------------------------------------------------------------------
    // 6. FetchGlobalLongShortRatioAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchGlobalLongShortRatioAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHandler
        {
            Handler = _ => Task.FromResult(JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchGlobalLongShortRatioAsync(
                "BTCUSDT",
                "5m",
                1_700_000_000_000L,
                1_700_000_600_000L,
                CancellationToken.None)
            .ToListAsync();

        Assert.Empty(records);
    }
}

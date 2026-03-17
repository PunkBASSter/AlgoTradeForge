using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientPositionRatioTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static BinanceFuturesClient BuildClient(
        FakeHttpHandler handler,
        BinanceOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var opts = options ?? new BinanceOptions { RequestDelayMs = 0 };
        var limiter = new SourceRateLimiter(
            new WeightedRateLimiter(maxWeightPerMinute: 2400, budgetPercent: 100));
        return new BinanceFuturesClient(httpClient, opts, limiter);
    }

    /// <summary>
    /// Builds a JSON array of top position ratio objects as returned by
    /// <c>GET /futures/data/topLongShortPositionRatio</c>.
    /// </summary>
    private static string BuildPositionRatioJson(
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

    // -------------------------------------------------------------------------
    // 1. FetchTopPositionRatioAsync_ParsesResponse_ReturnsFeedRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTopPositionRatioAsync_ParsesResponse_ReturnsFeedRecords()
    {
        var json = BuildPositionRatioJson(
            (1_700_000_000_000L, "0.7500", "0.2500", "3.0000"),
            (1_700_000_300_000L, "0.6800", "0.3200", "2.1250"));

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchTopPositionRatioAsync(
                "BTCUSDT",
                "5m",
                1_700_000_000_000L,
                1_700_000_600_000L,
                TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(3, first.Values.Length);
        Assert.Equal(0.75,  first.Values[0], precision: 10);  // longAccount
        Assert.Equal(0.25,  first.Values[1], precision: 10);  // shortAccount
        Assert.Equal(3.0,   first.Values[2], precision: 10);  // longShortRatio

        var second = records[1];
        Assert.Equal(1_700_000_300_000L, second.TimestampMs);
        Assert.Equal(0.68,   second.Values[0], precision: 10);
        Assert.Equal(0.32,   second.Values[1], precision: 10);
        Assert.Equal(2.125,  second.Values[2], precision: 10);
    }

    // -------------------------------------------------------------------------
    // 2. FetchTopPositionRatioAsync_UsesCorrectEndpoint
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTopPositionRatioAsync_UsesCorrectEndpoint()
    {
        string? capturedUrl = null;

        var handler = new FakeHttpHandler
        {
            Handler = req =>
            {
                capturedUrl = req.RequestUri?.ToString();
                return Task.FromResult(FakeHttpHandler.JsonResponse("[]"));
            }
        };

        var client = BuildClient(handler);
        await client
            .FetchTopPositionRatioAsync("BTCUSDT", "5m", 1_700_000_000_000L, 1_700_000_600_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/futures/data/topLongShortPositionRatio", capturedUrl);
        Assert.Contains("period=5m", capturedUrl);
        Assert.Contains("symbol=BTCUSDT", capturedUrl);
        Assert.Contains("limit=500", capturedUrl);
    }

    // -------------------------------------------------------------------------
    // 3. FetchTopPositionRatioAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTopPositionRatioAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchTopPositionRatioAsync(
                "BTCUSDT",
                "5m",
                1_700_000_000_000L,
                1_700_000_600_000L,
                TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 4. FetchTopPositionRatioAsync_Pagination_MakesMultipleRequests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchTopPositionRatioAsync_Pagination_MakesMultipleRequests()
    {
        var firstBatchRecords = Enumerable.Range(0, 500)
            .Select(i => (
                timestamp: 1_700_000_000_000L + i * 300_000L,
                longAccount: "0.7000",
                shortAccount: "0.3000",
                longShortRatio: "2.3333"))
            .ToArray();

        var firstBatchJson = BuildPositionRatioJson(firstBatchRecords);

        var secondBatchJson = BuildPositionRatioJson(
            (1_700_000_000_000L + 500 * 300_000L, "0.6500", "0.3500", "1.8571"),
            (1_700_000_000_000L + 501 * 300_000L, "0.6000", "0.4000", "1.5000"));

        int requestCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                requestCount++;
                var responseJson = requestCount == 1 ? firstBatchJson : secondBatchJson;
                return Task.FromResult(FakeHttpHandler.JsonResponse(responseJson));
            }
        };

        var client = BuildClient(handler);
        long endMs = 1_700_000_000_000L + 1000 * 300_000L;
        var records = await client
            .FetchTopPositionRatioAsync("BTCUSDT", "5m", 1_700_000_000_000L, endMs, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, requestCount);
        Assert.Equal(502, records.Count);
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        Assert.Equal(1_700_000_000_000L + 501 * 300_000L, records[501].TimestampMs);
    }
}

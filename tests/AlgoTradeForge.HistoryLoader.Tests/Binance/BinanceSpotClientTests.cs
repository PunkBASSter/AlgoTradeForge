using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceSpotClientTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static BinanceSpotClient BuildClient(
        FakeHttpHandler handler,
        BinanceOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var opts = options ?? new BinanceOptions { RequestDelayMs = 0 };
        var limiter = new SourceRateLimiter(
            new WeightedRateLimiter(maxWeightPerMinute: 2400, budgetPercent: 100));
        return new BinanceSpotClient(httpClient, opts, limiter);
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

    // -------------------------------------------------------------------------
    // 1. FetchCandlesAsync_ParsesResponse_ReturnsKlineRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchCandlesAsync_ParsesResponse_ReturnsKlineRecords()
    {
        var json = BuildKlineJson(
            (1_700_000_000_000L, "50000.50", "51000.75", "49500.25", "50500.00",
             "123.45", "6172500.00", 3000, "60.00", "3000000.00"),
            (1_700_000_060_000L, "50500.00", "52000.00", "50000.00", "51500.00",
             "200.00", "10300000.00", 4500, "100.00", "5150000.00"));

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchCandlesAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_120_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(50000.50m, first.Open);
        Assert.Equal(51000.75m, first.High);
        Assert.Equal(49500.25m, first.Low);
        Assert.Equal(50500.00m, first.Close);
        Assert.Equal(123.45m, first.Volume);
        Assert.NotNull(first.ExtValues);
        Assert.Equal(4, first.ExtValues.Length);
        Assert.Equal(6172500.00, first.ExtValues[0]); // quoteVolume
        Assert.Equal(3000, first.ExtValues[1]);        // tradeCount
        Assert.Equal(60.00, first.ExtValues[2]);       // takerBuyVolume
        Assert.Equal(3000000.00, first.ExtValues[3]);  // takerBuyQuoteVolume

        var second = records[1];
        Assert.Equal(1_700_000_060_000L, second.TimestampMs);
        Assert.Equal(50500.00m, second.Open);
        Assert.Equal(51500.00m, second.Close);
    }

    // -------------------------------------------------------------------------
    // 2. FetchCandlesAsync_Pagination_StopsAtLimit1000
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchCandlesAsync_Pagination_StopsAtLimit1000()
    {
        // Build a first batch of exactly 1000 records (the spot limit — triggers pagination).
        var firstBatchRecords = Enumerable.Range(0, 1000)
            .Select(i => (
                ts: 1_700_000_000_000L + i * 60_000L,
                o: "100.00", h: "101.00", l: "99.00", c: "100.50",
                v: "10.00", qv: "1005.00", tc: 100, tbv: "5.00", tbqv: "502.50"))
            .ToArray();

        var firstBatchJson = BuildKlineJson(firstBatchRecords);

        // Second batch has 3 records — signals end of data.
        var secondBatchJson = BuildKlineJson(
            (1_700_000_000_000L + 1000 * 60_000L, "100.50", "102.00", "99.50", "101.00",
             "8.00", "808.00", 80, "4.00", "404.00"),
            (1_700_000_000_000L + 1001 * 60_000L, "101.00", "103.00", "100.00", "102.00",
             "6.00", "612.00", 60, "3.00", "306.00"),
            (1_700_000_000_000L + 1002 * 60_000L, "102.00", "104.00", "101.00", "103.00",
             "5.00", "515.00", 50, "2.50", "257.50"));

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
        long endMs = 1_700_000_000_000L + 2000 * 60_000L;
        var records = await client
            .FetchCandlesAsync("BTCUSDT", "1m", 1_700_000_000_000L, endMs, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, requestCount);
        Assert.Equal(1003, records.Count);

        // Verify first record of first batch
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        // Verify first record of second batch
        Assert.Equal(1_700_000_000_000L + 1000 * 60_000L, records[1000].TimestampMs);
        // Verify last record of second batch
        Assert.Equal(1_700_000_000_000L + 1002 * 60_000L, records[1002].TimestampMs);
    }

    // -------------------------------------------------------------------------
    // 3. FetchCandlesAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchCandlesAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchCandlesAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 4. FetchCandlesAsync_UsesSpotBaseUrl
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchCandlesAsync_UsesSpotBaseUrl()
    {
        string? capturedUrl = null;
        var json = BuildKlineJson(
            (1_700_000_000_000L, "100.00", "101.00", "99.00", "100.50",
             "10.00", "1005.00", 100, "5.00", "502.50"));

        var handler = new FakeHttpHandler
        {
            Handler = req =>
            {
                capturedUrl = req.RequestUri?.ToString();
                return Task.FromResult(FakeHttpHandler.JsonResponse(json));
            }
        };

        var opts = new BinanceOptions
        {
            SpotBaseUrl = "https://api.binance.com",
            RequestDelayMs = 0
        };
        var client = BuildClient(handler, opts);

        await client
            .FetchCandlesAsync("ETHUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/api/v3/klines", capturedUrl);
        Assert.Contains("https://api.binance.com", capturedUrl);
        Assert.Contains("ETHUSDT", capturedUrl);
        Assert.Contains("limit=1000", capturedUrl);
    }
}

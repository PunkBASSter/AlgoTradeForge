using System.Net;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientMarkPriceTests
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
    /// Builds a JSON array-of-arrays representing the Binance markPriceKlines response.
    /// Each inner array has the same structure as regular klines; volume fields are "0".
    /// </summary>
    private static string BuildMarkPriceKlineJson(
        params (long ts, string o, string h, string l, string c)[] records)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = records[i];
            // openTime, open, high, low, close, volume(0), closeTime, quoteVolume(0),
            // tradeCount(0), takerBuyBaseVol(0), takerBuyQuoteVol(0), ignore
            sb.Append($"[{r.ts},\"{r.o}\",\"{r.h}\",\"{r.l}\",\"{r.c}\"," +
                      $"\"0\",{r.ts + 59999},\"0\",0,\"0\",\"0\",\"0\"]");
        }
        sb.Append(']');
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // 1. FetchMarkPriceFeedAsync_ParsesResponse_ReturnsFeedRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchMarkPriceFeedAsync_ParsesResponse_ReturnsFeedRecords()
    {
        var json = BuildMarkPriceKlineJson(
            (1_700_000_000_000L, "50000.50", "51000.75", "49500.25", "50500.00"),
            (1_700_000_060_000L, "50500.00", "52000.00", "50000.00", "51500.00"));

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchMarkPriceFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_120_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(50000.50, first.Values[0], precision: 10);
        Assert.Equal(51000.75, first.Values[1], precision: 10);
        Assert.Equal(49500.25, first.Values[2], precision: 10);
        Assert.Equal(50500.00, first.Values[3], precision: 10);

        var second = records[1];
        Assert.Equal(1_700_000_060_000L, second.TimestampMs);
        Assert.Equal(50500.00, second.Values[0], precision: 10);
        Assert.Equal(51500.00, second.Values[3], precision: 10);
    }

    // -------------------------------------------------------------------------
    // 2. FetchMarkPriceFeedAsync_Pagination_MakesMultipleRequests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchMarkPriceFeedAsync_Pagination_MakesMultipleRequests()
    {
        // Build a first batch of exactly 1500 records (triggers pagination).
        var firstBatchRecords = Enumerable.Range(0, 1500)
            .Select(i => (
                ts: 1_700_000_000_000L + i * 60_000L,
                o: "50000.00", h: "50100.00", l: "49900.00", c: "50050.00"))
            .ToArray();

        var firstBatchJson = BuildMarkPriceKlineJson(firstBatchRecords);

        // Second batch has 3 records — signals end of data.
        var secondBatchJson = BuildMarkPriceKlineJson(
            (1_700_000_000_000L + 1500 * 60_000L, "50050.00", "50200.00", "49950.00", "50100.00"),
            (1_700_000_000_000L + 1501 * 60_000L, "50100.00", "50300.00", "50000.00", "50150.00"),
            (1_700_000_000_000L + 1502 * 60_000L, "50150.00", "50400.00", "50050.00", "50200.00"));

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
            .FetchMarkPriceFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, endMs, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, requestCount);
        Assert.Equal(1503, records.Count);

        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        Assert.Equal(1_700_000_000_000L + 1500 * 60_000L, records[1500].TimestampMs);
        Assert.Equal(1_700_000_000_000L + 1502 * 60_000L, records[1502].TimestampMs);
    }

    // -------------------------------------------------------------------------
    // 3. FetchMarkPriceFeedAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchMarkPriceFeedAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchMarkPriceFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 4. FetchMarkPriceFeedAsync_UsesCorrectEndpoint
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchMarkPriceFeedAsync_UsesCorrectEndpoint()
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
            .FetchMarkPriceFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/fapi/v1/markPriceKlines", capturedUrl);
        Assert.Contains("symbol=BTCUSDT", capturedUrl);
        Assert.Contains("interval=1m", capturedUrl);
        Assert.Contains("limit=1500", capturedUrl);
    }

    // -------------------------------------------------------------------------
    // 5. FetchMarkPriceFeedAsync_Http429_RetriesWithBackoff
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchMarkPriceFeedAsync_Http429_RetriesWithBackoff()
    {
        var json = BuildMarkPriceKlineJson(
            (1_700_000_000_000L, "50000.00", "50100.00", "49900.00", "50050.00"));

        int callCount = 0;
        var handler = new FakeHttpHandler
        {
            Handler = _ =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                return Task.FromResult(FakeHttpHandler.JsonResponse(json));
            }
        };

        var opts = new BinanceOptions { RequestDelayMs = 0 };
        var client = BuildClient(handler, opts);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var records = await client
            .FetchMarkPriceFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, cts.Token)
            .ToListAsync(cts.Token);

        Assert.Equal(2, callCount);
        Assert.Single(records);
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
    }
}

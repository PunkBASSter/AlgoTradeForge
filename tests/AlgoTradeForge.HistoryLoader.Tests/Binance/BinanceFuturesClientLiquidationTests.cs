using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientLiquidationTests
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
    /// Builds a JSON array of force order objects as returned by
    /// <c>GET /fapi/v1/allForceOrders</c>.
    /// The nested <c>"o"</c> key wraps each order's fields.
    /// </summary>
    private static string BuildLiquidationJson(
        params (long time, string side, string price, string origQty, string executedQty, string averagePrice)[] orders)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < orders.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var o = orders[i];
            sb.Append($"{{\"o\":{{" +
                      $"\"time\":{o.time}," +
                      $"\"side\":\"{o.side}\"," +
                      $"\"price\":\"{o.price}\"," +
                      $"\"origQty\":\"{o.origQty}\"," +
                      $"\"executedQty\":\"{o.executedQty}\"," +
                      $"\"averagePrice\":\"{o.averagePrice}\"" +
                      $"}}}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // 1. FetchLiquidationsAsync_ParsesLongLiquidation_ReturnsSellSidePositive1
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchLiquidationsAsync_ParsesLongLiquidation_ReturnsSellSidePositive1()
    {
        // SELL order = long position liquidated → side = 1.0
        var json = BuildLiquidationJson(
            (1_700_000_000_000L, "SELL", "30000.00", "1.000", "1.000", "29950.00"));

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchLiquidationsAsync("BTCUSDT", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Single(records);
        var record = records[0];
        Assert.Equal(1_700_000_000_000L, record.TimestampMs);
        Assert.Equal(4, record.Values.Length);
        Assert.Equal(1.0,     record.Values[0], precision: 10);  // side: long liquidated
        Assert.Equal(29950.0, record.Values[1], precision: 5);   // averagePrice
        Assert.Equal(1.0,     record.Values[2], precision: 10);  // executedQty
        Assert.Equal(29950.0, record.Values[3], precision: 5);   // notional_usd = 1.0 * 29950.0
    }

    // -------------------------------------------------------------------------
    // 2. FetchLiquidationsAsync_ParsesShortLiquidation_ReturnsBuySideNegative1
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchLiquidationsAsync_ParsesShortLiquidation_ReturnsBuySideNegative1()
    {
        // BUY order = short position liquidated → side = -1.0
        var json = BuildLiquidationJson(
            (1_700_000_300_000L, "BUY", "31000.00", "2.000", "2.000", "31050.00"));

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchLiquidationsAsync("BTCUSDT", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Single(records);
        var record = records[0];
        Assert.Equal(1_700_000_300_000L, record.TimestampMs);
        Assert.Equal(4, record.Values.Length);
        Assert.Equal(-1.0,    record.Values[0], precision: 10);  // side: short liquidated
        Assert.Equal(31050.0, record.Values[1], precision: 5);   // averagePrice
        Assert.Equal(2.0,     record.Values[2], precision: 10);  // executedQty
        Assert.Equal(62100.0, record.Values[3], precision: 5);   // notional_usd = 2.0 * 31050.0
    }

    // -------------------------------------------------------------------------
    // 3. FetchLiquidationsAsync_ParsesMultipleRecords_ComputesNotionalCorrectly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchLiquidationsAsync_ParsesMultipleRecords_ComputesNotionalCorrectly()
    {
        var json = BuildLiquidationJson(
            (1_700_000_000_000L, "SELL", "30000.00", "1.500", "1.500", "29900.00"),
            (1_700_000_300_000L, "BUY",  "31000.00", "0.500", "0.500", "31100.00"));

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchLiquidationsAsync("BTCUSDT", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(1.0,     first.Values[0], precision: 10);    // long liquidated
        Assert.Equal(29900.0, first.Values[1], precision: 5);
        Assert.Equal(1.5,     first.Values[2], precision: 10);
        Assert.Equal(44850.0, first.Values[3], precision: 5);     // 1.5 * 29900

        var second = records[1];
        Assert.Equal(1_700_000_300_000L, second.TimestampMs);
        Assert.Equal(-1.0,    second.Values[0], precision: 10);   // short liquidated
        Assert.Equal(31100.0, second.Values[1], precision: 5);
        Assert.Equal(0.5,     second.Values[2], precision: 10);
        Assert.Equal(15550.0, second.Values[3], precision: 5);    // 0.5 * 31100
    }

    // -------------------------------------------------------------------------
    // 4. FetchLiquidationsAsync_UsesCorrectEndpoint
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchLiquidationsAsync_UsesCorrectEndpoint()
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
            .FetchLiquidationsAsync("BTCUSDT", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.NotNull(capturedUrl);
        Assert.Contains("/fapi/v1/allForceOrders", capturedUrl);
        Assert.Contains("symbol=BTCUSDT", capturedUrl);
        Assert.Contains("limit=1000", capturedUrl);
        Assert.Contains("startTime=", capturedUrl);
        Assert.Contains("endTime=", capturedUrl);
    }

    // -------------------------------------------------------------------------
    // 5. FetchLiquidationsAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchLiquidationsAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchLiquidationsAsync("BTCUSDT", 1_700_000_000_000L, 1_700_000_600_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 6. FetchLiquidationsAsync_Pagination_AdvancesStartTime
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchLiquidationsAsync_Pagination_AdvancesStartTime()
    {
        // Build first batch of 1000 entries to trigger pagination
        var firstBatchOrders = Enumerable.Range(0, 1000)
            .Select(i => (
                time: 1_700_000_000_000L + i * 1000L,
                side: "SELL",
                price: "30000.00",
                origQty: "1.000",
                executedQty: "1.000",
                averagePrice: "30000.00"))
            .ToArray();

        var firstBatchJson = BuildLiquidationJson(firstBatchOrders);

        var secondBatchJson = BuildLiquidationJson(
            (1_700_000_000_000L + 1000 * 1000L, "BUY", "31000.00", "0.500", "0.500", "31000.00"),
            (1_700_000_000_000L + 1001 * 1000L, "SELL", "29500.00", "2.000", "2.000", "29500.00"));

        int requestCount = 0;
        var capturedStartTimes = new List<string>();

        var handler = new FakeHttpHandler
        {
            Handler = req =>
            {
                requestCount++;
                var url = req.RequestUri?.ToString() ?? "";
                var startParam = url.Split('&')
                    .FirstOrDefault(p => p.StartsWith("startTime=", StringComparison.Ordinal));
                if (startParam is not null)
                    capturedStartTimes.Add(startParam);

                var responseJson = requestCount == 1 ? firstBatchJson : secondBatchJson;
                return Task.FromResult(FakeHttpHandler.JsonResponse(responseJson));
            }
        };

        var client = BuildClient(handler);
        long endMs = 1_700_000_000_000L + 2000 * 1000L;
        var records = await client
            .FetchLiquidationsAsync("BTCUSDT", 1_700_000_000_000L, endMs, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal(1002, records.Count);

        // Second request startTime should be last time of first batch + 1
        long expectedSecondStart = 1_700_000_000_000L + 999 * 1000L + 1;
        Assert.Equal(2, capturedStartTimes.Count);
        Assert.Equal($"startTime={expectedSecondStart}", capturedStartTimes[1]);
    }
}

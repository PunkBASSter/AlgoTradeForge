using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientFundingTests
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
    /// Builds a JSON array of funding rate objects as returned by
    /// <c>GET /fapi/v1/fundingRate</c>.
    /// </summary>
    private static string BuildFundingRateJson(
        params (long fundingTime, string rate, string markPrice)[] records)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = records[i];
            sb.Append($"{{\"fundingTime\":{r.fundingTime}," +
                      $"\"fundingRate\":\"{r.rate}\"," +
                      $"\"markPrice\":\"{r.markPrice}\"}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // 1. FetchFundingRatesAsync_ParsesResponse_ReturnsFeedRecords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchFundingRatesAsync_ParsesResponse_ReturnsFeedRecords()
    {
        var json = BuildFundingRateJson(
            (1_700_000_000_000L, "0.0001", "50000.5"),
            (1_700_028_800_000L, "-0.0002", "51000.25"));

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchFundingRatesAsync(
                "BTCUSDT",
                1_700_000_000_000L,
                1_700_100_000_000L,
                CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal(1_700_000_000_000L, first.TimestampMs);
        Assert.Equal(2, first.Values.Length);
        Assert.Equal(0.0001,   first.Values[0], precision: 10);   // fundingRate
        Assert.Equal(50000.5,  first.Values[1], precision: 10);   // markPrice

        var second = records[1];
        Assert.Equal(1_700_028_800_000L, second.TimestampMs);
        Assert.Equal(-0.0002,   second.Values[0], precision: 10);
        Assert.Equal(51000.25,  second.Values[1], precision: 10);
    }

    // -------------------------------------------------------------------------
    // 2. FetchFundingRatesAsync_Pagination_MakesMultipleRequests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchFundingRatesAsync_Pagination_MakesMultipleRequests()
    {
        // First batch: exactly 1000 records — triggers pagination.
        var firstBatchRecords = Enumerable.Range(0, 1000)
            .Select(i => (
                fundingTime: 1_700_000_000_000L + i * 28_800_000L, // 8-hour intervals
                rate: "0.0001",
                markPrice: "50000.0"))
            .ToArray();

        var firstBatchJson = BuildFundingRateJson(firstBatchRecords);

        // Second batch: 3 records — signals end of data.
        var secondBatchJson = BuildFundingRateJson(
            (1_700_000_000_000L + 1000 * 28_800_000L, "0.0002", "51000.0"),
            (1_700_000_000_000L + 1001 * 28_800_000L, "0.0003", "52000.0"),
            (1_700_000_000_000L + 1002 * 28_800_000L, "0.0004", "53000.0"));

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
        long endMs = 1_700_000_000_000L + 2000 * 28_800_000L;
        var records = await client
            .FetchFundingRatesAsync("BTCUSDT", 1_700_000_000_000L, endMs, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal(1003, records.Count);

        // Verify first record of first batch
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        // Verify first record of second batch
        Assert.Equal(1_700_000_000_000L + 1000 * 28_800_000L, records[1000].TimestampMs);
        // Verify last record of second batch
        Assert.Equal(1_700_000_000_000L + 1002 * 28_800_000L, records[1002].TimestampMs);
    }

    // -------------------------------------------------------------------------
    // 3. FetchFundingRatesAsync_EmptyResponse_YieldsNothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchFundingRatesAsync_EmptyResponse_YieldsNothing()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchFundingRatesAsync(
                "BTCUSDT",
                1_700_000_000_000L,
                1_700_100_000_000L,
                CancellationToken.None)
            .ToListAsync();

        Assert.Empty(records);
    }

    // -------------------------------------------------------------------------
    // 4. Empty string fields are skipped gracefully
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchFundingRatesAsync_EmptyFundingRate_SkipsRecord()
    {
        // Second record has empty fundingRate — should be skipped
        var json = """
            [
              {"fundingTime":1700000000000,"fundingRate":"0.0001","markPrice":"50000.5"},
              {"fundingTime":1700028800000,"fundingRate":"","markPrice":"51000.25"},
              {"fundingTime":1700057600000,"fundingRate":"0.0003","markPrice":"52000.0"}
            ]
            """;

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchFundingRatesAsync("BTCUSDT", 1_700_000_000_000L, 1_700_100_000_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, records.Count);
        Assert.Equal(1_700_000_000_000L, records[0].TimestampMs);
        Assert.Equal(1_700_057_600_000L, records[1].TimestampMs);
    }

    [Fact]
    public async Task FetchFundingRatesAsync_EmptyMarkPrice_SkipsRecord()
    {
        // First record has empty markPrice — should be skipped
        var json = """
            [
              {"fundingTime":1700000000000,"fundingRate":"0.0001","markPrice":""},
              {"fundingTime":1700028800000,"fundingRate":"-0.0002","markPrice":"51000.25"}
            ]
            """;

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchFundingRatesAsync("BTCUSDT", 1_700_000_000_000L, 1_700_100_000_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Single(records);
        Assert.Equal(1_700_028_800_000L, records[0].TimestampMs);
        Assert.Equal(-0.0002, records[0].Values[0], precision: 10);
        Assert.Equal(51000.25, records[0].Values[1], precision: 10);
    }

    [Fact]
    public async Task FetchFundingRatesAsync_AllEmptyFields_YieldsNothing()
    {
        var json = """
            [
              {"fundingTime":1700000000000,"fundingRate":"","markPrice":""},
              {"fundingTime":1700028800000,"fundingRate":"","markPrice":"51000.0"}
            ]
            """;

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchFundingRatesAsync("BTCUSDT", 1_700_000_000_000L, 1_700_100_000_000L, CancellationToken.None)
            .ToListAsync();

        Assert.Empty(records);
    }
}

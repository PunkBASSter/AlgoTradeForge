using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientPremiumIndexTests
{
    private static BinanceFuturesClient BuildClient(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var opts = new BinanceOptions { RequestDelayMs = 0 };
        var limiter = new SourceRateLimiter(
            new WeightedRateLimiter(maxWeightPerMinute: 2400, budgetPercent: 100));
        return new BinanceFuturesClient(httpClient, opts, limiter);
    }

    private static string BuildKlineJson(params (long ts, string o, string h, string l, string c)[] records)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = records[i];
            sb.Append($"[{r.ts},\"{r.o}\",\"{r.h}\",\"{r.l}\",\"{r.c}\"," +
                      $"\"0\",{r.ts + 59999},\"0\",0,\"0\",\"0\",\"0\"]");
        }
        sb.Append(']');
        return sb.ToString();
    }

    [Fact]
    public async Task FetchPremiumIndex_UsesCorrectEndpoint()
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
            .FetchPremiumIndexFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/fapi/v1/premiumIndexKlines", capturedUrl);
        Assert.Contains("symbol=BTCUSDT", capturedUrl);
        Assert.Contains("interval=1m", capturedUrl);
    }

    [Fact]
    public async Task FetchPremiumIndex_ParsesOhlc()
    {
        var json = BuildKlineJson((1_700_000_000_000L, "0.0001", "0.0002", "-0.0001", "0.00015"));
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchPremiumIndexFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(records);
        Assert.Equal(0.0001, records[0].Values[0], precision: 10);
        Assert.Equal(0.00015, records[0].Values[3], precision: 10);
    }

    [Fact]
    public async Task FetchIndexPrice_UsesPairQueryParam()
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
            .FetchIndexPriceFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/fapi/v1/indexPriceKlines", capturedUrl);
        // Index-price endpoint requires `pair=` not `symbol=`.
        Assert.Contains("pair=BTCUSDT", capturedUrl);
        Assert.DoesNotContain("symbol=", capturedUrl);
    }

    [Fact]
    public async Task FetchIndexPrice_ParsesOhlc()
    {
        var json = BuildKlineJson((1_700_000_000_000L, "50000.00", "51000.00", "49500.00", "50500.00"));
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var records = await client
            .FetchIndexPriceFeedAsync("BTCUSDT", "1m", 1_700_000_000_000L, 1_700_000_060_000L, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(records);
        Assert.Equal(50000.00, records[0].Values[0], precision: 10);
        Assert.Equal(50500.00, records[0].Values[3], precision: 10);
    }
}

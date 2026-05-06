using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceFuturesClientFundingInfoTests
{
    private static BinanceFuturesClient BuildClient(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var opts = new BinanceOptions { RequestDelayMs = 0 };
        var limiter = new SourceRateLimiter(
            new WeightedRateLimiter(maxWeightPerMinute: 2400, budgetPercent: 100));
        return new BinanceFuturesClient(httpClient, opts, limiter);
    }

    [Fact]
    public async Task FetchFundingInfo_ParsesEntries()
    {
        const string json = """
            [
              {"symbol":"BTCUSDT","adjustedFundingRateCap":"0.0300","adjustedFundingRateFloor":"-0.0300","fundingIntervalHours":8,"disclaimer":false},
              {"symbol":"ETHUSDT","adjustedFundingRateCap":"0.0200","adjustedFundingRateFloor":"-0.0200","fundingIntervalHours":4,"disclaimer":true}
            ]
            """;

        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse(json))
        };

        var client = BuildClient(handler);
        var entries = await client.FetchFundingInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);

        var btc = entries[0];
        Assert.Equal("BTCUSDT", btc.Symbol);
        Assert.Equal(0.03, btc.AdjustedFundingRateCap, precision: 10);
        Assert.Equal(-0.03, btc.AdjustedFundingRateFloor, precision: 10);
        Assert.Equal(8, btc.FundingIntervalHours);
        Assert.False(btc.Disclaimer);

        var eth = entries[1];
        Assert.Equal("ETHUSDT", eth.Symbol);
        Assert.Equal(4, eth.FundingIntervalHours);
        Assert.True(eth.Disclaimer);
    }

    [Fact]
    public async Task FetchFundingInfo_UsesCorrectEndpoint()
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
        await client.FetchFundingInfoAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/fapi/v1/fundingInfo", capturedUrl);
    }

    [Fact]
    public async Task FetchFundingInfo_EmptyResponse_ReturnsEmptyList()
    {
        var handler = new FakeHttpHandler
        {
            Handler = _ => Task.FromResult(FakeHttpHandler.JsonResponse("[]"))
        };

        var client = BuildClient(handler);
        var entries = await client.FetchFundingInfoAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }
}

using AlgoTradeForge.HistoryLoader.RateLimiting;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.RateLimiting;

public sealed class SourceRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_DelegatesToGlobalLimiter()
    {
        var global = new WeightedRateLimiter(1000, 100);
        var source = new SourceRateLimiter(global, "https://fapi.binance.com");

        await source.AcquireAsync(500, CancellationToken.None);

        Assert.Equal(500, global.CurrentWeight);
    }

    [Fact]
    public void BaseUrl_ReturnsConfiguredUrl()
    {
        var global = new WeightedRateLimiter(1000, 100);
        var source = new SourceRateLimiter(global, "https://api.binance.com");

        Assert.Equal("https://api.binance.com", source.BaseUrl);
    }

    [Fact]
    public async Task MultipleSources_ShareGlobalBudget()
    {
        var global = new WeightedRateLimiter(1000, 100);
        var futures = new SourceRateLimiter(global, "https://fapi.binance.com");
        var spot = new SourceRateLimiter(global, "https://api.binance.com");

        await futures.AcquireAsync(400, CancellationToken.None);
        await spot.AcquireAsync(400, CancellationToken.None);

        Assert.Equal(800, global.CurrentWeight);
    }
}

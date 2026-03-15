using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.RateLimiting;

public sealed class SourceRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_DelegatesToGlobalLimiter()
    {
        var global = new WeightedRateLimiter(1000, 100);
        var source = new SourceRateLimiter(global);

        await source.AcquireAsync(500, CancellationToken.None);

        Assert.Equal(500, global.CurrentWeight);
    }

    [Fact]
    public async Task MultipleSources_ShareGlobalBudget()
    {
        var global = new WeightedRateLimiter(1000, 100);
        var futures = new SourceRateLimiter(global);
        var spot = new SourceRateLimiter(global);

        await futures.AcquireAsync(400, CancellationToken.None);
        await spot.AcquireAsync(400, CancellationToken.None);

        Assert.Equal(800, global.CurrentWeight);
    }
}

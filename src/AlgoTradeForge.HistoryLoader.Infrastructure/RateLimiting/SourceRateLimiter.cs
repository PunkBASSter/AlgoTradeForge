namespace AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;

internal sealed class SourceRateLimiter(WeightedRateLimiter globalLimiter)
{
    public async Task AcquireAsync(int weight, CancellationToken ct)
    {
        await globalLimiter.AcquireAsync(weight, ct);
    }
}

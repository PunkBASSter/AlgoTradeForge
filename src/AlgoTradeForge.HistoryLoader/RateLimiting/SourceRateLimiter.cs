namespace AlgoTradeForge.HistoryLoader.RateLimiting;

internal sealed class SourceRateLimiter(WeightedRateLimiter globalLimiter, string baseUrl)
{
    public string BaseUrl => baseUrl;

    public async Task AcquireAsync(int weight, CancellationToken ct)
    {
        await globalLimiter.AcquireAsync(weight, ct);
    }
}

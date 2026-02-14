namespace AlgoTradeForge.CandleIngestor.DataSourceAdapters;

public sealed class RateLimiter
{
    private readonly Queue<DateTimeOffset> _timestamps = new();
    private readonly int _maxRequestsPerMinute;
    private readonly int _requestDelayMs;

    public RateLimiter(int maxRequestsPerMinute, int requestDelayMs)
    {
        _maxRequestsPerMinute = maxRequestsPerMinute;
        _requestDelayMs = requestDelayMs;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddMinutes(-1);

        while (_timestamps.Count > 0 && _timestamps.Peek() < windowStart)
            _timestamps.Dequeue();

        if (_timestamps.Count >= _maxRequestsPerMinute)
        {
            var oldestInWindow = _timestamps.Peek();
            var waitUntil = oldestInWindow.AddMinutes(1);
            var delay = waitUntil - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }

        if (_requestDelayMs > 0)
            await Task.Delay(_requestDelayMs, ct);

        _timestamps.Enqueue(DateTimeOffset.UtcNow);
    }
}

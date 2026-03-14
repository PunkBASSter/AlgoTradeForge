namespace AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;

/// <summary>
/// Thread-safe sliding-window rate limiter that tracks total API weight per minute.
/// </summary>
internal sealed class WeightedRateLimiter
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(60);

    private readonly int _budget;
    private readonly Queue<(long TicksUtc, int Weight)> _window = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <param name="maxWeightPerMinute">Exchange-declared weight ceiling per minute.</param>
    /// <param name="budgetPercent">Percentage of the ceiling to actually consume (1-100).</param>
    public WeightedRateLimiter(int maxWeightPerMinute, int budgetPercent)
    {
        if (maxWeightPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWeightPerMinute), "Must be positive.");
        if (budgetPercent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(budgetPercent), "Must be in range 1-100.");

        _budget = maxWeightPerMinute * budgetPercent / 100;
    }

    /// <summary>
    /// Returns the sum of weights recorded within the current 60-second window.
    /// </summary>
    public int CurrentWeight
    {
        get
        {
            lock (_window)
            {
                PurgeExpired();
                return SumWindow();
            }
        }
    }

    /// <summary>
    /// Blocks asynchronously until <paramref name="weight"/> units can be consumed within budget,
    /// then records the consumption.
    /// </summary>
    public async Task AcquireAsync(int weight, CancellationToken ct = default)
    {
        if (weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be positive.");

        // Intentionally hold the semaphore across the delay so that only one caller
        // polls the sliding window at a time, preventing thundering-herd bursts.
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                TimeSpan waitTime;
                lock (_window)
                {
                    PurgeExpired();
                    int current = SumWindow();

                    if (current + weight <= _budget)
                    {
                        _window.Enqueue((DateTime.UtcNow.Ticks, weight));
                        return;
                    }

                    // Calculate how long we must wait for enough weight to expire so
                    // that (remaining + weight) fits within budget.
                    waitTime = ComputeWaitTime(weight, current);
                }

                await Task.Delay(waitTime, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // -----------------------------------------------------------------
    // Private helpers  (must be called while holding _window lock)
    // -----------------------------------------------------------------

    private void PurgeExpired()
    {
        long cutoffTicks = (DateTime.UtcNow - WindowDuration).Ticks;
        while (_window.Count > 0 && _window.Peek().TicksUtc < cutoffTicks)
            _window.Dequeue();
    }

    private int SumWindow()
    {
        int sum = 0;
        foreach (var (_, w) in _window)
            sum += w;
        return sum;
    }

    /// <summary>
    /// Walk the queue oldest-first and determine the earliest moment at which enough
    /// entries will have expired so that <paramref name="weight"/> can be admitted.
    /// </summary>
    private TimeSpan ComputeWaitTime(int weight, int current)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        long windowTicks = WindowDuration.Ticks;

        int excess = current + weight - _budget;
        int freed = 0;
        long expireAtTicks = nowTicks; // fallback

        foreach (var (entryTicks, entryWeight) in _window)
        {
            freed += entryWeight;
            expireAtTicks = entryTicks + windowTicks;

            if (freed >= excess)
                break;
        }

        long waitTicks = expireAtTicks - nowTicks;
        // Add a small buffer (10 ms) to avoid re-checking too early.
        return waitTicks > 0
            ? TimeSpan.FromTicks(waitTicks) + TimeSpan.FromMilliseconds(10)
            : TimeSpan.FromMilliseconds(10);
    }
}

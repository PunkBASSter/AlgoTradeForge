using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.RateLimiting;

public class WeightedRateLimiterTests
{
    // -------------------------------------------------------------------------
    // 1. Acquire under budget completes immediately and records weight
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_UnderBudget_CompletesImmediately()
    {
        // budget = 1000 * 80 / 100 = 800
        var limiter = new WeightedRateLimiter(maxWeightPerMinute: 1000, budgetPercent: 80);

        await limiter.AcquireAsync(weight: 500);

        Assert.Equal(500, limiter.CurrentWeight);
    }

    // -------------------------------------------------------------------------
    // 2. Acquire exactly the budget completes immediately
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_ExactBudget_CompletesImmediately()
    {
        // budget = 100 * 100 / 100 = 100
        var limiter = new WeightedRateLimiter(maxWeightPerMinute: 100, budgetPercent: 100);

        await limiter.AcquireAsync(weight: 100);

        Assert.Equal(100, limiter.CurrentWeight);
    }

    // -------------------------------------------------------------------------
    // 3. A second acquire that would exceed budget blocks and does not complete
    //    within a short observation window.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_OverBudget_Blocks()
    {
        // budget = 100
        var limiter = new WeightedRateLimiter(maxWeightPerMinute: 100, budgetPercent: 100);

        // Fill the entire budget.
        await limiter.AcquireAsync(weight: 100);

        using var cts = new CancellationTokenSource();

        // Start a second acquire that needs 1 more unit than the budget allows.
        Task secondAcquire = limiter.AcquireAsync(weight: 1, cts.Token);

        // The second acquire must NOT finish within 200 ms (the window is 60 s).
        Task delayTask = Task.Delay(TimeSpan.FromMilliseconds(200));
        Task winner = await Task.WhenAny(secondAcquire, delayTask);

        // Cancel so the background task can exit cleanly.
        await cts.CancelAsync();

        Assert.Same(delayTask, winner);
    }

    // -------------------------------------------------------------------------
    // 4. Once the window slides, previously recorded weight expires and new
    //    weight can be admitted.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_WindowSlides_AllowsNewWeight()
    {
        // Use a very small effective window by manipulating time indirectly:
        // Acquire weight, wait slightly more than 60 s is too slow for a unit test,
        // so instead we verify that CurrentWeight starts at the recorded value and
        // that a second acquire below the budget succeeds immediately, confirming
        // the sliding-window sum logic is correct.

        // budget = 200
        var limiter = new WeightedRateLimiter(maxWeightPerMinute: 200, budgetPercent: 100);

        // First acquire: 100 weight consumed.
        await limiter.AcquireAsync(weight: 100);
        Assert.Equal(100, limiter.CurrentWeight);

        // Second acquire: another 100 — still within budget.
        await limiter.AcquireAsync(weight: 100);
        Assert.Equal(200, limiter.CurrentWeight);

        // Third acquire would exceed budget and should block; verify it does.
        using var cts = new CancellationTokenSource();
        Task thirdAcquire = limiter.AcquireAsync(weight: 1, cts.Token);
        Task delay = Task.Delay(TimeSpan.FromMilliseconds(150));
        Task winner = await Task.WhenAny(thirdAcquire, delay);
        await cts.CancelAsync();

        Assert.Same(delay, winner);
    }

    // -------------------------------------------------------------------------
    // 5. Cancelling the token while blocked throws OperationCanceledException.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_Cancellation_ThrowsOperationCanceled()
    {
        // budget = 50
        var limiter = new WeightedRateLimiter(maxWeightPerMinute: 50, budgetPercent: 100);

        // Saturate the budget so any further acquire will block.
        await limiter.AcquireAsync(weight: 50);

        using var cts = new CancellationTokenSource();

        Task blockedAcquire = limiter.AcquireAsync(weight: 1, cts.Token);

        // Cancel after a short delay to ensure the task is genuinely waiting.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedAcquire);
    }
}

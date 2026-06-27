using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class HistoricalTickPagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Simulates IB reqHistoricalTicks(startDateTime): returns up to pageSize ticks with TimeSec >= startSec,
    // ascending — including the WHOLE startSec second (the source of the cross-page overlap the pager dedups).
    private static Func<long, CancellationToken, Task<IReadOnlyList<IbHistoricalTick>>> Server(
        IReadOnlyList<IbHistoricalTick> all, int pageSize) =>
        (startSec, _) =>
            Task.FromResult<IReadOnlyList<IbHistoricalTick>>(
                all.Where(t => t.TimeSec >= startSec).Take(pageSize).ToList());

    private static IbHistoricalTick T(long sec, double price) => new(sec, price, 1m);

    [Fact]
    public async Task DistinctSeconds_AcrossPages_NoDuplicatesNoGaps()
    {
        // Two ticks at second 100, then 101, 102, 103 — pageSize 2 forces overlap re-reads of the boundary second.
        var src = new[] { T(100, 1), T(100, 2), T(101, 3), T(102, 4), T(103, 5) };
        var result = await HistoricalTickPager.Collect(Server(src, pageSize: 2), 100_000, 104_000, 2, Ct);

        Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }, result.Select(t => t.Price));
    }

    [Fact]
    public async Task SingleTickPerSecond_PageSizeOne_ReturnsAllOnce()
    {
        var src = new[] { T(10, 1), T(11, 2), T(12, 3) };
        var result = await HistoricalTickPager.Collect(Server(src, pageSize: 1), 10_000, 13_000, 1, Ct);

        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, result.Select(t => t.Price));
    }

    [Fact]
    public async Task ExcludesTicksAtOrBeyondToMs()
    {
        var src = new[] { T(100, 1), T(101, 2), T(102, 3), T(103, 4) };
        // toMs = 102_000 → second 102 (TimeSec*1000 == 102_000 >= toMs) and beyond are excluded.
        var result = await HistoricalTickPager.Collect(Server(src, pageSize: 10), 100_000, 102_000, 10, Ct);

        Assert.Equal(new[] { 1.0, 2.0 }, result.Select(t => t.Price));
    }

    [Fact]
    public async Task SecondExceedingPageSize_EscapesAndKeepsLaterSeconds_NoInfiniteLoop()
    {
        // Second 100 holds 3 ticks but pageSize is 2 → IB can only ever return its first 2; the 3rd is the
        // unrecoverable overflow. The pager must NOT loop forever and MUST still return the later second (101).
        var src = new[] { T(100, 1), T(100, 2), T(100, 3), T(101, 9) };
        var result = await HistoricalTickPager.Collect(Server(src, pageSize: 2), 100_000, 102_000, 2, Ct);

        Assert.Equal(new[] { 1.0, 2.0, 9.0 }, result.Select(t => t.Price)); // first 2 of sec 100 + sec 101; 3rd of sec 100 lost (IB limit)
    }

    [Fact]
    public async Task EmptyServer_ReturnsEmpty()
    {
        var result = await HistoricalTickPager.Collect(Server([], pageSize: 5), 0, 10_000, 5, Ct);
        Assert.Empty(result);
    }
}

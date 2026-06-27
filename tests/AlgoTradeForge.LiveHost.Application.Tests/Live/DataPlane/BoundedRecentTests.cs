using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.DataPlane;

public class BoundedRecentTests
{
    [Fact]
    public void Snapshot_WhenEmpty_ReturnsEmpty()
    {
        var recent = new BoundedRecent<int>(4);
        Assert.Empty(recent.Snapshot());
    }

    [Fact]
    public void Snapshot_BelowCapacity_ReturnsAllInInsertionOrder()
    {
        var recent = new BoundedRecent<int>(4);
        recent.Add(1);
        recent.Add(2);
        recent.Add(3);
        Assert.Equal(new[] { 1, 2, 3 }, recent.Snapshot());
    }

    [Fact]
    public void Snapshot_AboveCapacity_RetainsLastCapacityInOrder()
    {
        var recent = new BoundedRecent<int>(3);
        for (var i = 1; i <= 5; i++) recent.Add(i);
        Assert.Equal(new[] { 3, 4, 5 }, recent.Snapshot());
    }

    [Fact]
    public void Snapshot_ReturnsIndependentCopy()
    {
        var recent = new BoundedRecent<int>(3);
        recent.Add(1);
        var first = recent.Snapshot();
        recent.Add(2);
        Assert.Single(first);
        Assert.Equal(2, recent.Snapshot().Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_NonPositiveCapacity_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedRecent<int>(capacity));
    }

    [Fact]
    public async Task ConcurrentAddAndSnapshot_DoesNotThrowAndConverges()
    {
        var recent = new BoundedRecent<int>(64);
        var ct = TestContext.Current.CancellationToken;
        var tasks = new List<Task>();
        for (var t = 0; t < 4; t++)
            tasks.Add(Task.Run(() => { for (var i = 0; i < 1000; i++) recent.Add(i); }, ct));
        for (var t = 0; t < 4; t++)
            tasks.Add(Task.Run(() => { for (var i = 0; i < 1000; i++) _ = recent.Snapshot(); }, ct));

        await Task.WhenAll(tasks);

        Assert.Equal(64, recent.Snapshot().Count);
    }
}

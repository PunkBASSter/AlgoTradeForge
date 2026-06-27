using AlgoTradeForge.Domain.Collections;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Collections;

public sealed class RingBufferToArrayTests
{
    [Fact]
    public void ToArray_WhenEmpty_ReturnsEmpty()
    {
        var ring = new RingBuffer<int>(4);
        Assert.Empty(ring.ToArray());
    }

    [Fact]
    public void ToArray_BelowCapacity_ReturnsAllOldestToNewest()
    {
        var ring = new RingBuffer<int>(4);
        ring.Add(1);
        ring.Add(2);
        ring.Add(3);
        Assert.Equal(new[] { 1, 2, 3 }, ring.ToArray());
    }

    [Fact]
    public void ToArray_AfterWrap_ReturnsLastCapacityOldestToNewest()
    {
        var ring = new RingBuffer<int>(3);
        for (var i = 1; i <= 5; i++) ring.Add(i);
        Assert.Equal(new[] { 3, 4, 5 }, ring.ToArray());
    }

    [Fact]
    public void ToArray_IsRightSized()
    {
        var ring = new RingBuffer<int>(8);
        ring.Add(1);
        ring.Add(2);
        Assert.Equal(2, ring.ToArray().Length);
    }
}

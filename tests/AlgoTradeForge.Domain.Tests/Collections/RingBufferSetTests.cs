using AlgoTradeForge.Domain.Collections;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Collections;

public sealed class RingBufferSetTests
{
    [Fact]
    public void Set_WithinWindow_ValueReadable()
    {
        var ring = new RingBuffer<int>(4);
        for (var i = 0; i < 4; i++)
            ring.Add(i * 10); // [0, 10, 20, 30]

        ring.Set(2, 99); // overwrite index 2 (value 20 → 99)

        Assert.Equal(99, ring[2]);
        Assert.Equal(0, ring[0]);
        Assert.Equal(30, ring[3]);
    }

    [Fact]
    public void Set_EvictedIndex_SilentNoOp()
    {
        var ring = new RingBuffer<int>(3);
        for (var i = 0; i < 6; i++)
            ring.Add(i * 10); // retained: indices 3,4,5

        ring.Set(0, 999); // evicted — should be a no-op

        Assert.Equal(6, ring.Count);
        Assert.Equal(30, ring[3]); // oldest retained
        Assert.Equal(50, ring[5]); // newest
    }

    [Fact]
    public void Set_FutureIndex_SilentNoOp()
    {
        var ring = new RingBuffer<int>(3);
        ring.Add(10);

        ring.Set(5, 999); // future index — no-op

        Assert.True(ring.Count == 1);
        Assert.Equal(10, ring[0]);
    }

    [Fact]
    public void Set_LatestIndex_Overwritten()
    {
        var ring = new RingBuffer<int>(3);
        for (var i = 0; i < 5; i++)
            ring.Add(i); // retained: indices 2,3,4

        ring.Set(4, 42);

        Assert.Equal(42, ring[4]);
    }

    [Fact]
    public void Set_OldestRetainedIndex_Overwritten()
    {
        var ring = new RingBuffer<int>(3);
        for (var i = 0; i < 5; i++)
            ring.Add(i); // retained: indices 2,3,4

        ring.Set(2, 77);

        Assert.Equal(77, ring[2]);
    }

    [Fact]
    public void Set_NegativeIndex_SilentNoOp()
    {
        var ring = new RingBuffer<int>(3);
        ring.Add(10);

        ring.Set(-1, 999); // negative — no-op

        Assert.Equal(10, ring[0]);
    }
}

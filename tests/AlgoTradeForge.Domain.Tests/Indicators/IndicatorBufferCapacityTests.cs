using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public sealed class IndicatorBufferCapacityTests
{
    [Fact]
    public void Unbounded_DefaultBehavior_AllValuesRetained()
    {
        var buffer = new IndicatorBuffer<int>("test");
        for (var i = 0; i < 500; i++)
            buffer.Append(i);

        Assert.Equal(500, buffer.Count);
        Assert.Equal(0, buffer[0]);
        Assert.Equal(499, buffer[499]);
    }

    [Fact]
    public void Bounded_CountReturnsTotal_NotRetained()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 50; i++)
            buffer.Append(i);

        Assert.Equal(50, buffer.Count);
    }

    [Fact]
    public void Bounded_LatestValueAccessible()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 50; i++)
            buffer.Append(i);

        Assert.Equal(49, buffer[^1]);
        Assert.Equal(49, buffer[49]);
    }

    [Fact]
    public void Bounded_EvictedIndex_Throws()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 50; i++)
            buffer.Append(i);

        // Index 0 was evicted (only 40-49 retained)
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[0]);
    }

    [Fact]
    public void Bounded_OldestRetainedIndex_Accessible()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 50; i++)
            buffer.Append(i);

        // Oldest retained = 50 - 10 = index 40
        Assert.Equal(40, buffer[40]);
    }

    [Fact]
    public void Bounded_SetWithinWindow_Works()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 20; i++)
            buffer.Append(0);

        buffer.Set(15, 42);

        Assert.Equal(42, buffer[15]);
    }

    [Fact]
    public void Bounded_SetEvictedIndex_SilentNoOp()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 20; i++)
            buffer.Append(0);

        // Index 5 is evicted — this should not throw
        buffer.Set(5, 999);

        // Verify retained values are unchanged
        Assert.Equal(0, buffer[10]);
    }

    [Fact]
    public void Bounded_ReviseEvictedIndex_Throws()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 20; i++)
            buffer.Append(0);

        // Index 5 is evicted — revising it is a logic bug (capacity too small)
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Revise(5, 42));
        Assert.Contains("evicted", ex.Message);
    }

    [Fact]
    public void Bounded_ReviseWithinWindow_UpdatesValueAndFires()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        for (var i = 0; i < 15; i++)
            buffer.Append(0);

        var fired = false;
        buffer.OnRevised = (_, _, _) => fired = true;

        buffer.Revise(12, 99);

        Assert.Equal(99, buffer[12]);
        Assert.True(fired);
    }

    [Fact]
    public void Bounded_Enumerate_YieldsOnlyRetainedValues()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(5);

        for (var i = 0; i < 10; i++)
            buffer.Append(i);

        // Retained: indices 5-9 (values 5,6,7,8,9)
        var items = buffer.ToList();

        Assert.Equal(5, items.Count);
        Assert.Equal(5, items[0]);
        Assert.Equal(9, items[4]);
    }

    [Fact]
    public void SetCapacity_CalledTwice_Throws()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.SetCapacity(10);

        Assert.Throws<InvalidOperationException>(() => buffer.SetCapacity(20));
    }

    [Fact]
    public void SetCapacity_ZeroOrNegative_Throws()
    {
        var buffer = new IndicatorBuffer<int>("test");

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.SetCapacity(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.SetCapacity(-5));
    }

    [Fact]
    public void SetCapacity_AfterDataAdded_Throws()
    {
        var buffer = new IndicatorBuffer<int>("test");
        buffer.Append(1);

        Assert.Throws<InvalidOperationException>(() => buffer.SetCapacity(10));
    }

    // ── Integration: indicator with bounded buffer over long series ──

    [Fact]
    public void Sma_LongSeries_LatestValueCorrect_OldEvicted()
    {
        var sma = new Sma(10);
        // Default capacity = Max(10*2, 256) = 256

        var bars = Enumerable.Range(0, 500)
            .Select(i => new Int64Bar(i * 60_000L, 1000 + i, 1010 + i, 990 + i, 1000 + i, 100))
            .ToList();

        sma.Compute(bars);
        var values = sma.Buffers["Value"];

        Assert.Equal(500, values.Count);

        // Latest value should be SMA of last 10 closes: indices 490..499 → closes 1490..1499
        var expectedLast = Enumerable.Range(490, 10).Select(i => (long)(1000 + i)).Sum() / 10;
        Assert.Equal(expectedLast, values[^1]);

        // Index 0 is evicted (capacity 256, 500 added → indices 0-243 evicted)
        Assert.Throws<ArgumentOutOfRangeException>(() => values[0]);

        // Index 244 (=500-256) should still be accessible
        Assert.True(values[244] > 0);
    }

    [Fact]
    public void Rsi_LongSeries_LatestValueBounded()
    {
        var rsi = new Rsi(14);
        // Default capacity = Max(15*2, 256) = 256

        var bars = Enumerable.Range(0, 400)
            .Select(i => new Int64Bar(i * 60_000L, 1000 + (i % 20) * 10, 1100, 900, 1000 + (i % 20) * 10, 100))
            .ToList();

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        Assert.Equal(400, values.Count);
        Assert.InRange(values[^1], 0.0, 100.0);

        // Index 0 evicted
        Assert.Throws<ArgumentOutOfRangeException>(() => values[0]);
    }

    [Fact]
    public void Atr_LongSeries_LatestValueCorrect()
    {
        var atr = new Atr(14);

        var bars = Enumerable.Range(0, 400)
            .Select(i => new Int64Bar(i * 60_000L, 1000, 1050, 950, 1000, 100))
            .ToList();

        atr.Compute(bars);
        var values = atr.Buffers["Value"];

        Assert.Equal(400, values.Count);
        Assert.True(values[^1] > 0);

        // Index 0 evicted
        Assert.Throws<ArgumentOutOfRangeException>(() => values[0]);
    }
}

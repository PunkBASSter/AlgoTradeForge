using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>P2a-7: strict-monotonic timestamp invariant + bump counter.</summary>
public sealed class MonotonicTickSourceTests
{
    private static SourceRecord Tick(long ts, long price = 100, long qty = 1) =>
        new(ts, price, price, price, price, qty);

    [Fact]
    public void Read_AllSameMs_BumpsToStrictlyIncreasing_BumpCountIsNMinus1()
    {
        const long t = 1_700_000_000_000L;
        var input = Enumerable.Range(0, 50).Select(_ => Tick(t)).ToArray();

        var src = new MonotonicTickSource();
        var output = src.Read(input).ToList();

        Assert.Equal(50, output.Count);
        // First tick keeps raw ts; remaining 49 get bumped.
        Assert.Equal(t, output[0].TsMs);
        for (int i = 1; i < output.Count; i++)
        {
            Assert.True(output[i].TsMs > output[i - 1].TsMs,
                $"output[{i}].TsMs={output[i].TsMs} <= prev {output[i - 1].TsMs}");
        }
        Assert.Equal(t + 49, output[^1].TsMs);
        Assert.Equal(49L, src.BumpCount);
    }

    [Fact]
    public void Read_MixedClusters_AppliesMaxPrev1OrRaw()
    {
        // Input: [t, t, t+5, t+5, t+5]
        // Expected output: [t, t+1, t+5, t+6, t+7]
        // Bump count: 3 (positions 1, 3, 4)
        const long t = 1_700_000_000_000L;
        var input = new[] { Tick(t), Tick(t), Tick(t + 5), Tick(t + 5), Tick(t + 5) };

        var src = new MonotonicTickSource();
        var output = src.Read(input).ToList();

        Assert.Equal(t, output[0].TsMs);
        Assert.Equal(t + 1, output[1].TsMs);
        Assert.Equal(t + 5, output[2].TsMs);
        Assert.Equal(t + 6, output[3].TsMs);
        Assert.Equal(t + 7, output[4].TsMs);
        Assert.Equal(3L, src.BumpCount);
    }

    [Fact]
    public void Read_AlreadyStrictlyMonotonic_NoBumps()
    {
        const long t = 1_700_000_000_000L;
        var input = Enumerable.Range(0, 100).Select(i => Tick(t + i * 10)).ToArray();

        var src = new MonotonicTickSource();
        var output = src.Read(input).ToList();

        Assert.Equal(100, output.Count);
        for (int i = 0; i < output.Count; i++)
            Assert.Equal(t + i * 10, output[i].TsMs);
        Assert.Equal(0L, src.BumpCount);
    }

    [Fact]
    public void Read_EmptyInput_BumpCountZero()
    {
        var src = new MonotonicTickSource();
        var output = src.Read(Array.Empty<SourceRecord>()).ToList();
        Assert.Empty(output);
        Assert.Equal(0L, src.BumpCount);
    }

    [Fact]
    public void Read_OutOfOrderInput_BumpsToMonotonic()
    {
        // Out-of-order arrival shouldn't happen from Binance but the decorator must handle it.
        // Input: [10, 5, 20, 15] → output: [10, 11, 20, 21], bumps = 2
        var input = new[] { Tick(10), Tick(5), Tick(20), Tick(15) };

        var src = new MonotonicTickSource();
        var output = src.Read(input).ToList();

        Assert.Equal(10, output[0].TsMs);
        Assert.Equal(11, output[1].TsMs);
        Assert.Equal(20, output[2].TsMs);
        Assert.Equal(21, output[3].TsMs);
        Assert.Equal(2L, src.BumpCount);
    }

    [Fact]
    public void Read_PreservesPriceAndVolume_BumpsTsOnly()
    {
        const long t = 1_700_000_000_000L;
        var input = new[]
        {
            new SourceRecord(t, 5000, 5000, 5000, 5000, 100),
            new SourceRecord(t, 5050, 5050, 5050, 5050, 200),
        };

        var src = new MonotonicTickSource();
        var output = src.Read(input).ToList();

        Assert.Equal(5000, output[0].Open);
        Assert.Equal(5050, output[1].Open);
        Assert.Equal(100, output[0].Volume);
        Assert.Equal(200, output[1].Volume);
    }

    [Fact]
    public void Read_BumpCountResetsBetweenEnumerations()
    {
        var src = new MonotonicTickSource();
        _ = src.Read(new[] { Tick(1), Tick(1), Tick(1) }).ToList();
        Assert.Equal(2L, src.BumpCount);

        _ = src.Read(new[] { Tick(10), Tick(20) }).ToList();
        Assert.Equal(0L, src.BumpCount);  // re-initialized, no bumps in second run
    }
}

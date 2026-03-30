using AlgoTradeForge.Domain.Validation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation;

public class SimulationCacheTests
{
    [Fact]
    public void Constructor_ValidInput_Succeeds()
    {
        var timestamps = new long[] { 100, 200, 300 };
        var matrix = new double[][]
        {
            [1.0, 2.0, 3.0],
            [-1.0, 0.5, 1.5],
        };

        var cache = new SimulationCache(timestamps, matrix);

        Assert.Equal(2, cache.TrialCount);
        Assert.Equal(3, cache.BarCount);
    }

    [Fact]
    public void Constructor_MismatchedRowLength_Throws()
    {
        var timestamps = new long[] { 100, 200, 300 };
        var matrix = new double[][]
        {
            [1.0, 2.0, 3.0],
            [1.0, 2.0], // wrong length
        };

        var ex = Assert.Throws<ArgumentException>(() => new SimulationCache(timestamps, matrix));
        Assert.Contains("Trial 1", ex.Message);
    }

    [Fact]
    public void GetTrialPnl_ReturnsCorrectRow()
    {
        var cache = CreateTestCache();

        var row = cache.GetTrialPnl(0);

        Assert.Equal(3, row.Length);
        Assert.Equal(1.0, row[0]);
        Assert.Equal(2.0, row[1]);
        Assert.Equal(3.0, row[2]);
    }

    [Fact]
    public void GetBarPnl_ReturnsColumnSlice()
    {
        var cache = CreateTestCache();

        var col = cache.GetBarPnl(1);

        Assert.Equal(2, col.Length);
        Assert.Equal(2.0, col[0]);  // trial 0, bar 1
        Assert.Equal(0.5, col[1]);  // trial 1, bar 1
    }

    [Fact]
    public void SliceWindow_CreatesSubset()
    {
        var cache = CreateTestCache();

        var sliced = cache.SliceWindow(1, 3);

        Assert.Equal(2, sliced.BarCount);
        Assert.Equal(2, sliced.TrialCount);
        Assert.Equal(200, sliced.BarTimestamps[0]);
        Assert.Equal(300, sliced.BarTimestamps[1]);
        Assert.Equal(2.0, sliced.GetTrialPnl(0)[0]);
    }

    [Fact]
    public void SliceWindow_InvalidRange_Throws()
    {
        var cache = CreateTestCache();

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SliceWindow(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SliceWindow(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SliceWindow(0, 4));
    }

    [Fact]
    public void ComputeCumulativeEquity_CorrectRunningSum()
    {
        var cache = CreateTestCache();

        var equity = cache.ComputeCumulativeEquity(0, 100.0);

        Assert.Equal(3, equity.Length);
        Assert.Equal(101.0, equity[0]); // 100 + 1
        Assert.Equal(103.0, equity[1]); // 101 + 2
        Assert.Equal(106.0, equity[2]); // 103 + 3
    }

    [Fact]
    public void EmptyTrialMatrix_AllowedWithZeroBars()
    {
        var cache = new SimulationCache([], []);

        Assert.Equal(0, cache.TrialCount);
        Assert.Equal(0, cache.BarCount);
    }

    private static SimulationCache CreateTestCache()
    {
        var timestamps = new long[] { 100, 200, 300 };
        var matrix = new double[][]
        {
            [1.0, 2.0, 3.0],
            [-1.0, 0.5, 1.5],
        };
        return new SimulationCache(timestamps, matrix);
    }
}

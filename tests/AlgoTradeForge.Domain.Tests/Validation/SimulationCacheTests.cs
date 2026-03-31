using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using AlgoTradeForge.Domain.Validation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation;

public class SimulationCacheTests
{
    [Fact]
    public void Constructor_ValidInput_Succeeds()
    {
        var cache = CreateTestCache();

        Assert.Equal(2, cache.TrialCount);
        Assert.Equal(3, cache.MaxBarCount);
        Assert.Equal(3, cache.GetBarCount(0));
        Assert.Equal(3, cache.GetBarCount(1));
    }

    [Fact]
    public void Constructor_MismatchedTimestampAndPnlLength_Throws()
    {
        var ts1 = new long[] { 100, 200, 300 };
        var ts2 = new long[] { 100, 200 };
        var matrix = new double[][]
        {
            [1.0, 2.0, 3.0],
            [1.0, 2.0, 3.0], // 3 PnL values but only 2 timestamps
        };

        var ex = Assert.Throws<ArgumentException>(() => new SimulationCache([ts1, ts2], [0, 1], matrix));
        Assert.Contains("Trial 1", ex.Message);
    }

    [Fact]
    public void Constructor_VariableLengthTrials_Succeeds()
    {
        var ts1 = new long[] { 100, 200, 300 };
        var ts2 = new long[] { 100, 200 };
        var matrix = new double[][]
        {
            [1.0, 2.0, 3.0],
            [-1.0, 0.5],
        };

        var cache = new SimulationCache([ts1, ts2], [0, 1], matrix);

        Assert.Equal(2, cache.TrialCount);
        Assert.Equal(3, cache.MaxBarCount);
        Assert.Equal(3, cache.GetBarCount(0));
        Assert.Equal(2, cache.GetBarCount(1));
        Assert.Equal(100, cache.MinTimestamp);
        Assert.Equal(300, cache.MaxTimestamp);
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
    public void GetTrialTimestamps_ReturnsCorrectTimestamps()
    {
        var cache = CreateTestCache();

        var ts = cache.GetTrialTimestamps(0);

        Assert.Equal(3, ts.Length);
        Assert.Equal(100, ts[0]);
        Assert.Equal(200, ts[1]);
        Assert.Equal(300, ts[2]);
    }

    [Fact]
    public void FindTrialWindow_ReturnsCorrectRange()
    {
        var cache = CreateTestCache();

        // Window [100, 250) should include bars at 100, 200
        var (start, length) = cache.FindTrialWindow(0, 100, 250);
        Assert.Equal(0, start);
        Assert.Equal(2, length);

        // Window [200, 400) should include bars at 200, 300
        var (start2, length2) = cache.FindTrialWindow(0, 200, 400);
        Assert.Equal(1, start2);
        Assert.Equal(2, length2);

        // Empty window
        var (start3, length3) = cache.FindTrialWindow(0, 400, 500);
        Assert.Equal(0, length3);
    }

    [Fact]
    public void FindTrialWindow_FullRange_ReturnsAllBars()
    {
        var cache = CreateTestCache();

        var (start, length) = cache.FindTrialWindow(0, 0, long.MaxValue);
        Assert.Equal(0, start);
        Assert.Equal(3, length);
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
        var cache = new SimulationCache([], [], []);

        Assert.Equal(0, cache.TrialCount);
        Assert.Equal(0, cache.MaxBarCount);
    }

    [Fact]
    public void MinMaxTimestamp_ComputedCorrectly()
    {
        var ts1 = new long[] { 50, 100, 200 };
        var ts2 = new long[] { 75, 300 };
        var matrix = new double[][] { [1, 2, 3], [4, 5] };

        var cache = new SimulationCache([ts1, ts2], [0, 1], matrix);

        Assert.Equal(50, cache.MinTimestamp);
        Assert.Equal(300, cache.MaxTimestamp);
    }

    private static SimulationCache CreateTestCache()
    {
        var ts = new long[] { 100, 200, 300 };
        var matrix = new double[][]
        {
            [1.0, 2.0, 3.0],
            [-1.0, 0.5, 1.5],
        };
        return SimulationCacheTestHelper.Create(ts, matrix);
    }
}

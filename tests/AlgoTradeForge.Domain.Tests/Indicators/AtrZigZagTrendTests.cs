using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public class AtrZigZagTrendTests
{
    private static AtrZigZagTrend Create(double multiplier = 2.0, int atrPeriod = 2, int numberOfLevels = 1)
        => new(multiplier, atrPeriod, numberOfLevels);

    /// <summary>
    /// Shared scenario, hand-computed Wilder ATR (period=2, multiplier=2):
    ///   TR: 20,20,40,30,30,60,90,135,205 → ATR: -,20,30,30,30,45,67.5,101.25,153.125.
    ///   Bar 3: bootstrap breach up (1060-990=70 ≥ 60), anchor 1060@3.
    ///   Bar 4: relocate high → 1070@4 (extAtr=30, threshold 60).
    ///   Bar 5: 1070-1005=65 ≥ 60 → rev down; maxLevels+=1070; low 1005@5 (extAtr=45).
    ///   Bar 6: 1100-1005=95 ≥ 90 → rev up; minLevels+=1005; high 1100@6 (extAtr=67.5).
    ///   Bar 7: 1100-960=140 ≥ 135 → rev down; maxLevels+=1100; low 960@7 (extAtr=101.25).
    ///   Bar 8: 1170-960=210 ≥ 202.5 → rev up; minLevels+=960; high 1170@8.
    /// Trend (levels=1): warm at bar 6 (both arrays filled): 1100>1070 → +1;
    ///   bar 7: 960&lt;1005 → -1; bar 8: 1170>1100 → +1.
    /// </summary>
    private static List<Int64Bar> TrendScenarioBars() =>
    [
        TestBars.Create(1000, 1010, 990, 1000),
        TestBars.Create(1000, 1010, 990, 1000),
        TestBars.Create(1000, 1040, 1000, 1030),
        TestBars.Create(1030, 1060, 1030, 1050),
        TestBars.Create(1050, 1070, 1040, 1060),
        TestBars.Create(1060, 1065, 1005, 1010),
        TestBars.Create(1060, 1100, 1055, 1090),
        TestBars.Create(1090, 1095, 960, 970),
        TestBars.Create(970, 1170, 965, 1160),
    ];

    // --- Pivot parity with AtrZigZag ---

    [Fact]
    public void PivotParity_ValueBufferMatchesAtrZigZag()
    {
        var bars = TrendScenarioBars();

        var plain = new AtrZigZag(2.0, 2);
        plain.Compute(bars);
        var plainValues = plain.Buffers["Value"].ToList();

        var trend = Create();
        trend.Compute(bars);
        var trendValues = trend.Buffers["Value"].ToList();

        Assert.Equal(plainValues.Count, trendValues.Count);
        for (var i = 0; i < plainValues.Count; i++)
            Assert.Equal(plainValues[i], trendValues[i]);
    }

    [Fact]
    public void PivotSequence_MatchesHandTrace()
    {
        var ind = Create();
        ind.Compute(TrendScenarioBars());

        var v = ind.Buffers["Value"];
        Assert.Equal(0L, v[3]);    // bootstrap anchor relocated away by bar 4
        Assert.Equal(1070L, v[4]);
        Assert.Equal(1005L, v[5]);
        Assert.Equal(1100L, v[6]);
        Assert.Equal(960L, v[7]);
        Assert.Equal(1170L, v[8]);
    }

    // --- Trend warmup ---

    [Fact]
    public void TrendIsZero_DuringAtrWarmupBootstrapAndLevelWarmup()
    {
        var ind = Create();
        ind.Compute(TrendScenarioBars());

        var trend = ind.Buffers["Trend"];
        // Bars 0-4: ATR warmup, bootstrap, first swing — no levels recorded.
        // Bar 5: only maxLevels populated → still not warm.
        Assert.All(Enumerable.Range(0, 6), i => Assert.Equal(0L, trend[i]));
    }

    [Fact]
    public void MoreLevels_LongerTrendWarmup()
    {
        var bars = TrendScenarioBars();

        var one = Create(numberOfLevels: 1);
        one.Compute(bars);
        Assert.NotEqual(0L, one.Buffers["Trend"][6]); // warm at bar 6 (1 high + 1 low)

        var two = Create(numberOfLevels: 2);
        two.Compute(bars);
        var trend2 = two.Buffers["Trend"];
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(0L, trend2[i]));
        Assert.Equal(1L, trend2[8]); // 2 highs + 2 lows complete at bar 8; 1170 > max(1070,1100)
    }

    // --- Trend flips ---

    [Fact]
    public void Uptrend_WhenHighBreaksMaxLevel()
    {
        var ind = Create();
        ind.Compute(TrendScenarioBars());

        // Bar 6: reversal up makes minLevels=[1005]; in-progress high 1100 > max(maxLevels)=1070
        Assert.Equal(1L, ind.Buffers["Trend"][6]);
    }

    [Fact]
    public void Downtrend_WhenLowBreaksMinLevel()
    {
        var ind = Create();
        ind.Compute(TrendScenarioBars());

        // Bar 7: reversal down; in-progress low 960 < min(minLevels)=1005
        Assert.Equal(-1L, ind.Buffers["Trend"][7]);
        // Bar 8: 1170 > max(maxLevels)=1100 → back up
        Assert.Equal(1L, ind.Buffers["Trend"][8]);
    }

    [Fact]
    public void TrendPersists_WhenNoLevelBroken()
    {
        var bars = TrendScenarioBars();
        // Quiet bar: no extension (H<1170), no reversal (1170-1100=70 < 2*153.125),
        // no level break → trend stays up.
        bars.Add(TestBars.Create(1160, 1165, 1100, 1120));

        var ind = Create();
        ind.Compute(bars);

        Assert.Equal(1L, ind.Buffers["Trend"][9]);
    }

    // --- Breakout level buffers ---

    [Fact]
    public void BreakoutBuffers_TrackRecordedLevels()
    {
        var ind = Create();
        ind.Compute(TrendScenarioBars());

        var high = ind.Buffers["BreakoutHigh"];
        var low = ind.Buffers["BreakoutLow"];

        Assert.Equal(0L, high[4]);     // no levels before the first reversal
        Assert.Equal(0L, low[4]);
        Assert.Equal(1070L, high[5]);  // first swing high recorded
        Assert.Equal(0L, low[5]);
        Assert.Equal(1070L, high[6]);
        Assert.Equal(1005L, low[6]);   // first swing low recorded
        Assert.Equal(1100L, high[7]);  // levels=1: replaced by the newer swing high
        Assert.Equal(960L, low[8]);
    }

    // --- Incremental consistency ---

    [Fact]
    public void IncrementalConsistency_SameResultAsBatch()
    {
        var bars = TrendScenarioBars();
        bars.Add(TestBars.Create(1160, 1165, 1100, 1120));

        var batch = Create();
        batch.Compute(bars);

        var incr = Create();
        for (var n = 1; n <= bars.Count; n++)
            incr.Compute(bars.Take(n).ToList());

        foreach (var name in new[] { "Value", "Trend", "BreakoutHigh", "BreakoutLow" })
        {
            var b = batch.Buffers[name].ToList();
            var i = incr.Buffers[name].ToList();
            Assert.Equal(b.Count, i.Count);
            for (var k = 0; k < b.Count; k++)
                Assert.Equal(b[k], i[k]);
        }
    }

    // --- Edge cases ---

    [Fact]
    public void EmptySeries_NoBufferEntries()
    {
        var ind = Create();
        ind.Compute(new List<Int64Bar>());

        Assert.Empty(ind.Buffers["Value"]);
        Assert.Empty(ind.Buffers["Trend"]);
        Assert.Empty(ind.Buffers["BreakoutHigh"]);
        Assert.Empty(ind.Buffers["BreakoutLow"]);
    }

    [Fact]
    public void InvalidParameters_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZagTrend(0.0, 14, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZagTrend(-1.0, 14, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZagTrend(2.0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZagTrend(2.0, -5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZagTrend(2.0, 14, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZagTrend(2.0, 14, -1));
    }

    [Fact]
    public void BufferMetadata_Correct()
    {
        var ind = Create();

        Assert.True(ind.Buffers["Value"].SkipDefaultValues);
        Assert.Null(ind.Buffers["Value"].ExportChartId);

        Assert.False(ind.Buffers["Trend"].SkipDefaultValues);
        Assert.Equal(1, ind.Buffers["Trend"].ExportChartId);

        Assert.False(ind.Buffers["BreakoutHigh"].SkipDefaultValues);
        Assert.False(ind.Buffers["BreakoutLow"].SkipDefaultValues);
    }

    [Fact]
    public void MinimumHistory_IsAtrPeriodPlusOne()
    {
        Assert.Equal(15, Create(atrPeriod: 14).MinimumHistory);
    }

    [Fact]
    public void CapacityLimit_IsUnbounded()
    {
        Assert.Equal(0, Create().CapacityLimit);
    }
}

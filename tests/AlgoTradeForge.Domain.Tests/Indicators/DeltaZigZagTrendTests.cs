using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public class DeltaZigZagTrendTests
{
    private static DeltaZigZagTrend Create(double reversalPct = 5.0, int numberOfLevels = 2)
        => new(reversalPct, numberOfLevels);

    // --- Basic zigzag pivots ---

    [Fact]
    public void RisingHighs_RelocatePivot()
    {
        var ind = Create();
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            TestBars.Create(1150, 1300, 1100, 1250),
        };

        ind.Compute(bars);

        var v = ind.Buffers["Value"];
        Assert.Equal(0L, v[0]);
        Assert.Equal(0L, v[1]);
        Assert.Equal(1300L, v[2]);
    }

    [Fact]
    public void ReversalDetection_DropExceedingThreshold()
    {
        // reversalPct=5%, extremum=1300 → threshold=65. Need low < 1300-65=1235.
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 950, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            TestBars.Create(1150, 1300, 1100, 1250),
            // Low=1200 < 1235 → reversal
            TestBars.Create(1250, 1260, 1200, 1220),
        };

        ind.Compute(bars);

        var v = ind.Buffers["Value"];
        Assert.Equal(1300L, v[2]); // high pivot preserved
        Assert.Equal(1200L, v[3]); // low reversal pivot
    }

    [Fact]
    public void ThresholdScalesWithExtremum_NotClose()
    {
        // Key difference from DeltaZigZag: threshold is % of the extremum price,
        // not the close price. With high=2000, 5% → threshold=100.
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1800, 2000, 1750, 1900),  // High=2000 → threshold=100
            // Low=1920 > 2000-100=1900 → no reversal (close=1910 is irrelevant)
            TestBars.Create(1900, 1950, 1920, 1910),
        };

        ind.Compute(bars);

        var pivots = ind.Buffers["Value"].Where(v => v != 0L).ToList();
        Assert.Single(pivots); // no reversal
        Assert.Equal(2000L, pivots[0]);
    }

    // --- Trend warmup ---

    [Fact]
    public void TrendIsZero_UntilBothLevelArraysPopulated()
    {
        // numberOfLevels=2 → need 2 swing highs AND 2 swing lows
        var ind = Create(reversalPct: 5.0, numberOfLevels: 2);
        var bars = BuildTrendTestBars();

        ind.Compute(bars);

        var trend = ind.Buffers["Trend"];
        // First few bars during warmup should be 0
        Assert.Equal(0L, trend[0]);
    }

    [Fact]
    public void TrendBecomesNonZero_AfterWarmup()
    {
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);

        // With numberOfLevels=1: need 1 swing high + 1 swing low.
        // After first up→down reversal we have 1 maxLevel.
        // After first down→up reversal we have 1 minLevel. Trend activates.
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 950, 1050),    // up, pivot at 1100
            TestBars.Create(1050, 1200, 1000, 1150),    // relocate to 1200
            // threshold=5% of 1200=60. Low=1100 < 1200-60=1140 → reversal down. maxLevels=[1200]
            TestBars.Create(1150, 1160, 1100, 1120),
            // threshold=5% of 1100=55. High=1180 > 1100+55=1155 → reversal up. minLevels=[1100]
            // Low must be >= 1100 to avoid relocation. Now both arrays populated → trend activates
            TestBars.Create(1110, 1180, 1100, 1150),
        };

        ind.Compute(bars);

        var trend = ind.Buffers["Trend"];
        // Bar 3 is the first bar where both maxLevels and minLevels have 1 entry
        Assert.NotEqual(0L, trend[3]);
    }

    // --- Uptrend detection ---

    [Fact]
    public void UptrendDetection_HighExceedsMaxLevels()
    {
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);
        var bars = BuildUptrendDetectionBars();

        ind.Compute(bars);

        var trend = ind.Buffers["Trend"];
        // After warmup, when highValue > max(maxLevels) → upTrend = true → Trend = 1
        var lastTrend = trend[^1];
        Assert.Equal(1L, lastTrend);
    }

    // --- Downtrend detection ---

    [Fact]
    public void DowntrendDetection_LowBreaksMinLevels()
    {
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);
        var bars = BuildDowntrendDetectionBars();

        ind.Compute(bars);

        var trend = ind.Buffers["Trend"];
        var lastTrend = trend[^1];
        Assert.Equal(-1L, lastTrend);
    }

    // --- Trend persistence ---

    [Fact]
    public void TrendPersists_WhenSwingsDontBreakLevels()
    {
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);
        var bars = BuildUptrendDetectionBars();

        // Add bars that don't break the min level
        bars.Add(TestBars.Create(1350, 1400, 1340, 1370));
        bars.Add(TestBars.Create(1370, 1410, 1350, 1390));

        ind.Compute(bars);

        var trend = ind.Buffers["Trend"];
        Assert.Equal(1L, trend[^1]);
        Assert.Equal(1L, trend[^2]);
    }

    // --- Mid-swing trend change ---

    [Fact]
    public void MidSwingTrendChange_InProgressExtremumExceedsBreakoutLevel()
    {
        // Trend evaluated every bar based on in-progress highValue/lowValue,
        // not just at reversals.
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);

        var bars = new List<Int64Bar>
        {
            // Phase 1: establish pivots
            TestBars.Create(1000, 1100, 950, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            // Reversal down: threshold=60, low=1100<1140. maxLevels=[1200]
            TestBars.Create(1150, 1160, 1100, 1120),
            // Reversal up: threshold=55, high=1180>1155. minLevels=[1100]. Low >= 1100 to avoid relocation.
            TestBars.Create(1110, 1180, 1100, 1150),
            // Continue up: new high=1250 > maxLevels[0]=1200 → upTrend mid-swing
            TestBars.Create(1150, 1250, 1140, 1230),
        };

        ind.Compute(bars);

        var trend = ind.Buffers["Trend"];
        Assert.Equal(1L, trend[4]); // trend flips mid-swing
    }

    // --- Breakout level buffers ---

    [Fact]
    public void BreakoutBuffers_ZeroDuringWarmup()
    {
        var ind = Create(reversalPct: 5.0, numberOfLevels: 2);
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 950, 1050),
        };

        ind.Compute(bars);

        // No levels populated yet
        Assert.Equal(0L, ind.Buffers["BreakoutHigh"][0]);
        Assert.Equal(0L, ind.Buffers["BreakoutLow"][0]);
    }

    [Fact]
    public void BreakoutBuffers_ShowCorrectMaxMinAfterReversals()
    {
        var ind = Create(reversalPct: 5.0, numberOfLevels: 1);

        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 950, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            // Reversal down: maxLevels=[1200]
            TestBars.Create(1150, 1160, 1100, 1120),
        };

        ind.Compute(bars);

        Assert.Equal(1200L, ind.Buffers["BreakoutHigh"][2]);
        Assert.Equal(0L, ind.Buffers["BreakoutLow"][2]); // no min levels yet
    }

    // --- numberOfLevels comparison ---

    [Fact]
    public void MoreLevels_RequiresMoreFilteringForTrendChange()
    {
        // With levels=1, a single swing high exceeding max → uptrend.
        // With levels=2, need to exceed the max of 2 prior swing highs.
        var bars = BuildMultiSwingBars();

        var ind1 = Create(reversalPct: 5.0, numberOfLevels: 1);
        ind1.Compute(bars);
        var trend1 = ind1.Buffers["Trend"].ToList();

        var ind2 = Create(reversalPct: 5.0, numberOfLevels: 2);
        ind2.Compute(bars);
        var trend2 = ind2.Buffers["Trend"].ToList();

        // levels=2 should have more 0L warmup bars (needs 2 highs + 2 lows vs 1+1)
        var warmup1 = trend1.Count(t => t == 0L);
        var warmup2 = trend2.Count(t => t == 0L);
        Assert.True(warmup2 > warmup1, "More levels should mean longer warmup");
    }

    // --- Incremental consistency ---

    [Fact]
    public void IncrementalConsistency_BatchEqualsIncremental()
    {
        var bars = BuildMultiSwingBars();

        // Batch
        var batch = Create(reversalPct: 5.0, numberOfLevels: 1);
        batch.Compute(bars);
        var batchValue = batch.Buffers["Value"].ToList();
        var batchTrend = batch.Buffers["Trend"].ToList();

        // Incremental: 5 bars then all
        var incr = Create(reversalPct: 5.0, numberOfLevels: 1);
        incr.Compute(bars.Take(5).ToList());
        incr.Compute(bars);
        var incrValue = incr.Buffers["Value"].ToList();
        var incrTrend = incr.Buffers["Trend"].ToList();

        Assert.Equal(batchValue.Count, incrValue.Count);
        for (var i = 0; i < batchValue.Count; i++)
        {
            Assert.Equal(batchValue[i], incrValue[i]);
            Assert.Equal(batchTrend[i], incrTrend[i]);
        }
    }

    // --- Pivot relocation fires OnRevised ---

    [Fact]
    public void PivotRelocation_FiresOnRevised()
    {
        var ind = Create();
        var revisions = new List<(string Name, int Index, long Value)>();
        ind.Buffers["Value"].OnRevised = (name, index, value) =>
            revisions.Add((name, index, value));

        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
        };

        ind.Compute(bars);

        Assert.Single(revisions);
        Assert.Equal("Value", revisions[0].Name);
        Assert.Equal(0, revisions[0].Index);
        Assert.Equal(0L, revisions[0].Value);
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
    public void SingleBar_PivotAtBarZero()
    {
        var ind = Create();
        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050) };

        ind.Compute(bars);

        Assert.Equal(1100L, ind.Buffers["Value"][0]);
        Assert.Equal(0L, ind.Buffers["Trend"][0]);
    }

    [Fact]
    public void InvalidReversalPct_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaZigZagTrend(0.0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaZigZagTrend(-1.0, 1));
    }

    [Fact]
    public void InvalidNumberOfLevels_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaZigZagTrend(5.0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaZigZagTrend(5.0, -1));
    }

    // --- Buffer metadata ---

    [Fact]
    public void BufferMetadata_Correct()
    {
        var ind = Create();

        Assert.True(ind.Buffers["Value"].SkipDefaultValues);
        Assert.Null(ind.Buffers["Value"].ExportChartId);

        Assert.False(ind.Buffers["Trend"].SkipDefaultValues);
        Assert.Equal(1, ind.Buffers["Trend"].ExportChartId);

        Assert.False(ind.Buffers["BreakoutHigh"].SkipDefaultValues);
        Assert.Null(ind.Buffers["BreakoutHigh"].ExportChartId);

        Assert.False(ind.Buffers["BreakoutLow"].SkipDefaultValues);
        Assert.Null(ind.Buffers["BreakoutLow"].ExportChartId);
    }

    // --- Helpers ---

    /// <summary>Builds bars with multiple up/down swings for trend testing (numberOfLevels=2).</summary>
    private static List<Int64Bar> BuildTrendTestBars()
    {
        return
        [
            TestBars.Create(1000, 1100, 950, 1050),    // 0: up
            TestBars.Create(1050, 1200, 1000, 1150),    // 1: up
            TestBars.Create(1150, 1160, 1100, 1120),    // 2: reversal down (threshold 60, L=1100<1140)
            TestBars.Create(1110, 1180, 1100, 1150),    // 3: reversal up (threshold 55, H=1180>1155). Low>=1100.
            TestBars.Create(1150, 1250, 1140, 1230),    // 4: up
            TestBars.Create(1230, 1240, 1140, 1160),    // 5: reversal down (threshold 63, L=1140<1187)
            TestBars.Create(1160, 1220, 1140, 1200),    // 6: reversal up (threshold 57, H=1220>1197). Low>=1140.
            TestBars.Create(1200, 1300, 1180, 1280),    // 7: up
        ];
    }

    /// <summary>
    /// Bars that result in an uptrend: warmup completes, then high breaks max level.
    /// Uses numberOfLevels=1.
    /// </summary>
    private static List<Int64Bar> BuildUptrendDetectionBars()
    {
        return
        [
            TestBars.Create(1000, 1100, 950, 1050),    // 0: up
            TestBars.Create(1050, 1200, 1000, 1150),    // 1: up
            TestBars.Create(1150, 1160, 1100, 1120),    // 2: reversal down. maxLevels=[1200]
            TestBars.Create(1110, 1180, 1100, 1150),    // 3: reversal up. minLevels=[1100]. Warmup done. Low>=1100.
            TestBars.Create(1150, 1250, 1140, 1230),    // 4: highValue=1250 > max(1200) → upTrend
        ];
    }

    /// <summary>
    /// Bars that result in a downtrend: warmup completes, then low breaks min level.
    /// Uses numberOfLevels=1.
    /// </summary>
    private static List<Int64Bar> BuildDowntrendDetectionBars()
    {
        return
        [
            TestBars.Create(1000, 1100, 950, 1050),    // 0: up
            TestBars.Create(1050, 1200, 1000, 1150),    // 1: up
            TestBars.Create(1150, 1160, 1100, 1120),    // 2: reversal down. maxLevels=[1200]
            TestBars.Create(1110, 1180, 1100, 1150),    // 3: reversal up. minLevels=[1100]. Warmup done. Low>=1100.
            TestBars.Create(1150, 1250, 1140, 1230),    // 4: highValue=1250>1200 → upTrend
            TestBars.Create(1230, 1240, 1140, 1160),    // 5: reversal down (threshold 63). maxLevels=[1250]
            // Now start going down deeply. lowValue must break min(minLevels)=1100
            TestBars.Create(1160, 1170, 1080, 1100),    // 6: low relocates to 1080. 1080 < 1100 → downTrend
        ];
    }

    /// <summary>
    /// Many swings for incremental consistency and levels comparison testing.
    /// </summary>
    private static List<Int64Bar> BuildMultiSwingBars()
    {
        return
        [
            TestBars.Create(1000, 1100, 950, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            TestBars.Create(1150, 1300, 1100, 1250),
            TestBars.Create(1250, 1260, 1180, 1200),     // reversal down
            TestBars.Create(1200, 1210, 1150, 1170),     // extend low
            TestBars.Create(1170, 1280, 1160, 1260),     // reversal up
            TestBars.Create(1260, 1350, 1240, 1320),
            TestBars.Create(1320, 1360, 1240, 1260),     // reversal down
            TestBars.Create(1260, 1270, 1200, 1220),     // extend low
            TestBars.Create(1220, 1340, 1210, 1320),     // reversal up
            TestBars.Create(1320, 1400, 1300, 1380),
            TestBars.Create(1380, 1410, 1290, 1310),     // reversal down
            TestBars.Create(1310, 1430, 1300, 1410),     // reversal up
        ];
    }
}

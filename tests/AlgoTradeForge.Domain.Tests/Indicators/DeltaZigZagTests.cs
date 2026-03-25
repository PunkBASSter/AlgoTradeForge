using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public class DeltaZigZagTests
{
    private static DeltaZigZag CreateIndicator(double delta = 0.5, double minThresholdPct = 10.0)
        => new(delta, minThresholdPct);

    [Fact]
    public void Uptrend_PivotAtFirstHigh()
    {
        var dzz = CreateIndicator();
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            TestBars.Create(1150, 1300, 1100, 1250),
        };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        Assert.Equal(3, values.Count);

        // In an uptrend, the pivot relocates to the latest highest bar.
        // Bar 0 high=1100, bar 1 high=1200 (relocates), bar 2 high=1300 (relocates again)
        // Previous pivots get zeroed out, only the latest has the value.
        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);
        Assert.Equal(1300L, values[2]);
    }

    [Fact]
    public void DowntrendReversal_TwoPivots()
    {
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 10.0);

        // Ascending bars then a big drop exceeding threshold
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),   // High=1100, pivot here initially
            TestBars.Create(1050, 1200, 1000, 1150),   // High=1200, pivot relocates
            TestBars.Create(1150, 1300, 1100, 1250),   // High=1300, pivot relocates
            // Now drop: Low=1100. Threshold = 10% of close(1150)=115 (no prior swing).
            // 1100 < 1300 - 115 = 1185 → reversal confirmed
            TestBars.Create(1200, 1250, 1100, 1150),
        };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        Assert.Equal(4, values.Count);

        // Bar 2 keeps the high pivot (1300), bar 3 gets the low reversal pivot
        Assert.Equal(1300L, values[2]);
        Assert.Equal(1100L, values[3]);
    }

    [Fact]
    public void DynamicThreshold_UsesLastSwingSizeDelta()
    {
        // delta=0.5, minThresholdPct=1. First reversal uses pct floor (~14 at close 1400).
        // Second reversal uses dynamic = swingSize * 0.5, which is much larger.
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 1.0);

        var bars = new List<Int64Bar>
        {
            // Phase 1: uptrend to 1500
            TestBars.Create(1000, 1100, 950, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            TestBars.Create(1150, 1500, 1100, 1400),  // High=1500, pivot here

            // Phase 2: reversal down (floor=1% of 1420=14, L=1400 < 1500-14=1486)
            // swingSize = 1500 - 1400 = 100
            TestBars.Create(1350, 1450, 1400, 1420),

            // Phase 3: extend down pivot to 1300
            TestBars.Create(1400, 1430, 1300, 1350),  // Low=1300, pivot relocates

            // Phase 4: reverse up. Dynamic = max(100*0.5, 1% of 1340=13) = 50.
            // Need High > 1300 + 50 = 1350. L must NOT be < 1300 (to avoid relocation first).
            TestBars.Create(1310, 1360, 1305, 1340),  // L=1305 >= 1300, H=1360 > 1350 → reversal!
        };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        var pivots = values.Where(v => v != 0L).ToList();

        // Should have 3 pivots: 1500 (high), 1300 (low), 1360 (high)
        Assert.Equal(3, pivots.Count);
        Assert.Equal(1500L, pivots[0]);
        Assert.Equal(1300L, pivots[1]);
        Assert.Equal(1360L, pivots[2]);
    }

    [Fact]
    public void MinimumThreshold_UsedWhenNoPriorSwing()
    {
        // With a very large percentage threshold, no reversal should happen
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 99.0);

        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            // Drop of 300 (1200 - 900), but threshold is 10000, so no reversal
            TestBars.Create(1100, 1150, 900, 950),
        };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        // Only the ongoing uptrend pivot should exist (relocated to bar with highest high)
        var nonZero = values.Where(v => v != 0L).ToList();
        Assert.Single(nonZero); // Only one pivot, no reversal
    }

    [Fact]
    public void IncrementalConsistency_SameResultAsBatch()
    {
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            TestBars.Create(1150, 1300, 1100, 1250),
            TestBars.Create(1200, 1250, 1050, 1100),
            TestBars.Create(1100, 1150, 900, 950),
            TestBars.Create(950, 1000, 850, 900),
            TestBars.Create(900, 1100, 880, 1080),
            TestBars.Create(1080, 1200, 1050, 1180),
            TestBars.Create(1180, 1350, 1150, 1300),
            TestBars.Create(1300, 1400, 1250, 1350),
        };

        // Batch: compute all 10 bars at once
        var batchDzz = CreateIndicator(delta: 0.5, minThresholdPct: 10.0);
        batchDzz.Compute(bars);
        var batchValues = batchDzz.Buffers["Value"].ToList();

        // Incremental: compute 5 bars, then 10 bars
        var incrDzz = CreateIndicator(delta: 0.5, minThresholdPct: 10.0);
        incrDzz.Compute(bars.Take(5).ToList());
        incrDzz.Compute(bars);
        var incrValues = incrDzz.Buffers["Value"].ToList();

        Assert.Equal(batchValues.Count, incrValues.Count);
        for (var i = 0; i < batchValues.Count; i++)
        {
            Assert.Equal(batchValues[i], incrValues[i]);
        }
    }

    [Fact]
    public void PivotRelocation_OldPivotZeroed()
    {
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 10.0);

        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 950, 1050),   // High=1100, pivot at 0
            TestBars.Create(1050, 1200, 1000, 1150),   // High=1200 > 1100, pivot moves to 1
        };

        dzz.Compute(bars);
        var values = dzz.Buffers["Value"];

        Assert.Equal(0L, values[0]);    // Old pivot zeroed
        Assert.Equal(1200L, values[1]); // New pivot at bar 1

        // Add another bar that continues the trend
        bars.Add(TestBars.Create(1150, 1350, 1100, 1300)); // High=1350 > 1200
        dzz.Compute(bars);

        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);    // Old pivot at bar 1 now zeroed
        Assert.Equal(1350L, values[2]); // New pivot at bar 2
    }

    [Fact]
    public void Revise_FiresOnRevisedHook()
    {
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 10.0);
        var revisions = new List<(string Name, int Index, long Value)>();
        dzz.Buffers["Value"].OnRevised = (name, index, value) =>
            revisions.Add((name, index, value));

        // Bar 0: High=1100, pivot at 0
        // Bar 1: High=1200 > 1100, pivot relocates → Revise(0, 0L) fires
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
        };

        dzz.Compute(bars);

        Assert.Single(revisions);
        Assert.Equal("Value", revisions[0].Name);
        Assert.Equal(0, revisions[0].Index);
        Assert.Equal(0L, revisions[0].Value);
    }

    [Fact]
    public void Set_DoesNotFireHook()
    {
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 10.0);
        var revisions = new List<(string Name, int Index, long Value)>();
        dzz.Buffers["Value"].OnRevised = (name, index, value) =>
            revisions.Add((name, index, value));

        // Single bar: only Set is called for the new pivot, no Revise
        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050) };
        dzz.Compute(bars);

        Assert.Empty(revisions);
    }

    [Fact]
    public void EmptySeries_NoBufferEntries()
    {
        var dzz = CreateIndicator();
        dzz.Compute(new List<Int64Bar>());

        Assert.Empty(dzz.Buffers["Value"]);
    }

    [Fact]
    public void SingleBar_PivotAtBarZero()
    {
        var dzz = CreateIndicator();
        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050) };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        Assert.Single(values);
        Assert.Equal(1100L, values[0]); // Initial direction is up, so high becomes pivot
    }

    [Fact]
    public void ExportChartId_ValueBuffer_ReturnsNull()
    {
        var dzz = CreateIndicator();

        Assert.Null(dzz.Buffers["Value"].ExportChartId);
    }

    [Fact]
    public void ThresholdScalesWithPrice()
    {
        // 5% of close → for close=1000, threshold=50; for close=10000, threshold=500
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 5.0);

        // Low-priced bars: close~1000, threshold=50. Drop of 60 triggers reversal.
        var bars = new List<Int64Bar>
        {
            TestBars.Create(950, 1100, 900, 1000),   // High=1100, close=1000
            TestBars.Create(1000, 1200, 950, 1100),   // High=1200, close=1100, pivot relocates
            // threshold = 5% of 1050 = 55 (approx). L=1100 < 1200-55=1145? 1100 < 1145 → reversal
            TestBars.Create(1100, 1150, 1100, 1050),
        };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        var pivots = values.Where(v => v != 0L).ToList();

        // Should get 2 pivots: the high (1200) and the low reversal (1100)
        Assert.Equal(2, pivots.Count);
        Assert.Equal(1200L, pivots[0]);
        Assert.Equal(1100L, pivots[1]);
    }

    [Fact]
    public void HighPriceNeedsLargerAbsoluteMove()
    {
        // 5% of close=10000 → threshold=500. A 300 drop should NOT cause reversal.
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 5.0);

        var bars = new List<Int64Bar>
        {
            TestBars.Create(9800, 10200, 9700, 10000),  // High=10200, close=10000
            TestBars.Create(10000, 10500, 9900, 10300),  // High=10500, close=10300, relocate
            // threshold = 5% of 10100 = 505. L=10200 < 10500-505=9995? 10200 > 9995 → NO reversal
            TestBars.Create(10200, 10300, 10200, 10100),
        };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        var pivots = values.Where(v => v != 0L).ToList();

        // Only one pivot (ongoing uptrend, no reversal)
        Assert.Single(pivots);
        Assert.Equal(10500L, pivots[0]);
    }

    [Fact]
    public void DynamicThresholdOverridesPercentageFloor()
    {
        // After first reversal, dynamic threshold (swingSize * delta) should still
        // be used when it exceeds the percentage floor.
        var dzz = CreateIndicator(delta: 0.5, minThresholdPct: 0.5);

        var bars = new List<Int64Bar>
        {
            TestBars.Create(950, 1100, 900, 1000),
            TestBars.Create(1000, 1500, 950, 1400),  // High=1500

            // threshold = 0.5% of 900 = 5. L=900 < 1500-5=1495 → reversal
            // swingSize = 1500 - 900 = 600
            TestBars.Create(1300, 1400, 900, 950),

            // Extend low to 800
            TestBars.Create(900, 950, 800, 850),

            // Next reversal needs: dynamic = 600*0.5 = 300, pct floor = 0.5% of 1100 ≈ 6
            // max(300, 6) = 300. Need H > 800 + 300 = 1100
            // H=1050 < 1100 → no reversal
            TestBars.Create(850, 1050, 830, 1100),
        };

        dzz.Compute(bars);

        var values = dzz.Buffers["Value"];
        var pivots = values.Where(v => v != 0L).ToList();

        // Only 2 pivots: 1500 (high), 800 (low). No second reversal because dynamic > pct floor.
        Assert.Equal(2, pivots.Count);
        Assert.Equal(1500L, pivots[0]);
        Assert.Equal(800L, pivots[1]);
    }
}

using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public class AtrZigZagTests
{
    private static AtrZigZag CreateIndicator(double multiplier = 2.0, int atrPeriod = 2)
        => new(multiplier, atrPeriod);

    /// <summary>
    /// Shared scenario with hand-computed Wilder ATR (period=2, multiplier=2):
    ///   TR[0]=20, TR[1]=20 → ATR seed at bar 1 = 20.
    ///   Bar 2: TR=40 → ATR=30. Bar 3: TR=30 → ATR=30.
    ///   Bootstrap breach at bar 3: up_move = 1060-990 = 70 ≥ 2*30=60 → direction up,
    ///   extremum anchored at argmax(high[0..3]) = bar 3 (1060).
    /// </summary>
    private static List<Int64Bar> BootstrapBars() =>
    [
        TestBars.Create(1000, 1010, 990, 1000),
        TestBars.Create(1000, 1010, 990, 1000),
        TestBars.Create(1000, 1040, 1000, 1030),
        TestBars.Create(1030, 1060, 1030, 1050),
    ];

    [Fact]
    public void Warmup_NoPivotsBeforeAtrValid()
    {
        // period=5 → ATR first valid at bar 4; only 4 bars supplied
        var zz = CreateIndicator(multiplier: 2.0, atrPeriod: 5);
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1100, 900, 1050),
            TestBars.Create(1050, 1200, 1000, 1150),
            TestBars.Create(1150, 1300, 1100, 1250),
            TestBars.Create(1250, 1400, 1200, 1350),
        };

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(4, values.Count);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(0L, values[i]));
    }

    [Fact]
    public void Bootstrap_DeclaresDirectionAndAnchorsAtArgmax()
    {
        var zz = CreateIndicator();

        zz.Compute(BootstrapBars());

        var values = zz.Buffers["Value"];
        Assert.Equal(4, values.Count);
        // No pivot until the bootstrap breach at bar 3; extremum anchored there.
        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);
        Assert.Equal(0L, values[2]);
        Assert.Equal(1060L, values[3]);
    }

    [Fact]
    public void Bootstrap_NoBreach_NoPivot()
    {
        var zz = CreateIndicator();
        // Same first 3 bars as BootstrapBars: at bar 2 up_move = 1040-990 = 50 < 2*30 = 60
        var bars = BootstrapBars().Take(3).ToList();

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(0L, values[i]));
    }

    [Fact]
    public void Bootstrap_DeclaresDownDirectionAndAnchorsAtArgmin()
    {
        var zz = CreateIndicator();
        // Mirror of the up scenario: TR[2]=40 → ATR=30, TR[3]=30 → ATR=30.
        // Bar 3: down_move = 1010-940 = 70 ≥ 2*30=60 → direction down,
        // extremum anchored at argmin(low[0..3]) = bar 3 (940).
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1010, 990, 1000),
            TestBars.Create(1000, 1010, 990, 1000),
            TestBars.Create(1000, 1000, 960, 970),
            TestBars.Create(970, 970, 940, 950),
        };

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);
        Assert.Equal(0L, values[2]);
        Assert.Equal(940L, values[3]);
    }

    [Fact]
    public void Bootstrap_AnchorsAtPastBar_PinsPastBarAtr()
    {
        var zz = CreateIndicator();
        // Bar 2 spikes to 1075 without breaching (up_move 85 < 2*ATR[2]=95, ATR[2]=47.5).
        // Bar 3: ATR collapses to 28.75 (TR=10) → threshold 57.5; up_move = 1070-990 = 80
        // breaches → direction up, anchored at argmax = PAST bar 2 with ATR[2]=47.5
        // pinned (threshold 95), not the breach bar's 28.75.
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1010, 990, 1000),
            TestBars.Create(1000, 1010, 990, 1000),
            TestBars.Create(1000, 1075, 1000, 1070),
            TestBars.Create(1070, 1070, 1060, 1065),
            // Bar 4: drift down, 1075-1040 = 35 → no reversal under any threshold
            TestBars.Create(1065, 1065, 1040, 1045),
            // Bar 5: 1075-1010 = 65 ≥ current 2*ATR[5]=61.875 but < pinned 95 → must NOT reverse
            TestBars.Create(1045, 1045, 1010, 1015),
            // Bar 6: 1075-975 = 100 ≥ 95 → reversal against the pinned threshold
            TestBars.Create(1015, 1015, 975, 980),
        };

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(1075L, values[2]); // anchored at the past spike bar
        Assert.Equal(0L, values[3]);    // not at the breach bar
        Assert.Equal(0L, values[5]);    // pinned ATR rejected the bar-5 drop
        Assert.Equal(975L, values[6]);
    }

    [Fact]
    public void Bootstrap_PastBarAnchor_ReportsRevision()
    {
        var zz = CreateIndicator();
        var revisions = new List<(int Index, long Value)>();
        zz.Buffers["Value"].OnRevised = (_, index, value) => revisions.Add((index, value));

        // First 4 bars of Bootstrap_AnchorsAtPastBar_PinsPastBarAtr: the bar-3 breach
        // anchors the pivot at PAST bar 2. Chart emitters only see retroactive writes
        // through OnRevised, so the anchor must be reported as a revision.
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1010, 990, 1000),
            TestBars.Create(1000, 1010, 990, 1000),
            TestBars.Create(1000, 1075, 1000, 1070),
            TestBars.Create(1070, 1070, 1060, 1065),
        };

        zz.Compute(bars);

        Assert.Equal(1075L, zz.Buffers["Value"][2]);
        Assert.Contains((2, 1075L), revisions);
    }

    [Fact]
    public void Bootstrap_CurrentBarAnchor_NoRevision()
    {
        var zz = CreateIndicator();
        var revisions = new List<(int Index, long Value)>();
        zz.Buffers["Value"].OnRevised = (_, index, value) => revisions.Add((index, value));

        // BootstrapBars anchors at the breach bar itself (bar 3): the pivot is the
        // buffer's latest value, so it must be written via Set, not reported as a revision.
        zz.Compute(BootstrapBars());

        Assert.Equal(1060L, zz.Buffers["Value"][3]);
        Assert.Empty(revisions);
    }

    [Fact]
    public void Bootstrap_AnchorInAtrWarmup_FallsBackToCurrentAtr()
    {
        // period=3 → ATR first valid at bar 2; the bar-1 spike (high 1100) lies in warmup.
        var zz = CreateIndicator(multiplier: 2.0, atrPeriod: 3);
        // ATR[2] = (20+100+18)/3 = 46. Bar 2: up_move = 1098-990 = 108 ≥ 92 → direction up,
        // anchored at argmax = bar 1 where ATR is NaN → ext ATR falls back to 46 (threshold 92).
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1010, 990, 1005),
            TestBars.Create(1005, 1100, 1000, 1090),
            TestBars.Create(1090, 1098, 1080, 1095),
            // Bar 3: 1100-1010 = 90 < 92 → near miss against the fallback threshold
            TestBars.Create(1095, 1096, 1010, 1015),
            // Bar 4: 1100-1008 = 92 ≥ 92 → confirms exactly at the fallback threshold.
            // Current ATR[4] would demand 2*59.33 = 118.67 — a NaN ext ATR would never confirm.
            TestBars.Create(1015, 1016, 1008, 1010),
        };

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(0L, values[0]);
        Assert.Equal(1100L, values[1]); // anchored inside the ATR warmup region
        Assert.Equal(0L, values[3]);
        Assert.Equal(1008L, values[4]);
    }

    [Fact]
    public void UpPhase_RelocationZeroesOldPivot()
    {
        var zz = CreateIndicator();
        var bars = BootstrapBars();
        // Bar 4: H=1070 > 1060 → pivot relocates from bar 3 to bar 4
        bars.Add(TestBars.Create(1050, 1070, 1040, 1060));

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(0L, values[3]);
        Assert.Equal(1070L, values[4]);
    }

    [Fact]
    public void UpPhase_ReversalUsesAtrAtExtremumBar()
    {
        var zz = CreateIndicator();
        var bars = BootstrapBars();
        // Bar 4 relocates pivot to 1070; ATR[4]: TR=max(30,20,10)=30 → ATR=0.5*30+0.5*30=30.
        // Pinned threshold = 2*30 = 60.
        bars.Add(TestBars.Create(1050, 1070, 1040, 1060));
        // Bar 5: drop 1070-1005 = 65 ≥ 60 → reversal confirmed, low pivot at bar 5.
        bars.Add(TestBars.Create(1060, 1065, 1005, 1010));

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(1070L, values[4]);
        Assert.Equal(1005L, values[5]);
    }

    [Fact]
    public void DownPhase_ThresholdPinnedAtExtremum_NotCurrentAtr()
    {
        var zz = CreateIndicator();
        var bars = BootstrapBars();
        bars.Add(TestBars.Create(1050, 1070, 1040, 1060)); // up pivot 1070, ATR=30
        bars.Add(TestBars.Create(1060, 1065, 1005, 1010)); // reversal: low ext 1005, ATR[5]=45 pinned (TR=60)
        // Bar 6: tiny range → current ATR collapses to 0.5*45+0.5*2 = 23.5
        bars.Add(TestBars.Create(1010, 1011, 1009, 1010));
        // Bar 7: H-ext = 1062-1005 = 57. Against current ATR (23.5*2=47) this would
        // confirm, but the pinned extremum ATR demands 2*45 = 90 → must NOT reverse.
        bars.Add(TestBars.Create(1010, 1062, 1010, 1060));

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        // Still in down phase: low pivot 1005 stands, no up pivot written
        Assert.Equal(1005L, values[5]);
        Assert.Equal(0L, values[6]);
        Assert.Equal(0L, values[7]);

        // Bar 8: H-ext = 1100-1005 = 95 ≥ 90 → now it confirms
        bars.Add(TestBars.Create(1060, 1100, 1055, 1090));
        zz.Compute(bars);

        Assert.Equal(1005L, values[5]);
        Assert.Equal(1100L, values[8]);
    }

    [Fact]
    public void DownPhase_HighFirst_ConfirmationBeatsExtension()
    {
        var zz = CreateIndicator();
        var bars = BootstrapBars();
        bars.Add(TestBars.Create(1050, 1070, 1040, 1060)); // up pivot 1070
        bars.Add(TestBars.Create(1060, 1065, 1005, 1010)); // down ext 1005, pinned ATR=45 → threshold 90
        // Bar 6 BOTH makes a lower low (1000 < 1005) AND breaks the up threshold
        // (1100-1005 = 95 ≥ 90). High-first convention: confirmation wins, the
        // 1000 low must never become a pivot.
        bars.Add(TestBars.Create(1050, 1100, 1000, 1080));

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(1005L, values[5]); // low pivot preserved at its original bar
        Assert.Equal(1100L, values[6]); // up reversal confirmed at bar 6 high
        var pivots = Enumerable.Range(0, values.Count).Select(i => values[i]).Where(v => v != 0L).ToList();
        Assert.DoesNotContain(1000L, pivots);
    }

    [Fact]
    public void DownPhase_ExtensionRelocatesLowPivot()
    {
        var zz = CreateIndicator();
        var bars = BootstrapBars();
        bars.Add(TestBars.Create(1050, 1070, 1040, 1060));
        bars.Add(TestBars.Create(1060, 1065, 1005, 1010)); // down ext 1005 at bar 5
        // Bar 6: lower low without breaking up threshold → pivot relocates to bar 6
        bars.Add(TestBars.Create(1010, 1015, 995, 1000));

        zz.Compute(bars);

        var values = zz.Buffers["Value"];
        Assert.Equal(0L, values[5]);
        Assert.Equal(995L, values[6]);
    }

    [Fact]
    public void IncrementalConsistency_SameResultAsBatch()
    {
        var bars = BootstrapBars();
        bars.Add(TestBars.Create(1050, 1070, 1040, 1060));
        bars.Add(TestBars.Create(1060, 1065, 1005, 1010));
        bars.Add(TestBars.Create(1010, 1011, 1009, 1010));
        bars.Add(TestBars.Create(1010, 1062, 1010, 1060));
        bars.Add(TestBars.Create(1060, 1100, 1055, 1090));
        bars.Add(TestBars.Create(1090, 1120, 1080, 1110));
        bars.Add(TestBars.Create(1110, 1115, 1020, 1030));

        var batch = CreateIndicator();
        batch.Compute(bars);
        var batchValues = batch.Buffers["Value"].ToList();

        var incr = CreateIndicator();
        for (var n = 1; n <= bars.Count; n++)
            incr.Compute(bars.Take(n).ToList());
        var incrValues = incr.Buffers["Value"].ToList();

        Assert.Equal(batchValues.Count, incrValues.Count);
        for (var i = 0; i < batchValues.Count; i++)
            Assert.Equal(batchValues[i], incrValues[i]);
    }

    [Fact]
    public void EmptySeries_NoBufferEntries()
    {
        var zz = CreateIndicator();
        zz.Compute(new List<Int64Bar>());

        Assert.Empty(zz.Buffers["Value"]);
    }

    [Fact]
    public void InvalidParameters_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZag(0.0, 14));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZag(-1.0, 14));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZag(2.0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrZigZag(2.0, -5));
    }
}

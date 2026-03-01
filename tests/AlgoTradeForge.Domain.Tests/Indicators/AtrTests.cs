using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public sealed class AtrTests
{
    private static Atr CreateIndicator(int period = 3) => new(period);

    [Fact]
    public void EmptySeries_NoBufferEntries()
    {
        var atr = CreateIndicator();
        atr.Compute(new List<Int64Bar>());

        Assert.Empty(atr.Buffers["Value"]);
    }

    [Fact]
    public void WarmupPeriod_ZeroUntilPrimed()
    {
        var atr = CreateIndicator(period: 3);
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 110, 90, 105),  // TR = 110 - 90 = 20
            TestBars.Create(105, 115, 95, 110),  // TR = max(20, |115-105|, |95-105|) = 20
        };

        atr.Compute(bars);
        var values = atr.Buffers["Value"];

        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);
    }

    [Fact]
    public void FirstAtr_IsSimpleAverageOfTrueRanges()
    {
        var atr = CreateIndicator(period: 3);
        // TR1 = H-L = 110-90 = 20 (no prev close)
        // TR2 = max(115-95, |115-105|, |95-105|) = max(20, 10, 10) = 20
        // TR3 = max(120-100, |120-110|, |100-110|) = max(20, 10, 10) = 20
        // ATR = (20 + 20 + 20) / 3 = 20
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 110, 90, 105),
            TestBars.Create(105, 115, 95, 110),
            TestBars.Create(110, 120, 100, 115),
        };

        atr.Compute(bars);
        var values = atr.Buffers["Value"];

        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);
        Assert.Equal(20L, values[2]);
    }

    [Fact]
    public void WilderSmoothing_AppliedAfterWarmup()
    {
        var atr = CreateIndicator(period: 3);
        // TR1=20, TR2=20, TR3=20 → ATR3 = 20
        // TR4 = max(130-110, |130-115|, |110-115|) = max(20, 15, 5) = 20
        // ATR4 = (20 * 2 + 20) / 3 = 20
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 110, 90, 105),
            TestBars.Create(105, 115, 95, 110),
            TestBars.Create(110, 120, 100, 115),
            TestBars.Create(115, 130, 110, 125),
        };

        atr.Compute(bars);
        var values = atr.Buffers["Value"];

        Assert.Equal(20L, values[3]);
    }

    [Fact]
    public void WilderSmoothing_VolatilitySpike()
    {
        var atr = CreateIndicator(period: 3);
        // TR1=20, TR2=20, TR3=20 → ATR3 = 20
        // Bar4: big gap up. Close3=115, Open4=200, High4=220, Low4=190
        // TR4 = max(220-190, |220-115|, |190-115|) = max(30, 105, 75) = 105
        // ATR4 = (20 * 2 + 105) / 3 = 145/3 = 48 (integer division)
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 110, 90, 105),
            TestBars.Create(105, 115, 95, 110),
            TestBars.Create(110, 120, 100, 115),
            TestBars.Create(200, 220, 190, 210),
        };

        atr.Compute(bars);
        var values = atr.Buffers["Value"];

        Assert.Equal(48L, values[3]); // (20*2 + 105) / 3 = 48
    }

    [Fact]
    public void TrueRange_GapUp_UsesHighMinusPrevClose()
    {
        var atr = CreateIndicator(period: 2);
        // Bar0: H=110, L=90, C=105. TR=20
        // Bar1: gap up. H=200, L=180, C=190. PrevC=105.
        // TR = max(200-180, |200-105|, |180-105|) = max(20, 95, 75) = 95
        // ATR = (20 + 95) / 2 = 57
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 110, 90, 105),
            TestBars.Create(185, 200, 180, 190),
        };

        atr.Compute(bars);

        Assert.Equal(57L, atr.Buffers["Value"][1]);
    }

    [Fact]
    public void TrueRange_GapDown_UsesLowMinusPrevClose()
    {
        var atr = CreateIndicator(period: 2);
        // Bar0: H=110, L=90, C=105. TR=20
        // Bar1: gap down. H=50, L=30, C=40. PrevC=105.
        // TR = max(50-30, |50-105|, |30-105|) = max(20, 55, 75) = 75
        // ATR = (20 + 75) / 2 = 47
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 110, 90, 105),
            TestBars.Create(45, 50, 30, 40),
        };

        atr.Compute(bars);

        Assert.Equal(47L, atr.Buffers["Value"][1]);
    }

    [Fact]
    public void IncrementalConsistency_SameResultAsBatch()
    {
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 110, 90, 105),
            TestBars.Create(105, 115, 95, 110),
            TestBars.Create(110, 120, 100, 115),
            TestBars.Create(115, 130, 110, 125),
            TestBars.Create(125, 140, 120, 135),
            TestBars.Create(130, 160, 125, 155),
        };

        var batchAtr = CreateIndicator(period: 3);
        batchAtr.Compute(bars);
        var batchValues = batchAtr.Buffers["Value"].ToList();

        var incrAtr = CreateIndicator(period: 3);
        incrAtr.Compute(bars.Take(3).ToList());
        incrAtr.Compute(bars);
        var incrValues = incrAtr.Buffers["Value"].ToList();

        Assert.Equal(batchValues.Count, incrValues.Count);
        for (var i = 0; i < batchValues.Count; i++)
            Assert.Equal(batchValues[i], incrValues[i]);
    }

    [Fact]
    public void SingleBar_TrueRangeIsHighMinusLow()
    {
        var atr = CreateIndicator(period: 1);
        var bars = new List<Int64Bar>
        {
            TestBars.Create(100, 150, 80, 120), // TR = 150 - 80 = 70
        };

        atr.Compute(bars);

        Assert.Equal(70L, atr.Buffers["Value"][0]);
    }

    [Fact]
    public void MinimumHistory_EqualsPeriod()
    {
        var atr = CreateIndicator(period: 14);

        Assert.Equal(14, atr.MinimumHistory);
    }

    [Fact]
    public void Name_ReturnsClassName()
    {
        var atr = CreateIndicator();

        Assert.Equal("Atr", atr.Name);
    }

    [Fact]
    public void Measure_IsPrice()
    {
        var atr = CreateIndicator();

        Assert.Equal(IndicatorMeasure.Price, atr.Measure);
    }
}

using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public sealed class SmaTests
{
    private static List<Int64Bar> BarsFromCloses(params long[] closes) =>
        closes.Select((c, i) => new Int64Bar(i * 60000L, c, c + 10, c - 10, c, 1000L)).ToList();

    [Fact]
    public void Sma3_CorrectValues_AfterWarmup()
    {
        var sma = new Sma(3);
        var bars = BarsFromCloses(100, 200, 300, 400, 500);

        sma.Compute(bars);
        var values = sma.Buffers["Value"];

        Assert.Equal(5, values.Count);
        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);
        Assert.Equal(200L, values[2]); // (100 + 200 + 300) / 3
        Assert.Equal(300L, values[3]); // (200 + 300 + 400) / 3
        Assert.Equal(400L, values[4]); // (300 + 400 + 500) / 3
    }

    [Fact]
    public void Sma1_EqualsClosePrice()
    {
        var sma = new Sma(1);
        var bars = BarsFromCloses(50, 150, 250);

        sma.Compute(bars);
        var values = sma.Buffers["Value"];

        Assert.Equal(3, values.Count);
        Assert.Equal(50L, values[0]);
        Assert.Equal(150L, values[1]);
        Assert.Equal(250L, values[2]);
    }

    [Fact]
    public void Sma5_Warmup_FirstFourValuesAreZero()
    {
        var sma = new Sma(5);
        var bars = BarsFromCloses(10, 20, 30, 40, 50);

        sma.Compute(bars);
        var values = sma.Buffers["Value"];

        Assert.Equal(5, values.Count);
        Assert.Equal(0L, values[0]);
        Assert.Equal(0L, values[1]);
        Assert.Equal(0L, values[2]);
        Assert.Equal(0L, values[3]);
        Assert.Equal(30L, values[4]); // (10 + 20 + 30 + 40 + 50) / 5
    }

    [Fact]
    public void IncrementalCompute_SameResultAsBatch()
    {
        var bars = BarsFromCloses(100, 200, 300, 400, 500);

        var batchSma = new Sma(3);
        batchSma.Compute(bars);
        var batchValues = batchSma.Buffers["Value"].ToList();

        var incrSma = new Sma(3);
        incrSma.Compute(bars.Take(3).ToList());
        incrSma.Compute(bars);
        var incrValues = incrSma.Buffers["Value"].ToList();

        Assert.Equal(batchValues.Count, incrValues.Count);
        for (var i = 0; i < batchValues.Count; i++)
            Assert.Equal(batchValues[i], incrValues[i]);
    }

    [Fact]
    public void Sma3_IntegerDivision_Truncates()
    {
        // 101 + 202 + 304 = 607. 607/3 = 202 (truncates 202.33)
        var sma = new Sma(3);
        var bars = BarsFromCloses(101, 202, 304);

        sma.Compute(bars);
        var values = sma.Buffers["Value"];

        Assert.Equal(202L, values[2]); // integer division truncates
    }

    [Fact]
    public void MinimumHistory_EqualsPeriod()
    {
        var sma = new Sma(7);

        Assert.Equal(7, sma.MinimumHistory);
    }
}

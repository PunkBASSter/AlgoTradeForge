using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public sealed class AdxTests
{
    private static List<Int64Bar> CreateTrendingBars(int count, long startPrice = 10000, long increment = 100)
    {
        var bars = new List<Int64Bar>();
        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * increment;
            bars.Add(new Int64Bar(i * 60000L, price, price + 200, price - 50, price + 100, 1000L));
        }
        return bars;
    }

    private static List<Int64Bar> CreateChoppyBars(int count, long centerPrice = 10000)
    {
        var bars = new List<Int64Bar>();
        for (var i = 0; i < count; i++)
        {
            // Oscillate around center price
            var offset = (i % 2 == 0) ? 100L : -100L;
            var price = centerPrice + offset;
            bars.Add(new Int64Bar(i * 60000L, price, price + 150, price - 150, price, 1000L));
        }
        return bars;
    }

    [Fact]
    public void MinimumHistory_IsTwicePeriod()
    {
        var adx = new Adx(14);
        Assert.Equal(28, adx.MinimumHistory);
    }

    [Fact]
    public void Values_BoundedBetween0And100()
    {
        var adx = new Adx(14);
        var bars = CreateTrendingBars(60);

        adx.Compute(bars);
        var values = adx.Buffers["Value"];

        for (var i = 0; i < values.Count; i++)
        {
            Assert.InRange(values[i], 0.0, 100.0);
        }
    }

    [Fact]
    public void StrongTrend_HighAdxValue()
    {
        var adx = new Adx(14);
        var bars = CreateTrendingBars(60);

        adx.Compute(bars);
        var values = adx.Buffers["Value"];

        // Last value should indicate strong trend
        var lastAdx = values[^1];
        Assert.True(lastAdx > 20.0, $"ADX should be high for trending data, was {lastAdx}");
    }

    [Fact]
    public void ChoppyMarket_LowerAdxValue()
    {
        var adx = new Adx(14);
        var bars = CreateChoppyBars(60);

        adx.Compute(bars);
        var values = adx.Buffers["Value"];

        var lastAdx = values[^1];
        Assert.True(lastAdx < 40.0, $"ADX should be moderate/low for choppy data, was {lastAdx}");
    }

    [Fact]
    public void WarmupPeriod_FirstValuesAreZero()
    {
        var adx = new Adx(14);
        var bars = CreateTrendingBars(40);

        adx.Compute(bars);
        var values = adx.Buffers["Value"];

        // First few values should be zero during warmup
        Assert.Equal(0.0, values[0]);
    }

    [Fact]
    public void Measure_IsMinusOnePlusOne()
    {
        var adx = new Adx(14);
        Assert.Equal(IndicatorMeasure.MinusOnePlusOne, adx.Measure);
    }

    [Fact]
    public void IncrementalCompute_MatchesBatch()
    {
        var bars = CreateTrendingBars(40);

        var batch = new Adx(7);
        batch.Compute(bars);

        var incr = new Adx(7);
        incr.Compute(bars.Take(20).ToList());
        incr.Compute(bars);

        var batchValues = batch.Buffers["Value"];
        var incrValues = incr.Buffers["Value"];

        Assert.Equal(batchValues.Count, incrValues.Count);
        for (var i = 0; i < batchValues.Count; i++)
            Assert.Equal(batchValues[i], incrValues[i], precision: 10);
    }
}

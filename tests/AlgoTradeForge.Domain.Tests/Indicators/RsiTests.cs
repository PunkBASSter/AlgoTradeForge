using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public sealed class RsiTests
{
    private static List<Int64Bar> BarsFromCloses(params long[] closes) =>
        closes.Select((c, i) => new Int64Bar(i * 60000L, c, c + 10, c - 10, c, 1000L)).ToList();

    [Fact]
    public void Rsi2_KnownPriceSeries_ProducesExpectedValues()
    {
        // Closes: 100, 102, 101, 103, 105, 104, 106 (scaled as longs)
        var bars = BarsFromCloses(10000, 10200, 10100, 10300, 10500, 10400, 10600);
        var rsi = new Rsi(period: 2);

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        Assert.Equal(7, values.Count);

        // Bar 0: placeholder (first bar, no change computed)
        Assert.Equal(50.0, values[0]);

        // Bar 1: warmup not complete (warmupCount=1 < period=2)
        Assert.Equal(50.0, values[1]);

        // Bar 2: first real RSI (warmupCount=2 == period)
        // change=-100 → gain=0, loss=100. avgGain=200/2=100, avgLoss=100/2=50. RS=2. RSI=66.667
        Assert.Equal(66.667, values[2], precision: 2);

        // Bars 3-6: verify all are in valid range and not placeholder
        for (var i = 2; i < values.Count; i++)
        {
            Assert.InRange(values[i], 0.0, 100.0);
            Assert.NotEqual(50.0, values[i]);
        }
    }

    [Fact]
    public void AllValuesAfterWarmup_BoundedBetweenZeroAndHundred()
    {
        // Mix of ups and downs to exercise both gain and loss paths
        var bars = BarsFromCloses(
            10000, 10500, 10300, 10800, 10100, 10600, 10200,
            10900, 10400, 10700, 10000, 10800, 10100, 10500);
        var rsi = new Rsi(period: 3);

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        Assert.Equal(14, values.Count);

        // All values including warmup placeholders must be 0-100
        for (var i = 0; i < values.Count; i++)
            Assert.InRange(values[i], 0.0, 100.0);
    }

    [Fact]
    public void FallingPrices_RsiBelow50()
    {
        // Strictly declining close prices
        var bars = BarsFromCloses(10000, 9800, 9600, 9400, 9200, 9000, 8800);
        var rsi = new Rsi(period: 2);

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        // After warmup (index >= 2), RSI should be < 50 for consecutive declines
        for (var i = 2; i < values.Count; i++)
            Assert.True(values[i] < 50.0, $"RSI at index {i} was {values[i]}, expected < 50");
    }

    [Fact]
    public void RisingPrices_RsiAbove50()
    {
        // Strictly rising close prices
        var bars = BarsFromCloses(10000, 10200, 10400, 10600, 10800, 11000, 11200);
        var rsi = new Rsi(period: 2);

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        // After warmup (index >= 2), RSI should be > 50 for consecutive rises
        for (var i = 2; i < values.Count; i++)
            Assert.True(values[i] > 50.0, $"RSI at index {i} was {values[i]}, expected > 50");
    }

    [Fact]
    public void Rsi14_WarmupBars_ReturnPlaceholder_FirstRealValueAtBar15()
    {
        // Need at least period+1 = 15 bars. Create 16 bars with rising prices.
        var closes = Enumerable.Range(0, 16).Select(i => 10000L + i * 100).ToArray();
        var bars = BarsFromCloses(closes);
        var rsi = new Rsi(period: 14);

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        Assert.Equal(16, values.Count);

        // Bars 0-13: warmup placeholder = 50.0
        for (var i = 0; i < 14; i++)
            Assert.Equal(50.0, values[i]);

        // Bar 14 (index 14, the 15th bar): first real RSI value, should not be placeholder
        Assert.NotEqual(50.0, values[14]);
        Assert.InRange(values[14], 0.0, 100.0);
    }

    [Fact]
    public void AllRisingPrices_RsiEquals100()
    {
        // All gains, zero losses → avgLoss = 0 → RS = MaxValue → RSI = 100
        var bars = BarsFromCloses(10000, 10100, 10200, 10300, 10400);
        var rsi = new Rsi(period: 2);

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        // After warmup (index 2+), every bar has only gains, avgLoss stays 0
        for (var i = 2; i < values.Count; i++)
            Assert.Equal(100.0, values[i]);
    }

    [Fact]
    public void ConstantPrices_RsiEquals100()
    {
        // No change → avgGain = avgLoss = 0 → avgLoss == 0 check → RS = MaxValue → RSI ≈ 100
        var bars = BarsFromCloses(10000, 10000, 10000, 10000, 10000);
        var rsi = new Rsi(period: 2);

        rsi.Compute(bars);
        var values = rsi.Buffers["Value"];

        // With zero movement, the avgLoss == 0 guard fires, producing RSI = 100
        for (var i = 2; i < values.Count; i++)
            Assert.Equal(100.0, values[i], precision: 5);
    }

    [Fact]
    public void IncrementalCompute_MatchesBatchCompute()
    {
        var bars = BarsFromCloses(
            10000, 10200, 10100, 10300, 10500, 10400,
            10600, 10350, 10550, 10700, 10450, 10650);

        // Batch: compute all at once
        var batchRsi = new Rsi(period: 3);
        batchRsi.Compute(bars);
        var batchValues = batchRsi.Buffers["Value"].ToList();

        // Incremental: compute first half, then full series
        var incrRsi = new Rsi(period: 3);
        incrRsi.Compute(bars.Take(6).ToList());
        incrRsi.Compute(bars);
        var incrValues = incrRsi.Buffers["Value"].ToList();

        Assert.Equal(batchValues.Count, incrValues.Count);
        for (var i = 0; i < batchValues.Count; i++)
            Assert.Equal(batchValues[i], incrValues[i], precision: 10);
    }
}

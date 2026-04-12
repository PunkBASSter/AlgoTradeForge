using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Indicators;

public sealed class DonchianChannelTests
{
    private static List<Int64Bar> CreateBars(params (long high, long low)[] hl) =>
        hl.Select((x, i) =>
        {
            var mid = (x.high + x.low) / 2;
            return new Int64Bar(i * 60000L, mid, x.high, x.low, mid, 1000L);
        }).ToList();

    [Fact]
    public void Period3_UpperIsMaxHigh()
    {
        var dc = new DonchianChannel(3);
        var bars = CreateBars(
            (110, 90),   // bar 0
            (120, 95),   // bar 1
            (115, 85),   // bar 2  → upper = max(110,120,115) = 120
            (130, 100),  // bar 3  → upper = max(120,115,130) = 130
            (105, 80));  // bar 4  → upper = max(115,130,105) = 130

        dc.Compute(bars);
        var upper = dc.Buffers["Upper"];

        Assert.Equal(5, upper.Count);
        Assert.Equal(120L, upper[2]);
        Assert.Equal(130L, upper[3]);
        Assert.Equal(130L, upper[4]);
    }

    [Fact]
    public void Period3_LowerIsMinLow()
    {
        var dc = new DonchianChannel(3);
        var bars = CreateBars(
            (110, 90),   // bar 0
            (120, 95),   // bar 1
            (115, 85),   // bar 2  → lower = min(90,95,85) = 85
            (130, 100),  // bar 3  → lower = min(95,85,100) = 85
            (105, 80));  // bar 4  → lower = min(85,100,80) = 80

        dc.Compute(bars);
        var lower = dc.Buffers["Lower"];

        Assert.Equal(5, lower.Count);
        Assert.Equal(85L, lower[2]);
        Assert.Equal(85L, lower[3]);
        Assert.Equal(80L, lower[4]);
    }

    [Fact]
    public void Period3_MiddleIsMidpoint()
    {
        var dc = new DonchianChannel(3);
        var bars = CreateBars(
            (110, 90),   // bar 0
            (120, 95),   // bar 1
            (115, 85));  // bar 2  → upper=120, lower=85 → mid=(120+85)/2=102

        dc.Compute(bars);
        var middle = dc.Buffers["Middle"];

        Assert.Equal(102L, middle[2]); // (120 + 85) / 2 = 102
    }

    [Fact]
    public void WarmupPeriod_FirstBarsAreZero()
    {
        var dc = new DonchianChannel(3);
        var bars = CreateBars(
            (110, 90),
            (120, 95),
            (115, 85));

        dc.Compute(bars);
        var upper = dc.Buffers["Upper"];

        Assert.Equal(0L, upper[0]);
        Assert.Equal(0L, upper[1]);
        Assert.True(upper[2] > 0, "After warmup, upper should be non-zero");
    }

    [Fact]
    public void MinimumHistory_EqualsPeriod()
    {
        var dc = new DonchianChannel(20);
        Assert.Equal(20, dc.MinimumHistory);
    }

    [Fact]
    public void IncrementalCompute_MatchesBatch()
    {
        var bars = CreateBars(
            (110, 90), (120, 95), (115, 85), (130, 100), (105, 80));

        var batch = new DonchianChannel(3);
        batch.Compute(bars);

        var incr = new DonchianChannel(3);
        incr.Compute(bars.Take(3).ToList());
        incr.Compute(bars);

        for (var i = 0; i < bars.Count; i++)
        {
            Assert.Equal(batch.Buffers["Upper"][i], incr.Buffers["Upper"][i]);
            Assert.Equal(batch.Buffers["Lower"][i], incr.Buffers["Lower"][i]);
        }
    }

    [Fact]
    public void Period1_EachBarIsItsOwnChannel()
    {
        var dc = new DonchianChannel(1);
        var bars = CreateBars((100, 80), (120, 90));

        dc.Compute(bars);

        Assert.Equal(100L, dc.Buffers["Upper"][0]);
        Assert.Equal(80L, dc.Buffers["Lower"][0]);
        Assert.Equal(120L, dc.Buffers["Upper"][1]);
        Assert.Equal(90L, dc.Buffers["Lower"][1]);
    }
}

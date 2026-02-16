using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.History;

public class TimeSeriesSliceTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static TimeSeries<Int64Bar> MakeSeries(int count)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = Start.ToUnixTimeMilliseconds();
        var stepMs = (long)OneMinute.TotalMilliseconds;
        for (var i = 0; i < count; i++)
            series.Add(new Int64Bar(startMs + i * stepMs, 100 + i, 200 + i, 50 + i, 150 + i, 1000 + i));
        return series;
    }

    [Fact]
    public void Slice_WithinRange_ReturnsCorrectSubSeries()
    {
        var series = MakeSeries(10); // bars at minute 0..9

        // Slice [minute 2, minute 5) => bars at index 2, 3, 4
        var fromMs = (Start + TimeSpan.FromMinutes(2)).ToUnixTimeMilliseconds();
        var toMs = (Start + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds();
        var sliced = series.Slice(fromMs, toMs);

        Assert.Equal(3, sliced.Count);
        Assert.Equal(102, sliced[0].Open);
        Assert.Equal(103, sliced[1].Open);
        Assert.Equal(104, sliced[2].Open);
    }

    [Fact]
    public void Slice_EmptyRange_ReturnsEmptySeries()
    {
        var series = MakeSeries(10);
        var sameMs = (Start + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds();
        var sliced = series.Slice(sameMs, sameMs);

        Assert.Empty(sliced);
    }

    [Fact]
    public void Slice_FromAfterTo_ReturnsEmptySeries()
    {
        var series = MakeSeries(10);
        var fromMs = (Start + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds();
        var toMs = (Start + TimeSpan.FromMinutes(2)).ToUnixTimeMilliseconds();
        var sliced = series.Slice(fromMs, toMs);

        Assert.Empty(sliced);
    }

    [Fact]
    public void Slice_EmptySeries_ReturnsEmpty()
    {
        var series = MakeSeries(0);
        var fromMs = Start.ToUnixTimeMilliseconds();
        var toMs = (Start + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds();
        var sliced = series.Slice(fromMs, toMs);

        Assert.Empty(sliced);
    }

    [Fact]
    public void Slice_EntireRange_ReturnsAllBars()
    {
        var series = MakeSeries(5);
        var fromMs = Start.ToUnixTimeMilliseconds();
        var toMs = (Start + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds();
        var sliced = series.Slice(fromMs, toMs);

        Assert.Equal(5, sliced.Count);
    }

    [Fact]
    public void Slice_BeyondEnd_ClampsToCount()
    {
        var series = MakeSeries(5);
        var fromMs = Start.ToUnixTimeMilliseconds();
        var toMs = (Start + TimeSpan.FromMinutes(100)).ToUnixTimeMilliseconds();
        var sliced = series.Slice(fromMs, toMs);

        Assert.Equal(5, sliced.Count);
    }

    [Fact]
    public void Slice_BeforeStart_ClampsToZero()
    {
        var series = MakeSeries(5);
        var fromMs = (Start - TimeSpan.FromMinutes(10)).ToUnixTimeMilliseconds();
        var toMs = (Start + TimeSpan.FromMinutes(3)).ToUnixTimeMilliseconds();
        var sliced = series.Slice(fromMs, toMs);

        Assert.Equal(3, sliced.Count);
        Assert.Equal(100, sliced[0].Open);
    }
}

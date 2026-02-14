using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.History;

public class TimeSeriesResampleTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static TimeSeries<Int64Bar> MakeMinuteBars(int count,
        long openBase = 100, long highBase = 200, long lowBase = 50, long closeBase = 150, long volumeBase = 1000)
    {
        var series = new TimeSeries<Int64Bar>(Start, OneMinute);
        for (var i = 0; i < count; i++)
            series.Add(new Int64Bar(openBase + i, highBase + i, lowBase + i, closeBase + i, volumeBase));
        return series;
    }

    [Fact]
    public void Resample_1mTo5m_AggregatesCorrectly()
    {
        // 5 one-minute bars → 1 five-minute bar
        var series = new TimeSeries<Int64Bar>(Start, OneMinute);
        series.Add(new Int64Bar(100, 110, 90, 105, 1000));
        series.Add(new Int64Bar(105, 115, 95, 108, 2000));
        series.Add(new Int64Bar(108, 120, 85, 112, 1500));
        series.Add(new Int64Bar(112, 118, 92, 110, 1800));
        series.Add(new Int64Bar(110, 125, 88, 115, 2200));

        var resampled = series.Resample(TimeSpan.FromMinutes(5));

        Assert.Single(resampled);
        var bar = resampled[0];
        Assert.Equal(100, bar.Open);    // first bar's open
        Assert.Equal(125, bar.High);    // max high
        Assert.Equal(85, bar.Low);      // min low
        Assert.Equal(115, bar.Close);   // last bar's close
        Assert.Equal(8500, bar.Volume); // sum of volumes
    }

    [Fact]
    public void Resample_1mTo1h_AggregatesCorrectly()
    {
        var series = MakeMinuteBars(60);

        var resampled = series.Resample(TimeSpan.FromHours(1));

        Assert.Single(resampled);
        var bar = resampled[0];
        Assert.Equal(100, bar.Open);    // first bar open
        Assert.Equal(259, bar.High);    // highBase + 59
        Assert.Equal(50, bar.Low);      // lowBase + 0
        Assert.Equal(209, bar.Close);   // closeBase + 59
        Assert.Equal(60_000, bar.Volume);
    }

    [Fact]
    public void Resample_PartialFinalGroup_Handled()
    {
        // 7 bars resampled to 5-min → group of 5 + group of 2
        var series = MakeMinuteBars(7);

        var resampled = series.Resample(TimeSpan.FromMinutes(5));

        Assert.Equal(2, resampled.Count);

        // First group: bars 0-4
        Assert.Equal(100, resampled[0].Open);
        Assert.Equal(204, resampled[0].High);
        Assert.Equal(50, resampled[0].Low);
        Assert.Equal(154, resampled[0].Close);
        Assert.Equal(5000, resampled[0].Volume);

        // Second group: bars 5-6 (partial)
        Assert.Equal(105, resampled[1].Open);
        Assert.Equal(206, resampled[1].High);
        Assert.Equal(55, resampled[1].Low);
        Assert.Equal(156, resampled[1].Close);
        Assert.Equal(2000, resampled[1].Volume);
    }

    [Fact]
    public void Resample_SameInterval_Throws()
    {
        var series = MakeMinuteBars(5);

        Assert.Throws<ArgumentException>(() => series.Resample(OneMinute));
    }

    [Fact]
    public void Resample_SmallerTarget_Throws()
    {
        var series = MakeMinuteBars(5);

        Assert.Throws<ArgumentException>(() => series.Resample(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resample_NonMultiple_Throws()
    {
        // 3-minute source bars → 7-minute target is not an exact multiple (7 % 3 != 0)
        var series = new TimeSeries<Int64Bar>(Start, TimeSpan.FromMinutes(3));
        for (var i = 0; i < 5; i++)
            series.Add(new Int64Bar(100 + i, 200 + i, 50 + i, 150 + i, 1000));

        Assert.Throws<ArgumentException>(() => series.Resample(TimeSpan.FromMinutes(7)));
    }

    [Fact]
    public void Resample_EmptySeries_ReturnsEmpty()
    {
        var series = MakeMinuteBars(0);

        var resampled = series.Resample(TimeSpan.FromMinutes(5));

        Assert.Empty(resampled);
        Assert.Equal(TimeSpan.FromMinutes(5), resampled.Step);
    }
}

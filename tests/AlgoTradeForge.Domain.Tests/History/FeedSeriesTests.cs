using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.History;

public class FeedSeriesTests
{
    [Fact]
    public void Constructor_ValidData_SetsProperties()
    {
        var timestamps = new long[] { 100, 200, 300 };
        var columns = new[]
        {
            new double[] { 0.0001, 0.0002, -0.0001 }, // fundingRate
            new double[] { 50000, 50100, 49900 },       // markPrice
        };

        var series = new FeedSeries(timestamps, columns);

        Assert.Equal(3, series.Count);
        Assert.Equal(2, series.ColumnCount);
    }

    [Fact]
    public void Constructor_MismatchedLengths_Throws()
    {
        var timestamps = new long[] { 100, 200 };
        var columns = new[] { new double[] { 1.0, 2.0, 3.0 } }; // 3 values, 2 timestamps

        Assert.Throws<ArgumentException>(() => new FeedSeries(timestamps, columns));
    }

    [Fact]
    public void Constructor_MismatchedLengthAtSecondColumn_Throws()
    {
        var timestamps = new long[] { 100, 200, 300 };
        var columns = new[]
        {
            new double[] { 1.0, 2.0, 3.0 },  // correct length
            new double[] { 4.0, 5.0 },         // wrong length
        };

        var ex = Assert.Throws<ArgumentException>(() => new FeedSeries(timestamps, columns));
        Assert.Contains("Column 1", ex.Message);
    }

    [Fact]
    public void Constructor_EmptyColumns_AllowedWithEmptyTimestamps()
    {
        var series = new FeedSeries([], []);

        Assert.Equal(0, series.Count);
        Assert.Equal(0, series.ColumnCount);
    }

    [Fact]
    public void GetTimestamp_ReturnsCorrectValue()
    {
        var series = new FeedSeries([100L, 200L, 300L], [new double[] { 1, 2, 3 }]);

        Assert.Equal(100L, series.GetTimestamp(0));
        Assert.Equal(200L, series.GetTimestamp(1));
        Assert.Equal(300L, series.GetTimestamp(2));
    }

    [Fact]
    public void GetRow_FillsBufferWithAllColumnValues()
    {
        var columns = new[]
        {
            new double[] { 0.0001, 0.0002 },
            new double[] { 50000, 50100 },
            new double[] { 1.5, 1.8 },
        };
        var series = new FeedSeries([100L, 200L], columns);
        var buffer = new double[3];

        series.GetRow(0, buffer);
        Assert.Equal(0.0001, buffer[0]);
        Assert.Equal(50000, buffer[1]);
        Assert.Equal(1.5, buffer[2]);

        series.GetRow(1, buffer);
        Assert.Equal(0.0002, buffer[0]);
        Assert.Equal(50100, buffer[1]);
        Assert.Equal(1.8, buffer[2]);
    }
}

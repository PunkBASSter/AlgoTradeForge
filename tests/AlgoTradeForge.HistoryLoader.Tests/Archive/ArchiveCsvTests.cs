using AlgoTradeForge.HistoryLoader.Application.Archive;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class ArchiveCsvTests
{
    [Fact]
    public void ReadRows_SkipsHeaderRow_WhenPresent()
    {
        using var reader = new StringReader("open_time,open,high\n1000,1.5,2.5\n2000,2.5,3.5\n");
        var rows = ArchiveCsv.ReadRows(reader).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("1000", rows[0][0]);
    }

    [Fact]
    public void ReadRows_KeepsFirstRow_WhenNoHeader()
    {
        using var reader = new StringReader("1000,1.5,2.5\n2000,2.5,3.5\n");
        var rows = ArchiveCsv.ReadRows(reader).ToList();
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ReadRows_SkipsEmptyLines()
    {
        using var reader = new StringReader("1000,1.5\n\n2000,2.5\n");
        Assert.Equal(2, ArchiveCsv.ReadRows(reader).Count());
    }

    [Fact]
    public void NormalizeTimestampMs_PassesThroughMilliseconds()
    {
        Assert.Equal(1_751_846_400_000, ArchiveCsv.NormalizeTimestampMs(1_751_846_400_000));
    }

    [Fact]
    public void NormalizeTimestampMs_ConvertsMicroseconds()
    {
        // Spot archive switched to microseconds on 2025-01-01.
        Assert.Equal(1_751_846_400_000, ArchiveCsv.NormalizeTimestampMs(1_751_846_400_000_000));
    }
}

using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// SourceTailProbe is a hot-path helper: the aggregate endpoint calls it on every Continue
/// request to decide whether to enqueue a job or short-circuit to no_new_data. Tests cover
/// the structural cases (no dir, empty dir, single row, multi-partition) plus the
/// unhappy-but-recoverable cases (missing trailing newline, empty file).
/// </summary>
public sealed class SourceTailProbeTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"SourceTailProbeTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private DataFeedDescriptor TimeBarSource(string feedId) =>
        new(_tempDir, "binance", "BTCUSDT", feedId, DataFeedKind.TimeBar);

    [Fact]
    public void GetLastTs_MissingDir_ReturnsNull()
    {
        Assert.Null(SourceTailProbe.GetLastTs(TimeBarSource("1m")));
    }

    [Fact]
    public void GetLastTs_EmptyDir_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "binance", "BTCUSDT", "candles"));
        Assert.Null(SourceTailProbe.GetLastTs(TimeBarSource("1m")));
    }

    [Fact]
    public void GetLastTs_SingleFile_ReturnsLastRowTs()
    {
        var dir = Path.Combine(_tempDir, "binance", "BTCUSDT", "candles");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "2024-01_1m.csv");
        File.WriteAllLines(path,
        [
            "ts,o,h,l,c,vol",
            "1000,100,110,95,105,400",
            "2000,105,115,100,110,400",
            "3000,110,120,105,118,400",
        ]);

        Assert.Equal(3000L, SourceTailProbe.GetLastTs(TimeBarSource("1m")));
    }

    [Fact]
    public void GetLastTs_MultipleFiles_ReturnsLastTsFromLexLastFile()
    {
        var dir = Path.Combine(_tempDir, "binance", "BTCUSDT", "candles");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "2024-01_1m.csv"),
        [
            "ts,o,h,l,c,vol",
            "1000,100,110,95,105,400",
        ]);
        File.WriteAllLines(Path.Combine(dir, "2024-02_1m.csv"),
        [
            "ts,o,h,l,c,vol",
            "5000,200,210,195,205,800",
        ]);

        Assert.Equal(5000L, SourceTailProbe.GetLastTs(TimeBarSource("1m")));
    }

    [Fact]
    public void GetLastTs_FeedIdSuffixFilters_DoesNotCrossFeeds()
    {
        // "1m" and "5m" feeds in the same candles dir — the probe must read only "1m".
        var dir = Path.Combine(_tempDir, "binance", "BTCUSDT", "candles");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "2024-01_1m.csv"),
        [
            "ts,o,h,l,c,vol",
            "1000,100,110,95,105,400",
        ]);
        File.WriteAllLines(Path.Combine(dir, "2024-02_5m.csv"),
        [
            "ts,o,h,l,c,vol",
            "9999,200,210,195,205,800",
        ]);

        Assert.Equal(1000L, SourceTailProbe.GetLastTs(TimeBarSource("1m")));
    }

    [Fact]
    public void GetLastTs_HeaderOnlyFile_ReturnsNull()
    {
        // No data rows — the probe walks back, hits the header line, rejects it (alphabetic).
        var dir = Path.Combine(_tempDir, "binance", "BTCUSDT", "candles");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "2024-01_1m.csv"), "ts,o,h,l,c,vol\n");
        Assert.Null(SourceTailProbe.GetLastTs(TimeBarSource("1m")));
    }

    [Fact]
    public void GetLastTs_FileWithoutTrailingNewline_StillParsesLastRow()
    {
        var dir = Path.Combine(_tempDir, "binance", "BTCUSDT", "candles");
        Directory.CreateDirectory(dir);
        // No trailing \n.
        File.WriteAllText(
            Path.Combine(dir, "2024-01_1m.csv"),
            "ts,o,h,l,c,vol\n1000,100,110,95,105,400\n2000,105,115,100,110,400");

        Assert.Equal(2000L, SourceTailProbe.GetLastTs(TimeBarSource("1m")));
    }
}

using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>P1b-10 — chronological streaming source reader with per-FeedId glob.</summary>
public sealed class PartitionedSourceReaderTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"PartitionedSourceReaderTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CandlesDir(string asset) =>
        Path.Combine(_tempDir, "binance", asset, "candles");

    private void WriteCandles(string asset, string month, string interval, params (long ts, long o, long h, long l, long c, long v)[] rows)
    {
        var dir = CandlesDir(asset);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{month}_{interval}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,o,h,l,c,vol");
        foreach (var r in rows)
            sw.WriteLine($"{r.ts},{r.o},{r.h},{r.l},{r.c},{r.v}");
    }

    private static long Ts(int year, int month, int day, int hour = 0) =>
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private DataFeedDescriptor Source(string asset, string interval) =>
        new(_tempDir, "binance", asset, interval, DataFeedKind.TimeBar);

    [Fact]
    public void Read_SingleMonth_YieldsAllRecordsInOrder()
    {
        WriteCandles("BTCUSDT", "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 50),
            (Ts(2024, 1, 1, 1), 105, 115, 100, 110, 60),
            (Ts(2024, 1, 1, 2), 110, 120, 108, 118, 70));

        var reader = new PartitionedSourceReader();
        var records = reader.Read(Source("BTCUSDT", "1m")).ToList();

        Assert.Equal(3, records.Count);
        Assert.Equal(50, records[0].Volume);
        Assert.Equal(70, records[2].Volume);
    }

    [Fact]
    public void Read_AcrossMonths_IsChronological()
    {
        WriteCandles("BTCUSDT", "2024-02", "1m",
            (Ts(2024, 2, 1, 0), 110, 120, 105, 115, 80));
        WriteCandles("BTCUSDT", "2024-01", "1m",
            (Ts(2024, 1, 31, 23), 105, 115, 100, 110, 60));

        var reader = new PartitionedSourceReader();
        var records = reader.Read(Source("BTCUSDT", "1m")).ToList();

        Assert.Equal(2, records.Count);
        Assert.True(records[0].TsMs < records[1].TsMs);
    }

    [Fact]
    public void Read_OtherIntervalsInSameDir_NotPickedUp()
    {
        // P1a-30 regression: loading "1m" must not yield rows from "5m"-named files.
        WriteCandles("BTCUSDT", "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 50));
        WriteCandles("BTCUSDT", "2024-01", "5m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 250));   // 5m volume should not leak in

        var reader = new PartitionedSourceReader();
        var records = reader.Read(Source("BTCUSDT", "1m")).ToList();

        Assert.Single(records);
        Assert.Equal(50, records[0].Volume);
    }

    [Fact]
    public void Read_DateRangeFilter_ExcludesOutOfRange()
    {
        WriteCandles("BTCUSDT", "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 50),
            (Ts(2024, 1, 5, 0), 105, 115, 100, 110, 60),
            (Ts(2024, 1, 10, 0), 110, 120, 108, 118, 70));

        var reader = new PartitionedSourceReader();
        var records = reader.Read(
                Source("BTCUSDT", "1m"),
                from: new DateOnly(2024, 1, 3),
                to: new DateOnly(2024, 1, 7))
            .ToList();

        Assert.Single(records);
        Assert.Equal(60, records[0].Volume);
    }

    [Fact]
    public void Read_NonTimeBarKind_Throws()
    {
        var reader = new PartitionedSourceReader();
        var altSource = new DataFeedDescriptor(_tempDir, "binance", "BTCUSDT",
            "EqV_1m_1000", DataFeedKind.AltBar);

        Assert.Throws<NotSupportedException>(() => reader.Read(altSource).ToList());
    }

    [Fact]
    public void Read_MissingDir_YieldsNothing()
    {
        var reader = new PartitionedSourceReader();
        var records = reader.Read(Source("DOESNT_EXIST", "1m")).ToList();
        Assert.Empty(records);
    }

    [Fact]
    public void Read_MalformedCell_Throws()
    {
        // Reviewer Issue 2 — malformed source cells must surface, not silently skip.
        // A skipped source bar shifts every downstream threshold-equivalence boundary,
        // producing structurally different alt-bars than the user expects.
        var dir = CandlesDir("MALFORMED");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "2024-01_1m.csv");
        File.WriteAllLines(path,
        [
            "ts,o,h,l,c,vol",
            $"{Ts(2024, 1, 1, 0)},100,110,95,105,50",
            $"{Ts(2024, 1, 1, 1)},105,not-a-number,100,110,60",
        ]);

        var reader = new PartitionedSourceReader();
        var ex = Assert.Throws<FormatException>(() => reader.Read(Source("MALFORMED", "1m")).ToList());
        Assert.Contains("not-a-number", ex.Message, StringComparison.Ordinal);
        Assert.Contains("row 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'h'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TooFewColumns_Throws()
    {
        var dir = CandlesDir("SHORTROW");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "2024-01_1m.csv");
        File.WriteAllLines(path,
        [
            "ts,o,h,l,c,vol",
            $"{Ts(2024, 1, 1, 0)},100,110",   // truncated row
        ]);

        var reader = new PartitionedSourceReader();
        var ex = Assert.Throws<FormatException>(() => reader.Read(Source("SHORTROW", "1m")).ToList());
        Assert.Contains("at least 6", ex.Message, StringComparison.Ordinal);
    }
}

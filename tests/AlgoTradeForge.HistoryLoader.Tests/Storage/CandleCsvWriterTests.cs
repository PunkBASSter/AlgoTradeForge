using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class CandleCsvWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"CandleCsvWriterTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CandleRecord MakeRecord(long timestampMs, decimal open = 1m, decimal high = 2m,
        decimal low = 0.5m, decimal close = 1.5m, decimal volume = 100m) =>
        new(timestampMs, open, high, low, close, volume);

    private static string PartitionFile(string assetDir, string interval, long timestampMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        var partition = dt.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(assetDir, "candles", $"{partition}_{interval}.csv");
    }

    // -------------------------------------------------------------------------
    // Write_NewFile_CreatesWithHeader
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_NewFile_CreatesWithHeader()
    {
        var writer = new CandleCsvWriter();
        var assetDir = Path.Combine(_tempDir, "BTCUSDT");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = MakeRecord(ts);

        writer.Write(assetDir, "1h", record, decimalDigits: 2);

        var file = PartitionFile(assetDir, "1h", ts);
        Assert.True(File.Exists(file));
        var lines = File.ReadAllLines(file);
        Assert.Equal("ts,o,h,l,c,vol", lines[0]);
        Assert.Equal(2, lines.Length);
    }

    // -------------------------------------------------------------------------
    // Write_Int64Encoding_CorrectValues
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_Int64Encoding_CorrectValues()
    {
        var writer = new CandleCsvWriter();
        var assetDir = Path.Combine(_tempDir, "BTCUSDT");

        // January 2024 timestamp
        var ts = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var record = MakeRecord(ts, open: 50000.50m, high: 51000.75m, low: 49500.25m, close: 50500.00m, volume: 123.45m);

        writer.Write(assetDir, "1h", record, decimalDigits: 2);

        var file = PartitionFile(assetDir, "1h", ts);
        var dataLine = File.ReadAllLines(file)[1];
        var fields = dataLine.Split(',');

        Assert.Equal(ts.ToString(), fields[0]);
        Assert.Equal("5000050",  fields[1]); // 50000.50 * 100 = 5000050
        Assert.Equal("5100075",  fields[2]); // 51000.75 * 100 = 5100075
        Assert.Equal("4950025",  fields[3]); // 49500.25 * 100 = 4950025
        Assert.Equal("5050000",  fields[4]); // 50500.00 * 100 = 5050000
        Assert.Equal("12345",    fields[5]); // 123.45   * 100 = 12345
    }

    // -------------------------------------------------------------------------
    // Write_MonthBoundary_CreatesSeparatePartitions
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_MonthBoundary_CreatesSeparatePartitions()
    {
        var writer = new CandleCsvWriter();
        var assetDir = Path.Combine(_tempDir, "ETHUSDT");

        var tsJan = new DateTimeOffset(2024, 1, 31, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var tsFeb = new DateTimeOffset(2024, 2,  1,  0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.Write(assetDir, "1h", MakeRecord(tsJan), decimalDigits: 2);
        writer.Write(assetDir, "1h", MakeRecord(tsFeb), decimalDigits: 2);

        var janFile = PartitionFile(assetDir, "1h", tsJan);
        var febFile = PartitionFile(assetDir, "1h", tsFeb);

        Assert.True(File.Exists(janFile), $"January file not found: {janFile}");
        Assert.True(File.Exists(febFile), $"February file not found: {febFile}");
        Assert.NotEqual(janFile, febFile);

        // Each file has header + 1 data line
        Assert.Equal(2, File.ReadAllLines(janFile).Length);
        Assert.Equal(2, File.ReadAllLines(febFile).Length);
    }

    // -------------------------------------------------------------------------
    // Write_Dedup_SkipsDuplicateTimestamp
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_Dedup_SkipsDuplicateTimestamp()
    {
        var writer = new CandleCsvWriter();
        var assetDir = Path.Combine(_tempDir, "SOLUSDT");

        var ts = new DateTimeOffset(2024, 3, 10, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.Write(assetDir, "1h", MakeRecord(ts, open: 100m), decimalDigits: 2);
        writer.Write(assetDir, "1h", MakeRecord(ts, open: 200m), decimalDigits: 2); // duplicate — must be skipped

        var file = PartitionFile(assetDir, "1h", ts);
        var lines = File.ReadAllLines(file);

        // Only header + 1 data line (second write was skipped)
        Assert.Equal(2, lines.Length);
        // First write value: 100 * 100 = 10000
        Assert.Equal("10000", lines[1].Split(',')[1]);
    }

    // -------------------------------------------------------------------------
    // ResumeFrom_ExistingFile_ReturnsLastTimestamp
    // -------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_ExistingFile_ReturnsLastTimestamp()
    {
        var writerA = new CandleCsvWriter();
        var assetDir = Path.Combine(_tempDir, "BNBUSDT");

        var ts1 = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ts2 = new DateTimeOffset(2024, 5, 1, 1, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ts3 = new DateTimeOffset(2024, 5, 1, 2, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writerA.Write(assetDir, "1h", MakeRecord(ts1), decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts2), decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts3), decimalDigits: 2);

        // New writer instance — simulates restart; it has no in-memory dedup state
        var writerB = new CandleCsvWriter();
        var resumed = writerB.ResumeFrom(assetDir, "1h");

        Assert.Equal(ts3, resumed);
    }

    // -------------------------------------------------------------------------
    // ResumeFrom_NoFiles_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_NoFiles_ReturnsNull()
    {
        var writer = new CandleCsvWriter();
        var assetDir = Path.Combine(_tempDir, "XRPUSDT");

        var result = writer.ResumeFrom(assetDir, "1h");

        Assert.Null(result);
    }
}

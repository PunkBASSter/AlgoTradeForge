using System.Globalization;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class FeedCsvWriterTests : IDisposable
{
    private readonly string _tempDir;

    public FeedCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FeedCsvWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // 2024-01-15 00:00:00 UTC in epoch ms
    private static readonly long Ts20240115 = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------------------
    // 1. Write_NewFile_CreatesWithCorrectHeader
    // ---------------------------------------------------------------------------

    [Fact]
    public void Write_NewFile_CreatesWithCorrectHeader()
    {
        var writer = new FeedCsvWriter();
        var columns = new[] { "fundingRate", "markPrice" };
        var record = new FeedRecord(Ts20240115, [0.0001, 50000.0]);

        writer.Write(_tempDir, "funding-rate", "", columns, record);

        var filePath = Path.Combine(_tempDir, "funding-rate", "2024-01.csv");
        Assert.True(File.Exists(filePath));

        var lines = File.ReadAllLines(filePath);
        Assert.Equal("ts,fundingRate,markPrice", lines[0]);
    }

    // ---------------------------------------------------------------------------
    // 2. Write_DoubleValues_FormattedWithInvariantCulture
    // ---------------------------------------------------------------------------

    [Fact]
    public void Write_DoubleValues_FormattedWithInvariantCulture()
    {
        var writer = new FeedCsvWriter();
        var columns = new[] { "openInterest" };
        // Use a value that varies between cultures (decimal separator)
        var record = new FeedRecord(Ts20240115, [1234567.89]);

        writer.Write(_tempDir, "open-interest", "5m", columns, record);

        var filePath = Path.Combine(_tempDir, "open-interest", "2024-01_5m.csv");
        var lines = File.ReadAllLines(filePath);

        // data line is index 1; value must use '.' not ','
        Assert.Equal(2, lines.Length);
        var dataPart = lines[1].Split(',');
        Assert.Equal(Ts20240115.ToString(CultureInfo.InvariantCulture), dataPart[0]);
        Assert.Equal("1234567.89", dataPart[1]);
    }

    // ---------------------------------------------------------------------------
    // 3. Write_NoInterval_OmitsIntervalFromFilename
    // ---------------------------------------------------------------------------

    [Fact]
    public void Write_NoInterval_OmitsIntervalFromFilename()
    {
        var writer = new FeedCsvWriter();
        var record = new FeedRecord(Ts20240115, [0.0001]);

        writer.Write(_tempDir, "funding-rate", "", ["rate"], record);

        var expectedPath = Path.Combine(_tempDir, "funding-rate", "2024-01.csv");
        var unexpectedPath = Path.Combine(_tempDir, "funding-rate", "2024-01_.csv");

        Assert.True(File.Exists(expectedPath), $"Expected file not found: {expectedPath}");
        Assert.False(File.Exists(unexpectedPath), $"Unexpected file with trailing underscore found: {unexpectedPath}");
    }

    // ---------------------------------------------------------------------------
    // 4. Write_WithInterval_IncludesIntervalInFilename
    // ---------------------------------------------------------------------------

    [Fact]
    public void Write_WithInterval_IncludesIntervalInFilename()
    {
        var writer = new FeedCsvWriter();
        var record = new FeedRecord(Ts20240115, [999999.0]);

        writer.Write(_tempDir, "open-interest", "5m", ["oi"], record);

        var expectedPath = Path.Combine(_tempDir, "open-interest", "2024-01_5m.csv");
        Assert.True(File.Exists(expectedPath), $"Expected file not found: {expectedPath}");
    }

    // ---------------------------------------------------------------------------
    // 5. Write_Dedup_SkipsDuplicateTimestamp
    // ---------------------------------------------------------------------------

    [Fact]
    public void Write_Dedup_SkipsDuplicateTimestamp()
    {
        var writer = new FeedCsvWriter();
        var columns = new[] { "rate" };
        var record = new FeedRecord(Ts20240115, [0.0001]);

        writer.Write(_tempDir, "funding-rate", "", columns, record);
        writer.Write(_tempDir, "funding-rate", "", columns, record); // duplicate — must be skipped

        var filePath = Path.Combine(_tempDir, "funding-rate", "2024-01.csv");
        var lines = File.ReadAllLines(filePath);

        // header + exactly one data line
        Assert.Equal(2, lines.Length);
    }

    // ---------------------------------------------------------------------------
    // 6. ResumeFrom_ExistingFile_ReturnsLastTimestamp
    // ---------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_ExistingFile_ReturnsLastTimestamp()
    {
        var writer = new FeedCsvWriter();
        var columns = new[] { "rate" };

        var ts1 = Ts20240115;
        var ts2 = Ts20240115 + 8 * 60 * 60 * 1000L; // +8 hours

        writer.Write(_tempDir, "funding-rate", "", columns, new FeedRecord(ts1, [0.0001]));
        writer.Write(_tempDir, "funding-rate", "", columns, new FeedRecord(ts2, [0.0002]));

        var result = writer.ResumeFrom(_tempDir, "funding-rate", "");

        Assert.Equal(ts2, result);
    }
}

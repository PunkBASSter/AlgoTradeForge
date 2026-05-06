using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class DailyBookTickerCsvWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _bookTickerDir;
    private readonly List<DailyBookTickerCsvWriter> _writers = [];

    public DailyBookTickerCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ATF-BookTicker-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _bookTickerDir = Path.Combine(_tempDir, FeedNames.BookTicker);
    }

    private DailyBookTickerCsvWriter NewWriter()
    {
        var w = new DailyBookTickerCsvWriter();
        _writers.Add(w);
        return w;
    }

    // FileShare.ReadWrite — coexists with the writer's cached FileAccess.Write+FileShare.Read handle.
    private static string[] ReadCoexistingLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) is not null)
            lines.Add(line);
        return lines.ToArray();
    }

    private static FeedRecord Sample(long ts, double bidPrice, double askPrice, long updateId) =>
        new(ts, [bidPrice, 1.0, askPrice, 2.0, updateId]);

    [Fact]
    public void Write_AppendsHeaderAndRow()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Sample(1_700_000_000_000L, 50000.5, 50001.0, 100));

        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L).UtcDateTime
            .ToString("yyyy-MM-dd");
        var file = Path.Combine(_bookTickerDir, $"{dayKey}.csv");

        Assert.True(File.Exists(file));
        var lines = ReadCoexistingLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal("ts,bid_price,bid_qty,ask_price,ask_qty,update_id", lines[0]);
        Assert.Contains("1700000000000", lines[1]);
        Assert.Contains("50000.5", lines[1]);
        Assert.Contains("50001", lines[1]);
        Assert.EndsWith(",100", lines[1]);
    }

    [Fact]
    public void Write_DedupsByUpdateId()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Sample(1_700_000_000_000L, 1, 2, 100));
        writer.Write(_tempDir, Sample(1_700_000_001_000L, 99, 99, 100));
        writer.Write(_tempDir, Sample(1_700_000_002_000L, 99, 99, 50));
        writer.Write(_tempDir, Sample(1_700_000_003_000L, 3, 4, 101));

        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L).UtcDateTime
            .ToString("yyyy-MM-dd");
        var file = Path.Combine(_bookTickerDir, $"{dayKey}.csv");
        var lines = ReadCoexistingLines(file);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void ResumeFrom_NewDirectory_ReturnsNull()
    {
        var writer = NewWriter();
        Assert.Null(writer.ResumeFrom(_tempDir));
    }

    [Fact]
    public void ResumeFrom_AfterWrites_ReturnsLatestUpdateIdAndTs()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Sample(1_700_000_000_000L, 1, 2, 100));
        writer.Write(_tempDir, Sample(1_700_000_001_000L, 3, 4, 101));
        writer.Write(_tempDir, Sample(1_700_000_002_000L, 5, 6, 102));

        writer.Dispose();
        using var fresh = new DailyBookTickerCsvWriter();
        var resume = fresh.ResumeFrom(_tempDir);

        Assert.NotNull(resume);
        Assert.Equal(102, resume.Value.LastUpdateId);
        Assert.Equal(1_700_000_002_000L, resume.Value.LastTsMs);
    }

    [Fact]
    public void Write_RejectsWrongValueCount()
    {
        var writer = NewWriter();
        var bad = new FeedRecord(1L, [1.0, 2.0, 3.0]);
        Assert.Throws<ArgumentException>(() => writer.Write(_tempDir, bad));
    }

    public void Dispose()
    {
        // Dispose before recursive-delete — Windows file-share rules trigger IOException otherwise.
        foreach (var w in _writers)
            w.Dispose();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

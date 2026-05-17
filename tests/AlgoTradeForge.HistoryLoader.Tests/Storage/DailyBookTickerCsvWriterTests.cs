using AlgoTradeForge.Application.IO;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Infrastructure.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class DailyBookTickerCsvWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _bookTickerDir;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DailyBookTickerCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ATF-BookTicker-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _bookTickerDir = Path.Combine(_tempDir, FeedNames.BookTicker);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _tempDir });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private DailyBookTickerCsvWriter NewWriter(WriteLockManager? locks = null)
        => new(
            _storage,
            _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyBookTickerCsvWriter>.Instance,
            locks ?? new WriteLockManager());

    private static FeedRecord Sample(long ts, double bidPrice, double askPrice, long updateId) =>
        new(ts, [bidPrice, 1.0, askPrice, 2.0, updateId]);

    private static string PartitionKey(string tempDir, long ts)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime.ToString("yyyy-MM-dd");
        return Path.Combine(tempDir, FeedNames.BookTicker, $"{day}.csv");
    }

    [Fact]
    public async Task Write_AppendsHeaderAndRow()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Sample(1_700_000_000_000L, 50000.5, 50001.0, 100));
        await writer.FlushAllAsync(Ct);

        var key = PartitionKey(_tempDir, 1_700_000_000_000L);
        Assert.True(await _storage.Exists(key, Ct));
        var lines = await _storage.ReadAllLines(key, Ct);
        Assert.Equal(2, lines.Length);
        Assert.Equal("ts,bid_price,bid_qty,ask_price,ask_qty,update_id", lines[0]);
        Assert.Contains("1700000000000", lines[1]);
        Assert.Contains("50000.5", lines[1]);
        Assert.Contains("50001", lines[1]);
        Assert.EndsWith(",100", lines[1]);
    }

    [Fact]
    public async Task Write_DedupsByUpdateId()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Sample(1_700_000_000_000L, 1, 2, 100));
        writer.Write(_tempDir, Sample(1_700_000_001_000L, 99, 99, 100));
        writer.Write(_tempDir, Sample(1_700_000_002_000L, 99, 99, 50));
        writer.Write(_tempDir, Sample(1_700_000_003_000L, 3, 4, 101));
        await writer.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(PartitionKey(_tempDir, 1_700_000_000_000L), Ct);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task ResumeFrom_NewDirectory_ReturnsNull()
    {
        var writer = NewWriter();
        Assert.Null(await writer.ResumeFrom(_tempDir, Ct));
    }

    [Fact]
    public async Task ResumeFrom_AfterWrites_ReturnsLatestUpdateIdAndTs()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Sample(1_700_000_000_000L, 1, 2, 100));
        writer.Write(_tempDir, Sample(1_700_000_001_000L, 3, 4, 101));
        writer.Write(_tempDir, Sample(1_700_000_002_000L, 5, 6, 102));
        await writer.FlushAllAsync(Ct);

        var fresh = NewWriter();
        var resume = await fresh.ResumeFrom(_tempDir, Ct);

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
}

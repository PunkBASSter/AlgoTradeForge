using AlgoTradeForge.Application.IO;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Infrastructure.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// Tick writer behaviour post-PR3 (buffer-then-PUT). Torn-row recovery + cached handle tests
/// from the per-row-append era are gone — atomic publish makes both moot.
/// </summary>
public sealed class DailyTickCsvWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _ticksDir;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DailyTickCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DailyTickCsvWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _ticksDir = Path.Combine(_tempDir, FeedNames.Ticks);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _tempDir });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private DailyTickCsvWriter NewWriter(WriteLockManager? locks = null, int flushEveryRows = 1)
        => new(
            _storage,
            _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = flushEveryRows, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance,
            locks ?? new WriteLockManager());

    private static readonly long Ts20240315Noon =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static FeedRecord Tick(long ts, double price, double qty, bool isBuyerMaker, long aggId) =>
        new(ts, [price, qty, isBuyerMaker ? 1.0 : 0.0, aggId]);

    private static string PartitionKey(string assetDir, long timestampMs)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime.ToString("yyyy-MM-dd");
        return Path.Combine(assetDir, FeedNames.Ticks, $"{day}.csv");
    }

    [Fact]
    public async Task Write_NewFile_CreatesWithCorrectHeaderAndRow()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon, 50000.5, 0.123, isBuyerMaker: false, aggId: 100));
        await writer.FlushAllAsync(Ct);

        var key = PartitionKey(_tempDir, Ts20240315Noon);
        Assert.True(await _storage.Exists(key, Ct));
        var lines = await _storage.ReadAllLines(key, Ct);
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        Assert.Equal($"{Ts20240315Noon},50000.5,0.123,0,100", lines[1]);
    }

    [Fact]
    public async Task Write_DedupsByAggId_DropsRepeatsRegardlessOfTs()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon,      50000, 1, false, aggId: 100));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 5,  50001, 2, true,  aggId: 100));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 10, 50002, 3, false, aggId: 100));
        await writer.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(PartitionKey(_tempDir, Ts20240315Noon), Ct);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task ResumeFrom_NoFiles_ReturnsNull()
    {
        var writer = NewWriter();
        Assert.Null(await writer.ResumeFrom(_tempDir, Ct));
    }

    [Fact]
    public async Task ResumeFrom_HeaderOnlyFile_ReturnsNull()
    {
        Directory.CreateDirectory(_ticksDir);
        await File.WriteAllTextAsync(
            Path.Combine(_ticksDir, "2024-03-15.csv"),
            "ts,price,qty,is_buyer_maker,agg_id\n",
            Ct);

        var writer = NewWriter();
        Assert.Null(await writer.ResumeFrom(_tempDir, Ct));
    }

    [Fact]
    public async Task ResumeFrom_CleanFile_ReturnsLastRow()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon,        50000, 1, false, aggId: 100));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 1000, 50100, 1, true,  aggId: 101));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 2000, 50200, 1, false, aggId: 102));
        await writer.FlushAllAsync(Ct);

        var fresh = NewWriter();
        var resume = await fresh.ResumeFrom(_tempDir, Ct);

        Assert.NotNull(resume);
        Assert.Equal(102, resume!.Value.LastAggId);
        Assert.Equal(Ts20240315Noon + 2000, resume.Value.LastTsMs);
    }

    [Fact]
    public async Task Write_AfterResumeFrom_DedupsReplayedAggIds()
    {
        var writer = NewWriter();
        for (int i = 100; i <= 110; i++)
            writer.Write(_tempDir, Tick(Ts20240315Noon + i, 50000 + i, 1, false, aggId: i));
        await writer.FlushAllAsync(Ct);

        var fresh = NewWriter();
        var resume = await fresh.ResumeFrom(_tempDir, Ct);
        Assert.NotNull(resume);
        Assert.Equal(110, resume!.Value.LastAggId);

        // Simulate Binance redelivery overlap: 109, 110 must be deduped; 111, 112 appended.
        fresh.Write(_tempDir, Tick(Ts20240315Noon + 109, 50109, 1, false, aggId: 109));
        fresh.Write(_tempDir, Tick(Ts20240315Noon + 110, 50110, 1, false, aggId: 110));
        fresh.Write(_tempDir, Tick(Ts20240315Noon + 111, 50111, 1, false, aggId: 111));
        fresh.Write(_tempDir, Tick(Ts20240315Noon + 112, 50112, 1, false, aggId: 112));
        await fresh.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(PartitionKey(_tempDir, Ts20240315Noon), Ct);
        Assert.Equal(14, lines.Length); // header + 11 original + 2 new
        Assert.EndsWith(",112", lines[^1]);
    }

    [Fact]
    public async Task Write_DayBoundary_RoutesToCorrectPartition()
    {
        var writer = NewWriter();
        var day1 = new DateTimeOffset(2024, 3, 15, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var day2 = new DateTimeOffset(2024, 3, 16, 0, 0, 1, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.Write(_tempDir, Tick(day1, 50000, 1, false, aggId: 200));
        writer.Write(_tempDir, Tick(day1 + 100, 50001, 1, false, aggId: 201));
        writer.Write(_tempDir, Tick(day2, 50010, 1, false, aggId: 202));
        await writer.FlushAllAsync(Ct);

        var day1Lines = await _storage.ReadAllLines(PartitionKey(_tempDir, day1), Ct);
        Assert.Equal(3, day1Lines.Length); // header + 2 rows
        Assert.EndsWith(",201", day1Lines[^1]);

        var day2Lines = await _storage.ReadAllLines(PartitionKey(_tempDir, day2), Ct);
        Assert.Equal(2, day2Lines.Length); // header + 1 row
        Assert.EndsWith(",202", day2Lines[^1]);
    }

    [Fact]
    public async Task ResumeFrom_PicksLatestDay_WhenMultiplePartitionsExist()
    {
        Directory.CreateDirectory(_ticksDir);
        await File.WriteAllTextAsync(
            Path.Combine(_ticksDir, "2024-03-14.csv"),
            "ts,price,qty,is_buyer_maker,agg_id\n1,100,1,0,50\n",
            Ct);
        await File.WriteAllTextAsync(
            Path.Combine(_ticksDir, "2024-03-15.csv"),
            "ts,price,qty,is_buyer_maker,agg_id\n2,200,1,0,75\n",
            Ct);

        var writer = NewWriter();
        var resume = await writer.ResumeFrom(_tempDir, Ct);
        Assert.NotNull(resume);
        Assert.Equal(75, resume!.Value.LastAggId);
        Assert.Equal(2, resume.Value.LastTsMs);
    }

    [Fact]
    public void Write_InvalidValueCount_Throws()
    {
        var writer = NewWriter();
        var bad = new FeedRecord(Ts20240315Noon, [50000, 1.0]);

        Assert.Throws<ArgumentException>(() => writer.Write(_tempDir, bad));
    }
}

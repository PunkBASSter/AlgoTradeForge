using AlgoTradeForge.Application.IO;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Infrastructure.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class CandleCsvWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"CandleCsvWriterTests_{Guid.NewGuid():N}");
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CandleCsvWriterTests()
    {
        Directory.CreateDirectory(_tempDir);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _tempDir });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private CandleCsvWriter NewWriter(WriteLockManager? locks = null, int flushEveryRows = 1)
        => new(
            _storage,
            _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = flushEveryRows, FlushIntervalSeconds = 60 }),
            NullLogger<CandleCsvWriter>.Instance,
            locks ?? new WriteLockManager());

    private static CandleRecord MakeRecord(long timestampMs, decimal open = 1m, decimal high = 2m,
        decimal low = 0.5m, decimal close = 1.5m, decimal volume = 100m) =>
        new(timestampMs, open, high, low, close, volume);

    private Task<string[]> ReadLines(string key) => _storage.ReadAllLines(key, Ct);

    private static string PartitionKey(string assetDir, string interval, long timestampMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        var partition = dt.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(assetDir, "candles", $"{partition}_{interval}.csv");
    }

    [Fact]
    public async Task Write_NewFile_CreatesWithHeader()
    {
        var writer = NewWriter();
        var assetDir = Path.Combine(_tempDir, "BTCUSDT");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        writer.Write(assetDir, "1h", MakeRecord(ts), decimalDigits: 2);
        await writer.FlushAllAsync(Ct);

        var key = PartitionKey(assetDir, "1h", ts);
        Assert.True(await _storage.Exists(key, Ct));
        var lines = await ReadLines(key);
        Assert.Equal("ts,o,h,l,c,vol", lines[0]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task Write_Int64Encoding_CorrectValues()
    {
        var writer = NewWriter();
        var assetDir = Path.Combine(_tempDir, "BTCUSDT");
        var ts = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.Write(assetDir, "1h", MakeRecord(ts, open: 50000.50m, high: 51000.75m, low: 49500.25m, close: 50500.00m, volume: 123.45m), decimalDigits: 2);
        await writer.FlushAllAsync(Ct);

        var lines = await ReadLines(PartitionKey(assetDir, "1h", ts));
        var fields = lines[1].Split(',');

        Assert.Equal(ts.ToString(), fields[0]);
        Assert.Equal("5000050", fields[1]);
        Assert.Equal("5100075", fields[2]);
        Assert.Equal("4950025", fields[3]);
        Assert.Equal("5050000", fields[4]);
        Assert.Equal("12345",   fields[5]);
    }

    [Fact]
    public async Task Write_MonthBoundary_CreatesSeparatePartitions()
    {
        var writer = NewWriter();
        var assetDir = Path.Combine(_tempDir, "ETHUSDT");
        var tsJan = new DateTimeOffset(2024, 1, 31, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var tsFeb = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.Write(assetDir, "1h", MakeRecord(tsJan), decimalDigits: 2);
        writer.Write(assetDir, "1h", MakeRecord(tsFeb), decimalDigits: 2);
        await writer.FlushAllAsync(Ct);

        var janKey = PartitionKey(assetDir, "1h", tsJan);
        var febKey = PartitionKey(assetDir, "1h", tsFeb);
        Assert.True(await _storage.Exists(janKey, Ct));
        Assert.True(await _storage.Exists(febKey, Ct));
        Assert.NotEqual(janKey, febKey);
        Assert.Equal(2, (await ReadLines(janKey)).Length);
        Assert.Equal(2, (await ReadLines(febKey)).Length);
    }

    [Fact]
    public async Task Write_Dedup_SkipsDuplicateTimestamp()
    {
        var writer = NewWriter();
        var assetDir = Path.Combine(_tempDir, "SOLUSDT");
        var ts = new DateTimeOffset(2024, 3, 10, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.Write(assetDir, "1h", MakeRecord(ts, open: 100m), decimalDigits: 2);
        writer.Write(assetDir, "1h", MakeRecord(ts, open: 200m), decimalDigits: 2); // duplicate
        await writer.FlushAllAsync(Ct);

        var lines = await ReadLines(PartitionKey(assetDir, "1h", ts));
        Assert.Equal(2, lines.Length);
        Assert.Equal("10000", lines[1].Split(',')[1]); // first write wins
    }

    [Fact]
    public async Task ResumeFrom_ExistingFile_ReturnsLastTimestamp()
    {
        var writerA = NewWriter();
        var assetDir = Path.Combine(_tempDir, "BNBUSDT");
        var ts1 = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ts2 = new DateTimeOffset(2024, 5, 1, 1, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ts3 = new DateTimeOffset(2024, 5, 1, 2, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writerA.Write(assetDir, "1h", MakeRecord(ts1), decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts2), decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts3), decimalDigits: 2);
        await writerA.FlushAllAsync(Ct);

        var writerB = NewWriter();
        Assert.Equal(ts3, await writerB.ResumeFrom(assetDir, "1h", Ct));
    }

    [Fact]
    public async Task ResumeFrom_NoFiles_ReturnsNull()
    {
        var writer = NewWriter();
        var assetDir = Path.Combine(_tempDir, "XRPUSDT");
        Assert.Null(await writer.ResumeFrom(assetDir, "1h", Ct));
    }

    [Fact]
    public async Task Write_ConcurrentSameTimestamp_OnlyOneLineWritten()
    {
        var locks = new WriteLockManager();
        var writer = NewWriter(locks);
        var assetDir = Path.Combine(_tempDir, "CONCURRENT");
        var ts = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            writer.Write(assetDir, "1h", MakeRecord(ts), decimalDigits: 2)));
        await Task.WhenAll(tasks);
        await writer.FlushAllAsync(Ct);

        var lines = await ReadLines(PartitionKey(assetDir, "1h", ts));
        Assert.Equal(2, lines.Length); // header + exactly 1 data row despite 10 concurrent writes
    }

    [Fact]
    public async Task ResumeFrom_RestoresDedupWatermark_NoBufferHydration()
    {
        var writerA = NewWriter();
        var assetDir = Path.Combine(_tempDir, "RESUME_DEDUP");
        var ts1 = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ts2 = ts1 + TimeSpan.FromHours(1).Ticks / 10_000;

        writerA.Write(assetDir, "1h", MakeRecord(ts1, open: 100m), decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts2, open: 200m), decimalDigits: 2);
        await writerA.FlushAllAsync(Ct);

        var writerB = NewWriter();
        await writerB.ResumeFrom(assetDir, "1h", Ct);

        // Replay the second row; the watermark should dedup it.
        writerB.Write(assetDir, "1h", MakeRecord(ts2, open: 999m), decimalDigits: 2);
        await writerB.FlushAllAsync(Ct);

        var lines = await ReadLines(PartitionKey(assetDir, "1h", ts2));
        Assert.Equal(3, lines.Length); // header + 2 original rows; replay rejected
        Assert.Equal("20000", lines[2].Split(',')[1]); // ts2's original open=200, not the replay's 999
    }
}

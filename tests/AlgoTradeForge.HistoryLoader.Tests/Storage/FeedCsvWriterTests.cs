using System.Globalization;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class FeedCsvWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FeedCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FeedCsvWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _tempDir });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private FeedCsvWriter NewWriter(WriteLockManager? locks = null)
        => new(
            _storage,
            _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<FeedCsvWriter>.Instance,
            locks ?? new WriteLockManager());

    private static readonly long Ts20240115 = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static string PartitionKey(string assetDir, string feedName, string interval, long timestampMs)
    {
        var partitionDate = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;
        var fileName = string.IsNullOrEmpty(interval)
            ? $"{partitionDate:yyyy-MM}.csv"
            : $"{partitionDate:yyyy-MM}_{interval}.csv";
        return Path.Combine(assetDir, feedName, fileName);
    }

    [Fact]
    public async Task Write_NewFile_CreatesWithCorrectHeader()
    {
        var writer = NewWriter();
        var columns = new[] { "fundingRate", "markPrice" };
        var record = new FeedRecord(Ts20240115, [0.0001, 50000.0]);

        writer.Write(_tempDir, "funding-rate", "", columns, record);
        await writer.FlushAllAsync(Ct);

        var key = PartitionKey(_tempDir, "funding-rate", "", Ts20240115);
        Assert.True(await _storage.Exists(key, Ct));
        var lines = await _storage.ReadAllLines(key, Ct);
        Assert.Equal("ts,fundingRate,markPrice", lines[0]);
    }

    [Fact]
    public async Task Write_DoubleValues_FormattedWithInvariantCulture()
    {
        var writer = NewWriter();
        var record = new FeedRecord(Ts20240115, [1234567.89]);

        writer.Write(_tempDir, "open-interest", "5m", ["openInterest"], record);
        await writer.FlushAllAsync(Ct);

        var key = PartitionKey(_tempDir, "open-interest", "5m", Ts20240115);
        var lines = await _storage.ReadAllLines(key, Ct);
        Assert.Equal(2, lines.Length);
        var dataPart = lines[1].Split(',');
        Assert.Equal(Ts20240115.ToString(CultureInfo.InvariantCulture), dataPart[0]);
        Assert.Equal("1234567.89", dataPart[1]);
    }

    [Fact]
    public async Task Write_NoInterval_OmitsIntervalFromFilename()
    {
        var writer = NewWriter();
        var record = new FeedRecord(Ts20240115, [0.0001]);

        writer.Write(_tempDir, "funding-rate", "", ["rate"], record);
        await writer.FlushAllAsync(Ct);

        Assert.True(await _storage.Exists(PartitionKey(_tempDir, "funding-rate", "", Ts20240115), Ct));
        Assert.False(await _storage.Exists(Path.Combine(_tempDir, "funding-rate", "2024-01_.csv"), Ct));
    }

    [Fact]
    public async Task Write_WithInterval_IncludesIntervalInFilename()
    {
        var writer = NewWriter();
        var record = new FeedRecord(Ts20240115, [999999.0]);

        writer.Write(_tempDir, "open-interest", "5m", ["oi"], record);
        await writer.FlushAllAsync(Ct);

        Assert.True(await _storage.Exists(PartitionKey(_tempDir, "open-interest", "5m", Ts20240115), Ct));
    }

    [Fact]
    public async Task Write_Dedup_SkipsDuplicateTimestamp()
    {
        var writer = NewWriter();
        var record = new FeedRecord(Ts20240115, [0.0001]);

        writer.Write(_tempDir, "funding-rate", "", ["rate"], record);
        writer.Write(_tempDir, "funding-rate", "", ["rate"], record);
        await writer.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(PartitionKey(_tempDir, "funding-rate", "", Ts20240115), Ct);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task ResumeFrom_ExistingFile_ReturnsLastTimestamp()
    {
        var writer = NewWriter();
        var ts1 = Ts20240115;
        var ts2 = Ts20240115 + 8 * 60 * 60 * 1000L;

        writer.Write(_tempDir, "funding-rate", "", ["rate"], new FeedRecord(ts1, [0.0001]));
        writer.Write(_tempDir, "funding-rate", "", ["rate"], new FeedRecord(ts2, [0.0002]));
        await writer.FlushAllAsync(Ct);

        var fresh = NewWriter();
        Assert.Equal(ts2, await fresh.ResumeFrom(_tempDir, "funding-rate", "", Ct));
    }

    [Fact]
    public async Task Write_ConcurrentSameTimestamp_OnlyOneLineWritten()
    {
        var writer = NewWriter();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            writer.Write(_tempDir, "concurrent-feed", "", ["rate"],
                new FeedRecord(Ts20240115, [0.0001]))));
        await Task.WhenAll(tasks);
        await writer.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(PartitionKey(_tempDir, "concurrent-feed", "", Ts20240115), Ct);
        Assert.Equal(2, lines.Length);
    }
}

using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class DailySessionCsvWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public DailySessionCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DailySessionCsvWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _tempDir });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private DailySessionCsvWriter NewWriter() => new(
        _storage, _tail,
        Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
        NullLogger<DailySessionCsvWriter>.Instance, new WriteLockManager());

    private static FeedRecord Session(long ts, int kind) => new(ts, [kind]);

    private static string PartitionKey(string dir, long ts)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
        return Path.Combine(dir, FeedNames.Session, $"{day:yyyy-MM-dd}.csv");
    }

    [Fact]
    public async Task Write_NewFile_CreatesHeaderAndRow()
    {
        var w = NewWriter();
        w.Write(_tempDir, Session(Ts, kind: 1)); // SessionStart
        await w.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(PartitionKey(_tempDir, Ts), Ct);
        Assert.Equal("ts,kind", lines[0]);
        Assert.Equal($"{Ts},1", lines[1]);
    }

    [Fact]
    public async Task Write_DedupsByTimestamp()
    {
        var w = NewWriter();
        w.Write(_tempDir, Session(Ts, 0));
        w.Write(_tempDir, Session(Ts, 0));      // same ts -> dropped
        w.Write(_tempDir, Session(Ts + 1, 0));
        await w.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(PartitionKey(_tempDir, Ts), Ct);
        Assert.Equal(3, lines.Length); // header + 2
    }

    [Fact]
    public async Task ResumeFrom_CleanFile_ReturnsLastTs()
    {
        var w = NewWriter();
        w.Write(_tempDir, Session(Ts, 0));
        w.Write(_tempDir, Session(Ts + 1000, 0));
        await w.FlushAllAsync(Ct);

        var resume = await NewWriter().ResumeFrom(_tempDir, Ct);
        Assert.NotNull(resume);
        Assert.Equal(Ts + 1000, resume!.Value.LastTsMs);
    }
}

using System.Runtime.CompilerServices;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage.Buffered;

/// <summary>
/// Tests the cross-cutting behaviour of the BufferedPartitionWriter base via its CandleCsvWriter
/// derivative: resume reads the watermark only (no full-partition hydration), threshold and
/// periodic flushes publish atomically, and the failure path retains rows for retry.
/// </summary>
public sealed class BufferedPartitionWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BufferedPartitionWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BufferedWriter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _tempDir });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private CandleCsvWriter NewWriter(IFileStorage storage, IPartitionTailIndex tail, int flushEveryRows = 10000, int flushIntervalSeconds = 60)
        => new(
            storage,
            tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = flushEveryRows, FlushIntervalSeconds = flushIntervalSeconds }),
            NullLogger<CandleCsvWriter>.Instance,
            new WriteLockManager());

    private static CandleRecord MakeRecord(long ts) => new(ts, 1m, 2m, 0.5m, 1.5m, 100m);

    [Fact]
    public async Task ResumeAfterCrash_HydratesWatermarkOnly_NotBuffer()
    {
        // Seed a partition with 3 rows via writer A.
        var writerA = NewWriter(_storage, _tail, flushEveryRows: 1);
        var assetDir = Path.Combine(_tempDir, "RESUME_ONLY");
        var ts1 = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ts2 = ts1 + 3600_000;
        var ts3 = ts2 + 3600_000;

        writerA.Write(assetDir, "1h", MakeRecord(ts1), decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts2), decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts3), decimalDigits: 2);
        await writerA.FlushAllAsync(Ct);

        // Writer B picks up. Resume should set the watermark but not load any rows.
        var writerB = NewWriter(_storage, _tail);
        Assert.Equal(ts3, await writerB.ResumeFrom(assetDir, "1h", Ct));

        // Replay ts2 — the watermark should dedup it.
        writerB.Write(assetDir, "1h", MakeRecord(ts2), decimalDigits: 2);
        await writerB.FlushAllAsync(Ct);

        // Disk content unchanged: header + 3 rows.
        var partitionKey = Path.Combine(assetDir, "candles", "2024-06_1h.csv");
        var lines = await _storage.ReadAllLines(partitionKey, Ct);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task TailIndexShortCircuit_DoesNotReadFullPartition()
    {
        // Seed via writer A (3 rows).
        var writerA = NewWriter(_storage, _tail, flushEveryRows: 1);
        var assetDir = Path.Combine(_tempDir, "SHORT_CIRCUIT");
        var ts1 = new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        writerA.Write(assetDir, "1h", MakeRecord(ts1),               decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts1 + 3600_000),    decimalDigits: 2);
        writerA.Write(assetDir, "1h", MakeRecord(ts1 + 7200_000),    decimalDigits: 2);
        await writerA.FlushAllAsync(Ct);

        // Wrap storage in a spy and let writer B resume; assert no ReadAllLines / ReadLines call.
        var spy = new RecordingStorage(_storage);
        var spyTail = new LocalTailIndex(spy);
        var writerB = NewWriter(spy, spyTail);

        await writerB.ResumeFrom(assetDir, "1h", Ct);

        Assert.Equal(0, spy.ReadAllLinesCalls);
        Assert.Equal(0, spy.ReadLinesCalls);
        Assert.True(spy.OpenReadCalls <= 1, $"expected OpenRead ≤ 1, got {spy.OpenReadCalls}");
    }

    [Fact]
    public async Task RowThresholdFlush_TriggersWithoutTimer()
    {
        var writer = NewWriter(_storage, _tail, flushEveryRows: 3, flushIntervalSeconds: 3600);
        var assetDir = Path.Combine(_tempDir, "ROW_THRESHOLD");
        var baseTs = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // Three writes — the third trips the threshold and kicks fire-and-forget flush.
        writer.Write(assetDir, "1h", MakeRecord(baseTs),              decimalDigits: 2);
        writer.Write(assetDir, "1h", MakeRecord(baseTs + 3600_000),   decimalDigits: 2);
        writer.Write(assetDir, "1h", MakeRecord(baseTs + 7200_000),   decimalDigits: 2);

        var partitionKey = Path.Combine(assetDir, "candles", "2024-09_1h.csv");

        // Spin briefly waiting for the async fire-and-forget flush; bounded to 2s.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && !await _storage.Exists(partitionKey, Ct))
            await Task.Delay(20, Ct);

        Assert.True(await _storage.Exists(partitionKey, Ct), "threshold flush did not publish partition within 2s");
    }

    [Fact]
    public async Task FlushFailure_RestoresRowsForRetry()
    {
        // flushEveryRows: 10 keeps the threshold flush from racing FlushAllAsync — both
        // explicit flushes are driven by the test, so the failure outcome is deterministic.
        var faulty = new FlakyWriteStorage(_storage, failTimes: 1);
        var writer = NewWriter(faulty, _tail, flushEveryRows: 10);
        var assetDir = Path.Combine(_tempDir, "FAILURE_RETRY");
        var ts = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.Write(assetDir, "1h", MakeRecord(ts), decimalDigits: 2);

        await Assert.ThrowsAsync<IOException>(() => writer.FlushAllAsync(Ct));
        await writer.FlushAllAsync(Ct); // second attempt: fault budget exhausted, succeeds

        var partitionKey = Path.Combine(assetDir, "candles", "2024-10_1h.csv");
        Assert.True(await _storage.Exists(partitionKey, Ct), "row was lost after recovering from one transient failure");
        var lines = await _storage.ReadAllLines(partitionKey, Ct);
        Assert.Equal(2, lines.Length); // header + the one rescued row
    }

    [Fact]
    public async Task PeriodicFlush_PublishesBufferedRows()
    {
        var assetDir = Path.Combine(_tempDir, "PERIODIC");
        var ts = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var writer = NewWriter(_storage, _tail, flushEveryRows: 10_000, flushIntervalSeconds: 1);
        var options = Options.Create(new HistoryLoaderStorageOptions { FlushIntervalSeconds = 1, FlushEveryRows = 10_000 });
        var service = new BufferedWriterFlushService(
            new IBufferedPartitionWriter[] { writer },
            options,
            NullLogger<BufferedWriterFlushService>.Instance);

        await service.StartAsync(Ct);
        try
        {
            writer.Write(assetDir, "1h", MakeRecord(ts), decimalDigits: 2);

            var partitionKey = Path.Combine(assetDir, "candles", "2024-11_1h.csv");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !await _storage.Exists(partitionKey, Ct))
                await Task.Delay(50, Ct);

            Assert.True(await _storage.Exists(partitionKey, Ct), "periodic timer did not flush within 5s");
        }
        finally
        {
            await service.StopAsync(Ct);
            service.Dispose();
        }
    }

    [Fact]
    public async Task ShutdownFlush_HonoursTimeout()
    {
        // Storage that hangs forever in WriteAllLines simulates a wedged S3 endpoint.
        var hanging = new HangingWriteStorage(_storage);
        var writer = NewWriter(hanging, _tail, flushEveryRows: 10_000, flushIntervalSeconds: 3600);
        var assetDir = Path.Combine(_tempDir, "SHUTDOWN");
        var ts = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        writer.Write(assetDir, "1h", MakeRecord(ts), decimalDigits: 2);

        var options = Options.Create(new HistoryLoaderStorageOptions
        {
            FlushIntervalSeconds = 3600,
            FlushEveryRows = 10_000,
            ShutdownFlushTimeoutSeconds = 1,
        });
        var service = new BufferedWriterFlushService(
            new IBufferedPartitionWriter[] { writer },
            options,
            NullLogger<BufferedWriterFlushService>.Instance);

        await service.StartAsync(Ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync(Ct);
        sw.Stop();
        service.Dispose();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"StopAsync did not honour the 1s shutdown timeout — took {sw.Elapsed.TotalSeconds:F2}s");
    }

    // --- spies / fault injectors --------------------------------------------

    private sealed class RecordingStorage : IFileStorage
    {
        private readonly IFileStorage _inner;
        public int OpenReadCalls;
        public int ReadAllLinesCalls;
        public int ReadLinesCalls;

        public RecordingStorage(IFileStorage inner) { _inner = inner; }

        public Task<bool> Exists(string key, CancellationToken ct = default) => _inner.Exists(key, ct);

        public IAsyncEnumerable<string> ListKeys(string prefix, string? suffix = null, bool recursive = true, CancellationToken ct = default)
            => _inner.ListKeys(prefix, suffix, recursive, ct);

        public Task<Stream> OpenRead(string key, CancellationToken ct = default)
        {
            Interlocked.Increment(ref OpenReadCalls);
            return _inner.OpenRead(key, ct);
        }

        public Task<string> ReadAllText(string key, CancellationToken ct = default) => _inner.ReadAllText(key, ct);

        public Task<string[]> ReadAllLines(string key, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ReadAllLinesCalls);
            return _inner.ReadAllLines(key, ct);
        }

        public async IAsyncEnumerable<string> ReadLines(string key, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref ReadLinesCalls);
            await foreach (var line in _inner.ReadLines(key, ct))
                yield return line;
        }

        public Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default) => _inner.ReadAllBytes(key, ct);
        public Task WriteAllText(string key, string content, System.Text.Encoding? encoding = null, CancellationToken ct = default) => _inner.WriteAllText(key, content, encoding, ct);
        public Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default) => _inner.WriteAllLines(key, lines, ct);
        public Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => _inner.WriteAllBytes(key, bytes, ct);
        public Task<IObjectWriteSession> OpenWriteSession(string key, CancellationToken ct = default) => _inner.OpenWriteSession(key, ct);
        public Task Delete(string key, CancellationToken ct = default) => _inner.Delete(key, ct);
        public Task DeleteByPrefix(string prefix, CancellationToken ct = default) => _inner.DeleteByPrefix(prefix, ct);
        public Task Move(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct = default) => _inner.Move(sourceKey, destinationKey, overwrite, ct);
    }

    private sealed class FlakyWriteStorage : IFileStorage
    {
        private readonly IFileStorage _inner;
        private int _remainingFailures;

        public FlakyWriteStorage(IFileStorage inner, int failTimes)
        {
            _inner = inner;
            _remainingFailures = failTimes;
        }

        public Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default)
        {
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
                throw new IOException("injected transient failure");
            return _inner.WriteAllLines(key, lines, ct);
        }

        public Task<bool> Exists(string key, CancellationToken ct = default) => _inner.Exists(key, ct);
        public IAsyncEnumerable<string> ListKeys(string prefix, string? suffix = null, bool recursive = true, CancellationToken ct = default) => _inner.ListKeys(prefix, suffix, recursive, ct);
        public Task<Stream> OpenRead(string key, CancellationToken ct = default) => _inner.OpenRead(key, ct);
        public Task<string> ReadAllText(string key, CancellationToken ct = default) => _inner.ReadAllText(key, ct);
        public Task<string[]> ReadAllLines(string key, CancellationToken ct = default) => _inner.ReadAllLines(key, ct);
        public IAsyncEnumerable<string> ReadLines(string key, CancellationToken ct = default) => _inner.ReadLines(key, ct);
        public Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default) => _inner.ReadAllBytes(key, ct);
        public Task WriteAllText(string key, string content, System.Text.Encoding? encoding = null, CancellationToken ct = default) => _inner.WriteAllText(key, content, encoding, ct);
        public Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => _inner.WriteAllBytes(key, bytes, ct);
        public Task<IObjectWriteSession> OpenWriteSession(string key, CancellationToken ct = default) => _inner.OpenWriteSession(key, ct);
        public Task Delete(string key, CancellationToken ct = default) => _inner.Delete(key, ct);
        public Task DeleteByPrefix(string prefix, CancellationToken ct = default) => _inner.DeleteByPrefix(prefix, ct);
        public Task Move(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct = default) => _inner.Move(sourceKey, destinationKey, overwrite, ct);
    }

    private sealed class HangingWriteStorage : IFileStorage
    {
        private readonly IFileStorage _inner;
        public HangingWriteStorage(IFileStorage inner) { _inner = inner; }

        public Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default)
            => Task.Delay(Timeout.Infinite, ct);

        public Task<bool> Exists(string key, CancellationToken ct = default) => _inner.Exists(key, ct);
        public IAsyncEnumerable<string> ListKeys(string prefix, string? suffix = null, bool recursive = true, CancellationToken ct = default) => _inner.ListKeys(prefix, suffix, recursive, ct);
        public Task<Stream> OpenRead(string key, CancellationToken ct = default) => _inner.OpenRead(key, ct);
        public Task<string> ReadAllText(string key, CancellationToken ct = default) => _inner.ReadAllText(key, ct);
        public Task<string[]> ReadAllLines(string key, CancellationToken ct = default) => _inner.ReadAllLines(key, ct);
        public IAsyncEnumerable<string> ReadLines(string key, CancellationToken ct = default) => _inner.ReadLines(key, ct);
        public Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default) => _inner.ReadAllBytes(key, ct);
        public Task WriteAllText(string key, string content, System.Text.Encoding? encoding = null, CancellationToken ct = default) => _inner.WriteAllText(key, content, encoding, ct);
        public Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => _inner.WriteAllBytes(key, bytes, ct);
        public Task<IObjectWriteSession> OpenWriteSession(string key, CancellationToken ct = default) => _inner.OpenWriteSession(key, ct);
        public Task Delete(string key, CancellationToken ct = default) => _inner.Delete(key, ct);
        public Task DeleteByPrefix(string prefix, CancellationToken ct = default) => _inner.DeleteByPrefix(prefix, ct);
        public Task Move(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct = default) => _inner.Move(sourceKey, destinationKey, overwrite, ct);
    }
}

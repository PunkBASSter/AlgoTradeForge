using System.Collections.Concurrent;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.HistoryLoader.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

/// <summary>
/// Base for partition-oriented CSV writers. Buffers rows per partition key in memory and
/// publishes the whole partition atomically through <see cref="IFileStorage"/> on flush.
/// Each flush reads the existing object (if any), concatenates buffered rows, and rewrites
/// via <c>WriteAllLines</c> — atomic on local FS (`.tmp + Move`) and on S3 (single PutObject).
/// </summary>
/// <remarks>
/// Concurrency: per-partition <see cref="SemaphoreSlim"/> from <see cref="WriteLockManager"/>
/// serializes <c>AppendRow</c>, <c>RegisterPartitionWatermark</c>, and <c>FlushPartitionAsync</c>
/// against each other. The semaphore IS held across the storage round-trip inside
/// <c>FlushPartitionAsync</c> — that's how readers + concurrent writers on the same key are kept
/// consistent. <c>AppendRow</c> itself never crosses an HTTP boundary while holding the lock.
/// <para>
/// Resume model: subclasses call <see cref="RegisterPartitionWatermark"/> after probing
/// <see cref="IPartitionTailIndex"/> — the buffer is NOT hydrated with existing rows on open,
/// only the watermark. Each subsequent flush reads + merges + rewrites.
/// </para>
/// </remarks>
internal abstract class BufferedPartitionWriter : IBufferedPartitionWriter
{
    protected IFileStorage Storage { get; }
    protected IPartitionTailIndex TailIndex { get; }
    protected HistoryLoaderStorageOptions Options { get; }
    protected ILogger Logger { get; }
    protected WriteLockManager Locks { get; }

    private readonly ConcurrentDictionary<string, PartitionBuffer> _buffers = new();
    private long _lastBufferLimitWarnUtcTicks;

    protected BufferedPartitionWriter(
        IFileStorage storage,
        IPartitionTailIndex tailIndex,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger logger,
        WriteLockManager locks)
    {
        Storage = storage;
        TailIndex = tailIndex;
        Options = options.Value;
        Logger = logger;
        Locks = locks;
    }

    /// <summary>
    /// Buffer a row for later atomic publish. <paramref name="watermark"/> is the monotonic
    /// dedup key (timestamp for candle/feed, agg_id / update_id for tick / book). Rows whose
    /// watermark is &lt;= the current partition watermark are silently dropped.
    /// </summary>
    protected void AppendRow(string partitionKey, string header, string row, long watermark)
    {
        var sem = Locks.GetLock(partitionKey);
        sem.Wait();
        try
        {
            var buffer = _buffers.GetOrAdd(partitionKey, _ => new PartitionBuffer(header));

            // Watermark-only resume registers an empty header; the first real AppendRow installs
            // the actual schema. Without this, a flush that misses the existing-on-disk file
            // (e.g. deleted out from under us) would emit a partition with an empty header line.
            if (string.IsNullOrEmpty(buffer.Header) && !string.IsNullOrEmpty(header))
                buffer.Header = header;

            if (buffer.LastWatermark is { } lastWm && watermark <= lastWm) return;

            buffer.Rows.Add(row);
            buffer.ApproxBytes += row.Length;
            buffer.LastWatermark = watermark;

            CheckBufferSize(partitionKey, buffer);

            if (buffer.Rows.Count >= Options.FlushEveryRows && !buffer.FlushInFlight)
            {
                buffer.FlushInFlight = true;
                _ = FlushPartitionAsync(partitionKey, CancellationToken.None)
                    .ContinueWith(
                        t => Logger.LogError(t.Exception, "Threshold-triggered flush failed for {Partition}", partitionKey),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Seed the partition's dedup watermark from disk (subclass-resolved field).</summary>
    protected void RegisterPartitionWatermark(string partitionKey, string header, long? watermark)
    {
        var sem = Locks.GetLock(partitionKey);
        sem.Wait();
        try
        {
            _buffers.AddOrUpdate(
                partitionKey,
                _ => new PartitionBuffer(header) { LastWatermark = watermark },
                (_, existing) =>
                {
                    if (string.IsNullOrEmpty(existing.Header) && !string.IsNullOrEmpty(header))
                        existing.Header = header;
                    if (watermark.HasValue && (!existing.LastWatermark.HasValue || existing.LastWatermark.Value < watermark.Value))
                        existing.LastWatermark = watermark;
                    return existing;
                });
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        foreach (var key in _buffers.Keys)
        {
            ct.ThrowIfCancellationRequested();
            await FlushPartitionAsync(key, ct);
        }
    }

    private async Task FlushPartitionAsync(string partitionKey, CancellationToken ct)
    {
        var sem = Locks.GetLock(partitionKey);
        await sem.WaitAsync(ct);
        try
        {
            if (!_buffers.TryGetValue(partitionKey, out var buffer)) return;
            if (buffer.Rows.Count == 0)
            {
                buffer.FlushInFlight = false;
                return;
            }

            var rowsToWrite = buffer.Rows;
            var rowsToWriteBytes = buffer.ApproxBytes;
            var header = buffer.Header;
            buffer.Rows = new List<string>();
            buffer.ApproxBytes = 0;

            try
            {
                List<string> linesToWrite;
                if (await Storage.Exists(partitionKey, ct))
                {
                    var existing = await Storage.ReadAllLines(partitionKey, ct);
                    linesToWrite = new List<string>(existing.Length + rowsToWrite.Count);
                    linesToWrite.AddRange(existing);
                    linesToWrite.AddRange(rowsToWrite);
                }
                else
                {
                    linesToWrite = new List<string>(rowsToWrite.Count + 1) { header };
                    linesToWrite.AddRange(rowsToWrite);
                }

                await Storage.WriteAllLines(partitionKey, linesToWrite, ct);
                buffer.LastFlushUtc = DateTime.UtcNow;
            }
            catch
            {
                // Restore the un-published rows so a later flush retries them.
                var rescued = new List<string>(rowsToWrite.Count + buffer.Rows.Count);
                rescued.AddRange(rowsToWrite);
                rescued.AddRange(buffer.Rows);
                buffer.Rows = rescued;
                buffer.ApproxBytes = rowsToWriteBytes + buffer.ApproxBytes;
                throw;
            }
        }
        finally
        {
            if (_buffers.TryGetValue(partitionKey, out var buffer))
                buffer.FlushInFlight = false;
            sem.Release();
        }
    }

    private void CheckBufferSize(string partitionKey, PartitionBuffer buffer)
    {
        var limit = Options.InMemoryBufferLimitMB;
        if (limit <= 0) return;
        if (buffer.ApproxBytes <= limit * 1024L * 1024L) return;

        // Throttle warnings: at most once per minute across all partitions.
        var now = DateTime.UtcNow.Ticks;
        var prev = Interlocked.Read(ref _lastBufferLimitWarnUtcTicks);
        if (now - prev <= TimeSpan.FromMinutes(1).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastBufferLimitWarnUtcTicks, now, prev) != prev) return;

        Logger.LogWarning(
            "Buffered partition {Partition} size {ApproxMB} MB exceeded limit {LimitMB} MB; spill-to-disk not implemented",
            partitionKey, buffer.ApproxBytes / (1024 * 1024), limit);
    }

    private sealed class PartitionBuffer
    {
        public PartitionBuffer(string header) { Header = header; }
        public string Header { get; set; }
        public List<string> Rows { get; set; } = new();
        public long ApproxBytes { get; set; }
        public long? LastWatermark { get; set; }
        public DateTime LastFlushUtc { get; set; } = DateTime.UtcNow;
        public bool FlushInFlight { get; set; }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.IO;


namespace AlgoTradeForge.Infrastructure.Events;

/// <summary>
/// JSONL run sink that buffers events in memory and atomically re-publishes the whole
/// file through <see cref="IFileStorage"/> on each flush. The buffer-then-PUT pattern
/// (same shape as the HistoryLoader's BufferedPartitionWriter) is what lets the same
/// sink target local FS (.tmp + Move) and S3 (single PutObject) without code changes.
/// </summary>
/// <remarks>
/// Live tailing of the on-disk events.jsonl is gone — readers see content only after
/// a Flush. The WebSocket sink remains the channel for live debugger consumers; on-disk
/// consumers (SqliteEventIndexBuilder, SqliteTradeDbWriter) run after the sink is
/// disposed, so the final flush makes the whole stream visible before they touch it.
/// </remarks>
public sealed class JsonlFileSink : IRunSink
{
    private const byte NewLine = (byte)'\n';

    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly RunIdentity _identity;
    private readonly IFileStorage _fileStorage;
    private readonly string _eventsKey;
    private readonly string _metaKey;
    private readonly Lock _writeLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly MemoryStream _buffer = new();
    private int _disposed;

    public string RunFolderPath { get; }

    public JsonlFileSink(RunIdentity identity, EventLogStorageOptions options, IFileStorage fileStorage)
    {
        _identity = identity;
        _fileStorage = fileStorage;
        RunFolderPath = Path.Combine(options.Root, identity.ComputeFolderName());
        Directory.CreateDirectory(RunFolderPath);
        // IFileStorage keys are slash-delimited; the local backend's absolute-path bypass
        // still resolves these correctly until the StorageKey migration in PR 5.
        _eventsKey = ToKey(RunFolderPath, "events.jsonl");
        _metaKey = ToKey(RunFolderPath, "meta.json");
    }

    private static string ToKey(string folder, string name)
        => (folder + "/" + name).Replace('\\', '/');

    public void Write(ReadOnlyMemory<byte> utf8Json)
    {
        lock (_writeLock)
        {
            if (_disposed != 0) return;
            _buffer.Write(utf8Json.Span);
            _buffer.WriteByte(NewLine);
        }
    }

    // Each Flush re-publishes the entire accumulated buffer (atomic replace semantics).
    // Cost is O(total bytes written so far), so don't loop Flush per-event — call it on
    // dispose, or at coarse checkpoints.
    public async Task Flush(CancellationToken ct = default)
    {
        byte[] snapshot;
        lock (_writeLock)
        {
            if (_buffer.Length == 0) return;
            snapshot = _buffer.ToArray();
        }

        await _flushGate.WaitAsync(ct);
        try
        {
            await _fileStorage.WriteAllBytes(_eventsKey, snapshot, ct);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public Task WriteMeta(RunSummary summary, CancellationToken ct = default)
    {
        var meta = new RunMeta
        {
            StrategyName = _identity.StrategyName,
            StrategyVersion = _identity.StrategyVersion,
            AssetName = _identity.AssetName,
            StartTime = _identity.StartTime,
            EndTime = _identity.EndTime,
            InitialCash = _identity.InitialCash,
            RunMode = _identity.RunMode,
            RunTimestamp = _identity.RunTimestamp,
            StrategyParameters = _identity.StrategyParameters,
            TotalBarsProcessed = summary.TotalBarsProcessed,
            FinalEquity = summary.FinalEquity,
            TotalFills = summary.TotalFills,
            Duration = summary.Duration,
        };

        var json = JsonSerializer.Serialize(meta, MetaJsonOptions);
        return _fileStorage.WriteAllText(_metaKey, json, Encoding.UTF8, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Flush();
        }
        finally
        {
            await _buffer.DisposeAsync();
            _flushGate.Dispose();
        }
    }
}

namespace AlgoTradeForge.HistoryLoader.Application;

/// <summary>
/// Tunables for the buffer-then-PUT writer pattern. Bound from <c>HistoryLoader:Storage</c>.
/// Defaults: flush at most every 60 s, or every 1000 rows per partition, with a 16 MB
/// per-partition advisory budget (PR3 logs a warning when exceeded; spill-to-disk is deferred).
/// </summary>
public sealed class HistoryLoaderStorageOptions
{
    public int FlushIntervalSeconds { get; set; } = 60;
    public int FlushEveryRows { get; set; } = 1000;
    public int InMemoryBufferLimitMB { get; set; } = 16;

    /// <summary>
    /// Upper bound for the final flush kicked off during graceful shutdown. A wedged S3 endpoint
    /// would otherwise block the host from exiting; unflushed rows past this window are lost.
    /// </summary>
    public int ShutdownFlushTimeoutSeconds { get; set; } = 30;
}

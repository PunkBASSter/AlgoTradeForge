namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

/// <summary>
/// Implemented by writers that buffer rows in memory and publish whole partitions atomically.
/// The <see cref="BufferedWriterFlushService"/> fans out periodic + shutdown flushes through
/// this marker interface.
/// </summary>
internal interface IBufferedPartitionWriter
{
    Task FlushAllAsync(CancellationToken ct);
}

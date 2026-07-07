namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public interface IPartitionFileWriter
{
    // Writes header + rows to "<partitionPath>.tmp-<guid>" then atomically moves over partitionPath.
    Task ReplacePartition(string partitionPath, string header, IEnumerable<string> rows, CancellationToken ct = default);
}

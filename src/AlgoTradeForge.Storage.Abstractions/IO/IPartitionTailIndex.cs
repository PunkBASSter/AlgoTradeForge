namespace AlgoTradeForge.Storage;

public interface IPartitionTailIndex
{
    Task<string?> GetLastLine(string key, CancellationToken ct = default);
}

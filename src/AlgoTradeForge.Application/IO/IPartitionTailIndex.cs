namespace AlgoTradeForge.Application.IO;

public interface IPartitionTailIndex
{
    Task<string?> GetLastLine(string key, CancellationToken ct = default);
}

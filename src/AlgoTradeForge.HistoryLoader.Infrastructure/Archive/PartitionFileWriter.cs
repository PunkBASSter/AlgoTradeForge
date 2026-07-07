using AlgoTradeForge.HistoryLoader.Application.Archive;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class PartitionFileWriter : IPartitionFileWriter
{
    public async Task ReplacePartition(string partitionPath, string header, IEnumerable<string> rows, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partitionPath)!);
        var tempPath = $"{partitionPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = File.Create(tempPath))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteLineAsync(header.AsMemory(), ct);
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(row.AsMemory(), ct);
                }
            }
            try
            {
                File.Move(tempPath, partitionPath, overwrite: true);
            }
            catch (IOException)
            {
                // Windows: a concurrent reader (backtest loader) holding the partition open
                // fails the move; one short retry is cheap insurance before failing the job.
                await Task.Delay(500, ct);
                File.Move(tempPath, partitionPath, overwrite: true);
            }
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }
}

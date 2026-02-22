using System.Buffers.Binary;
using Microsoft.Extensions.Caching.Distributed;

namespace AlgoTradeForge.Application.Progress;

public sealed class RunProgressCache(IDistributedCache cache)
{
    private static string ProgressKey(Guid id) => $"progress:{id}";
    private static string RunKeyKey(string runKey) => $"runkey:{runKey}";

    public async Task SetProgressAsync(Guid id, long processed, long total, CancellationToken ct = default)
    {
        var buffer = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0), processed);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), total);
        await cache.SetAsync(ProgressKey(id), buffer, ct);
    }

    public async Task<(long Processed, long Total)?> GetProgressAsync(Guid id, CancellationToken ct = default)
    {
        var bytes = await cache.GetAsync(ProgressKey(id), ct);
        if (bytes is null)
            return null;

        var processed = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0));
        var total = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
        return (processed, total);
    }

    public async Task RemoveProgressAsync(Guid id, CancellationToken ct = default)
    {
        await cache.RemoveAsync(ProgressKey(id), ct);
    }

    public async Task SetRunKeyAsync(string runKey, Guid id, CancellationToken ct = default)
    {
        await cache.SetAsync(RunKeyKey(runKey), id.ToByteArray(), ct);
    }

    public async Task<Guid?> TryGetRunIdByKeyAsync(string runKey, CancellationToken ct = default)
    {
        var bytes = await cache.GetAsync(RunKeyKey(runKey), ct);
        return bytes is not null ? new Guid(bytes) : null;
    }

    public async Task RemoveRunKeyAsync(string runKey, CancellationToken ct = default)
    {
        await cache.RemoveAsync(RunKeyKey(runKey), ct);
    }
}

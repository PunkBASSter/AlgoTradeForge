using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace AlgoTradeForge.Application.Progress;

public sealed class RunProgressCache(IDistributedCache cache)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string ProgressKey(Guid id) => $"progress:{id}";
    private static string RunKeyKey(string runKey) => $"runkey:{runKey}";

    public async Task SetAsync(RunProgressEntry entry, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        await cache.SetStringAsync(ProgressKey(entry.Id), json, ct);
    }

    public async Task<RunProgressEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var json = await cache.GetStringAsync(ProgressKey(id), ct);
        return json is null ? null : JsonSerializer.Deserialize<RunProgressEntry>(json, JsonOptions);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await cache.RemoveAsync(ProgressKey(id), ct);
    }

    public async Task<Guid?> TryGetRunIdByKeyAsync(string runKey, CancellationToken ct = default)
    {
        var value = await cache.GetStringAsync(RunKeyKey(runKey), ct);
        return value is not null ? Guid.Parse(value) : null;
    }

    public async Task SetRunKeyAsync(string runKey, Guid id, CancellationToken ct = default)
    {
        await cache.SetStringAsync(RunKeyKey(runKey), id.ToString(), ct);
    }

    public async Task RemoveRunKeyAsync(string runKey, CancellationToken ct = default)
    {
        await cache.RemoveAsync(RunKeyKey(runKey), ct);
    }
}

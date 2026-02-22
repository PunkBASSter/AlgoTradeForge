using AlgoTradeForge.Application.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Progress;

public sealed class RunProgressCacheTests
{
    private readonly RunProgressCache _cache;

    public RunProgressCacheTests()
    {
        var memoryCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _cache = new RunProgressCache(memoryCache);
    }

    [Fact]
    public async Task SetAsync_GetAsync_RoundTrip()
    {
        var entry = MakeEntry();

        await _cache.SetAsync(entry);
        var result = await _cache.GetAsync(entry.Id);

        Assert.NotNull(result);
        Assert.Equal(entry.Id, result.Id);
        Assert.Equal(entry.Status, result.Status);
        Assert.Equal(entry.Processed, result.Processed);
        Assert.Equal(entry.Failed, result.Failed);
        Assert.Equal(entry.Total, result.Total);
    }

    [Fact]
    public async Task GetAsync_Returns_Null_For_Missing_Key()
    {
        var result = await _cache.GetAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_Removes_Entry()
    {
        var entry = MakeEntry();

        await _cache.SetAsync(entry);
        await _cache.RemoveAsync(entry.Id);
        var result = await _cache.GetAsync(entry.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetRunIdByKeyAsync_Returns_Null_For_Missing_Key()
    {
        var result = await _cache.TryGetRunIdByKeyAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetRunKeyAsync_TryGetRunIdByKeyAsync_RoundTrip()
    {
        var id = Guid.NewGuid();
        var runKey = "test-key-123";

        await _cache.SetRunKeyAsync(runKey, id);
        var result = await _cache.TryGetRunIdByKeyAsync(runKey);

        Assert.Equal(id, result);
    }

    [Fact]
    public async Task RemoveRunKeyAsync_Removes_Mapping()
    {
        var runKey = "test-key-456";
        await _cache.SetRunKeyAsync(runKey, Guid.NewGuid());

        await _cache.RemoveRunKeyAsync(runKey);
        var result = await _cache.TryGetRunIdByKeyAsync(runKey);

        Assert.Null(result);
    }

    private static RunProgressEntry MakeEntry(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Status = RunStatus.Running,
        Processed = 100,
        Failed = 2,
        Total = 1000,
        StartedAt = DateTimeOffset.UtcNow
    };
}

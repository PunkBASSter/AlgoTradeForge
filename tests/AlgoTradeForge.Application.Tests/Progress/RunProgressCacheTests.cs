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
    public async Task SetProgress_GetProgress_RoundTrip()
    {
        var id = Guid.NewGuid();

        await _cache.SetProgressAsync(id, 42, 1000);
        var result = await _cache.GetProgressAsync(id);

        Assert.NotNull(result);
        Assert.Equal(42, result.Value.Processed);
        Assert.Equal(1000, result.Value.Total);
    }

    [Fact]
    public async Task GetProgress_Returns_Null_For_Missing_Key()
    {
        var result = await _cache.GetProgressAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveProgress_Removes_Entry()
    {
        var id = Guid.NewGuid();

        await _cache.SetProgressAsync(id, 10, 100);
        await _cache.RemoveProgressAsync(id);
        var result = await _cache.GetProgressAsync(id);

        Assert.Null(result);
    }

    [Fact]
    public async Task SetRunKey_TryGetRunId_RoundTrip()
    {
        var id = Guid.NewGuid();
        var runKey = "test-key-123";

        await _cache.SetRunKeyAsync(runKey, id);
        var result = await _cache.TryGetRunIdByKeyAsync(runKey);

        Assert.Equal(id, result);
    }

    [Fact]
    public async Task TryGetRunId_Returns_Null_For_Missing_Key()
    {
        var result = await _cache.TryGetRunIdByKeyAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveRunKey_Removes_Mapping()
    {
        var runKey = "test-key-456";
        await _cache.SetRunKeyAsync(runKey, Guid.NewGuid());

        await _cache.RemoveRunKeyAsync(runKey);
        var result = await _cache.TryGetRunIdByKeyAsync(runKey);

        Assert.Null(result);
    }
}

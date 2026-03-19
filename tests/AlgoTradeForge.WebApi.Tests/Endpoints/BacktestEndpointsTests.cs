using AlgoTradeForge.Application.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

public class BacktestEndpointsTests
{
    private readonly RunProgressCache _progressCache;

    public BacktestEndpointsTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    [Fact]
    public async Task GetStatus_ActiveRun_CacheHitMeansRunning()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetProgressAsync(runId, 50, 100, TestContext.Current.CancellationToken);

        // Act
        var progress = await _progressCache.GetProgressAsync(runId, TestContext.Current.CancellationToken);

        // Assert — cache presence means running
        Assert.NotNull(progress);
        Assert.Equal(50, progress.Value.Processed);
        Assert.Equal(100, progress.Value.Total);
    }

    [Fact]
    public async Task GetStatus_CompletedRun_CacheMiss()
    {
        // Act — no cache entry for this ID
        var progress = await _progressCache.GetProgressAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert — cache miss, would check SQLite next
        Assert.Null(progress);
    }

    [Fact]
    public async Task GetStatus_UnknownRun_ReturnsNull()
    {
        // Act
        var progress = await _progressCache.GetProgressAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(progress);
    }

    [Fact]
    public async Task Cancel_RegisteredRun_CancelsViaRegistry()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var registry = new InMemoryRunCancellationRegistry();
        var cts = new CancellationTokenSource();
        registry.Register(runId, cts);

        // Act
        var result = registry.TryCancel(runId);

        // Assert
        Assert.True(result);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancel_UnknownRun_ReturnsFalse()
    {
        // Arrange
        var registry = new InMemoryRunCancellationRegistry();

        // Act
        var result = registry.TryCancel(Guid.NewGuid());

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task Dedup_SameRunKey_ReturnsSameId()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var runKey = "test-run-key-12345";

        await _progressCache.SetRunKeyAsync(runKey, runId, TestContext.Current.CancellationToken);
        await _progressCache.SetProgressAsync(runId, 0, 50, TestContext.Current.CancellationToken);

        // Act
        var existingId = await _progressCache.TryGetRunIdByKeyAsync(runKey, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(runId, existingId);

        // Verify the run is active
        var progress = await _progressCache.GetProgressAsync(existingId!.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(progress);
    }

    [Fact]
    public async Task ProgressUpdate_RoundTrip()
    {
        // Arrange
        var runId = Guid.NewGuid();

        // Act — simulate progress updates
        await _progressCache.SetProgressAsync(runId, 0, 100, TestContext.Current.CancellationToken);
        await _progressCache.SetProgressAsync(runId, 50, 100, TestContext.Current.CancellationToken);
        await _progressCache.SetProgressAsync(runId, 100, 100, TestContext.Current.CancellationToken);

        var progress = await _progressCache.GetProgressAsync(runId, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(progress);
        Assert.Equal(100, progress.Value.Processed);
        Assert.Equal(100, progress.Value.Total);
    }
}

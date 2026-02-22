using AlgoTradeForge.Application.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

public class OptimizationEndpointsTests
{
    private readonly RunProgressCache _progressCache;

    public OptimizationEndpointsTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    [Fact]
    public async Task GetStatus_RunningOptimization_ReturnsProgressCounts()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetProgressAsync(runId, 15, 50);

        // Act
        var progress = await _progressCache.GetProgressAsync(runId);

        // Assert — cache presence means running
        Assert.NotNull(progress);
        Assert.Equal(15, progress.Value.Processed);
        Assert.Equal(50, progress.Value.Total);
    }

    [Fact]
    public async Task GetStatus_CompletedOptimization_CacheMiss()
    {
        // Act — no cache entry
        var progress = await _progressCache.GetProgressAsync(Guid.NewGuid());

        // Assert — would check SQLite next
        Assert.Null(progress);
    }

    [Fact]
    public async Task Cancel_RegisteredOptimization_CancelsViaRegistry()
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
    public async Task Cancel_UnknownOptimization_ReturnsFalse()
    {
        // Arrange
        var registry = new InMemoryRunCancellationRegistry();

        // Act
        var result = registry.TryCancel(Guid.NewGuid());

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProgressUpdate_Optimization_RoundTrip()
    {
        // Arrange
        var runId = Guid.NewGuid();

        // Act — simulate incremental progress updates
        await _progressCache.SetProgressAsync(runId, 0, 100);
        await _progressCache.SetProgressAsync(runId, 100, 100);
        await _progressCache.SetProgressAsync(runId, 200, 100);

        var progress = await _progressCache.GetProgressAsync(runId);

        // Assert
        Assert.NotNull(progress);
        Assert.Equal(200, progress.Value.Processed);
        Assert.Equal(100, progress.Value.Total);
    }

    [Fact]
    public async Task Cleanup_RemovesProgressAndRunKey()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var runKey = "opt-run-key-789";

        await _progressCache.SetProgressAsync(runId, 50, 100);
        await _progressCache.SetRunKeyAsync(runKey, runId);

        // Act — simulate cleanup (what finally block does)
        await _progressCache.RemoveProgressAsync(runId);
        await _progressCache.RemoveRunKeyAsync(runKey);

        // Assert
        var progress = await _progressCache.GetProgressAsync(runId);
        var mappedId = await _progressCache.TryGetRunIdByKeyAsync(runKey);
        Assert.Null(progress);
        Assert.Null(mappedId);
    }
}

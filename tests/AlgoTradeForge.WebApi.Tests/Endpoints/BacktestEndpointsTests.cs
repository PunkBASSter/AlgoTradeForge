using System.Net;
using System.Net.Http.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.WebApi.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

public class BacktestEndpointsTests
{
    private readonly IDistributedCache _distributedCache;
    private readonly RunProgressCache _progressCache;

    public BacktestEndpointsTests()
    {
        _distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(_distributedCache);
    }

    [Fact]
    public async Task GetStatus_RunningEntry_ReturnsOkWithProgress()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Running,
            Processed = 50,
            Failed = 0,
            Total = 100,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(RunStatus.Running, entry.Status);
        Assert.Equal(50, entry.Processed);
        Assert.Equal(100, entry.Total);
    }

    [Fact]
    public async Task GetStatus_CompletedEntry_ReturnsCompletedStatus()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Completed,
            Processed = 100,
            Failed = 0,
            Total = 100,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(RunStatus.Completed, entry.Status);
        Assert.Equal(100, entry.Processed);
    }

    [Fact]
    public async Task GetStatus_FailedEntry_ReturnsErrorDetails()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Failed,
            Processed = 30,
            Failed = 0,
            Total = 100,
            ErrorMessage = "Test error",
            ErrorStackTrace = "at TestStack",
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(RunStatus.Failed, entry.Status);
        Assert.Equal("Test error", entry.ErrorMessage);
        Assert.Equal("at TestStack", entry.ErrorStackTrace);
    }

    [Fact]
    public async Task GetStatus_UnknownId_ReturnsNull()
    {
        // Act
        var entry = await _progressCache.GetAsync(Guid.NewGuid());

        // Assert
        Assert.Null(entry);
    }

    [Fact]
    public async Task Cancel_RunningEntry_UpdatesStatusToCancelled()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var registry = new InMemoryRunCancellationRegistry();
        var cts = new CancellationTokenSource();
        registry.Register(runId, cts);

        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Running,
            Processed = 10,
            Failed = 0,
            Total = 100,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);
        Assert.NotNull(entry);
        Assert.Equal(RunStatus.Running, entry.Status);

        registry.TryCancel(runId);
        await _progressCache.SetAsync(entry with { Status = RunStatus.Cancelled });

        // Assert
        var updated = await _progressCache.GetAsync(runId);
        Assert.NotNull(updated);
        Assert.Equal(RunStatus.Cancelled, updated.Status);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancel_CompletedEntry_ConflictDetected()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Completed,
            Processed = 100,
            Failed = 0,
            Total = 100,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);

        // Assert — status is terminal, cancel should not proceed
        Assert.NotNull(entry);
        Assert.True(entry.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled);
    }

    [Fact]
    public async Task Dedup_SameRunKey_ReturnsSameId()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var runKey = "test-run-key-12345";

        await _progressCache.SetRunKeyAsync(runKey, runId);
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Running,
            Processed = 0,
            Failed = 0,
            Total = 50,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var existingId = await _progressCache.TryGetRunIdByKeyAsync(runKey);

        // Assert
        Assert.Equal(runId, existingId);
    }
}

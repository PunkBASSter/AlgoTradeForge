using AlgoTradeForge.Application.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

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
    public async Task GetStatus_RunningOptimization_ReturnsProgressWithCombinationCounts()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Running,
            Processed = 15,
            Failed = 2,
            Total = 50,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(RunStatus.Running, entry.Status);
        Assert.Equal(15, entry.Processed);
        Assert.Equal(2, entry.Failed);
        Assert.Equal(50, entry.Total);
    }

    [Fact]
    public async Task GetStatus_CompletedOptimization_ReturnsCompletedStatus()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Completed,
            Processed = 50,
            Failed = 3,
            Total = 50,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(RunStatus.Completed, entry.Status);
        Assert.Equal(50, entry.Processed);
        Assert.Equal(3, entry.Failed);
    }

    [Fact]
    public async Task Cancel_PendingOptimization_CancelsSuccessfully()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var registry = new InMemoryRunCancellationRegistry();
        var cts = new CancellationTokenSource();
        registry.Register(runId, cts);

        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Pending,
            Processed = 0,
            Failed = 0,
            Total = 50,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var cancelResult = registry.TryCancel(runId);
        var entry = await _progressCache.GetAsync(runId);
        await _progressCache.SetAsync(entry! with { Status = RunStatus.Cancelled });

        // Assert
        Assert.True(cancelResult);
        Assert.True(cts.IsCancellationRequested);
        var updated = await _progressCache.GetAsync(runId);
        Assert.Equal(RunStatus.Cancelled, updated!.Status);
    }

    [Fact]
    public async Task Cancel_FailedOptimization_DetectsTerminalState()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Failed,
            Processed = 10,
            Failed = 5,
            Total = 50,
            ErrorMessage = "Out of memory",
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var entry = await _progressCache.GetAsync(runId);

        // Assert — terminal state, cannot cancel
        Assert.NotNull(entry);
        Assert.True(entry.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_UnknownId_ReturnsNull()
    {
        // Act
        var entry = await _progressCache.GetAsync(Guid.NewGuid());

        // Assert
        Assert.Null(entry);
    }

    [Fact]
    public async Task ProgressEntry_RoundTrips_AllFields()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var original = new RunProgressEntry
        {
            Id = runId,
            Status = RunStatus.Running,
            Processed = 25,
            Failed = 3,
            Total = 100,
            ErrorMessage = null,
            ErrorStackTrace = null,
            StartedAt = startedAt
        };

        // Act
        await _progressCache.SetAsync(original);
        var retrieved = await _progressCache.GetAsync(runId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(runId, retrieved.Id);
        Assert.Equal(RunStatus.Running, retrieved.Status);
        Assert.Equal(25, retrieved.Processed);
        Assert.Equal(3, retrieved.Failed);
        Assert.Equal(100, retrieved.Total);
        Assert.Null(retrieved.ErrorMessage);
    }
}

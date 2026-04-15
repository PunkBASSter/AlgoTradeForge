using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public class OptimizationGroupHandlerTests
{
    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly IRunCancellationRegistry _cancellationRegistry = Substitute.For<IRunCancellationRegistry>();
    private readonly RunProgressCache _progressCache;

    public OptimizationGroupHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid RunId1 = Guid.NewGuid();
    private static readonly Guid RunId2 = Guid.NewGuid();

    private static OptimizationGroupRecord MakeGroup(
        string status = "Completed", params OptimizationRunRecord[] runs) => new()
    {
        Id = GroupId,
        StrategyName = "TestStrategy",
        OptimizationMethod = "BruteForce",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        TotalRuns = runs.Length,
        Status = status,
        SubscriptionsJson = "[]",
        BacktestSettingsJson = "{}",
        MaxParallelism = 4,
        Runs = runs,
    };

    private static OptimizationRunRecord MakeRun(Guid id, string status = "Completed") => new()
    {
        Id = id,
        StrategyName = "TestStrategy",
        StrategyVersion = "1.0",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        CompletedAt = DateTimeOffset.UtcNow,
        DurationMs = 1000,
        TotalCombinations = 100,
        SortBy = "Fitness",
        DataSubscriptions = [],
        BacktestSettings = new BacktestSettingsDto
        {
            InitialCash = 10_000m,
            StartTime = DateTimeOffset.UtcNow.AddDays(-30),
            EndTime = DateTimeOffset.UtcNow,
        },
        Trials = [],
        MaxParallelism = 4,
        Status = status,
    };

    // ── GetOptimizationGroupByIdQuery ────────────────────────────

    [Fact]
    public async Task GetById_ReturnsGroup_WhenFound()
    {
        var group = MakeGroup();
        _repository.GetOptimizationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        var handler = new GetOptimizationGroupByIdQueryHandler(_repository);
        var result = await handler.HandleAsync(new GetOptimizationGroupByIdQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(GroupId, result!.Id);
    }

    [Fact]
    public async Task GetById_ReturnsNull_WhenNotFound()
    {
        _repository.GetOptimizationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns((OptimizationGroupRecord?)null);

        var handler = new GetOptimizationGroupByIdQueryHandler(_repository);
        var result = await handler.HandleAsync(new GetOptimizationGroupByIdQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // ── GetOptimizationGroupTrialsQuery ──────────────────────────

    [Fact]
    public async Task GetTrials_DelegatesToRepository()
    {
        var paged = new PagedResult<BacktestRunRecord>([], 0);
        _repository.GetOptimizationGroupTrialsAsync(GroupId, 50, 10, "Fitness", Arg.Any<CancellationToken>())
            .Returns(paged);

        var handler = new GetOptimizationGroupTrialsQueryHandler(_repository);
        var result = await handler.HandleAsync(
            new GetOptimizationGroupTrialsQuery(GroupId, 50, 10, "Fitness"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetTrials_UsesDefaultParameters()
    {
        var paged = new PagedResult<BacktestRunRecord>([], 42);
        _repository.GetOptimizationGroupTrialsAsync(GroupId, 1000, 0, null, Arg.Any<CancellationToken>())
            .Returns(paged);

        var handler = new GetOptimizationGroupTrialsQueryHandler(_repository);
        var result = await handler.HandleAsync(new GetOptimizationGroupTrialsQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.Equal(42, result.TotalCount);
    }

    // ── GetOptimizationGroupStatusQuery ──────────────────────────

    [Fact]
    public async Task GetStatus_ReturnsNull_WhenGroupNotFound()
    {
        _repository.GetOptimizationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns((OptimizationGroupRecord?)null);

        var handler = new GetOptimizationGroupStatusQueryHandler(_repository, _progressCache);
        var result = await handler.HandleAsync(new GetOptimizationGroupStatusQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatus_ReturnsRunProgress_WhenInCache()
    {
        var group = MakeGroup("InProgress",
            MakeRun(RunId1, "InProgress"),
            MakeRun(RunId2, "Completed"));
        _repository.GetOptimizationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        // Seed progress for RunId1
        await _progressCache.SetProgressAsync(RunId1, 50, 100, TestContext.Current.CancellationToken);

        var handler = new GetOptimizationGroupStatusQueryHandler(_repository, _progressCache);
        var result = await handler.HandleAsync(new GetOptimizationGroupStatusQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("InProgress", result!.Status);
        Assert.Equal(2, result.Runs.Count);

        var run1 = result.Runs.First(r => r.Id == RunId1);
        Assert.Equal(50, run1.Processed);
        Assert.Equal(100, run1.Total);

        var run2 = result.Runs.First(r => r.Id == RunId2);
        Assert.Equal(100, run2.Processed); // fallback to TotalCombinations
        Assert.Equal(100, run2.Total);
    }

    [Fact]
    public async Task GetStatus_FallsBackToTotalCombinations_WhenNoProgress()
    {
        var group = MakeGroup("Completed", MakeRun(RunId1));
        _repository.GetOptimizationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        var handler = new GetOptimizationGroupStatusQueryHandler(_repository, _progressCache);
        var result = await handler.HandleAsync(new GetOptimizationGroupStatusQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var run = Assert.Single(result!.Runs);
        Assert.Equal(100, run.Processed);
        Assert.Equal(100, run.Total);
    }

    // ── CancelOptimizationGroupCommand ──────────────────────────

    [Fact]
    public async Task Cancel_ReturnsFalse_WhenNotFound()
    {
        _repository.GetOptimizationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns((OptimizationGroupRecord?)null);

        var handler = new CancelOptimizationGroupCommandHandler(_repository, _cancellationRegistry);
        var result = await handler.HandleAsync(new CancelOptimizationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task Cancel_CancelsViaGroupId()
    {
        var group = MakeGroup("InProgress",
            MakeRun(RunId1, "InProgress"),
            MakeRun(RunId2, "Completed"));
        _repository.GetOptimizationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        var handler = new CancelOptimizationGroupCommandHandler(_repository, _cancellationRegistry);
        var result = await handler.HandleAsync(new CancelOptimizationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.True(result);
        // Group-level CTS cancellation cascades to all linked per-DSS tokens
        _cancellationRegistry.Received(1).TryCancel(GroupId);
    }

    // ── DeleteOptimizationGroupCommand ──────────────────────────

    [Fact]
    public async Task Delete_ReturnsFalse_WhenNotFound()
    {
        _repository.DeleteOptimizationGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(false);

        var handler = new DeleteOptimizationGroupCommandHandler(_repository);
        var result = await handler.HandleAsync(new DeleteOptimizationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task Delete_ReturnsTrue_WhenDeleted()
    {
        _repository.DeleteOptimizationGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new DeleteOptimizationGroupCommandHandler(_repository);
        var result = await handler.HandleAsync(new DeleteOptimizationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.True(result);
    }
}

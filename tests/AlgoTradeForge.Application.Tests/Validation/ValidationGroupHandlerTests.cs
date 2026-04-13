using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Validation;

public class ValidationGroupHandlerTests
{
    private readonly IValidationRepository _repository = Substitute.For<IValidationRepository>();
    private readonly IRunCancellationRegistry _cancellationRegistry = Substitute.For<IRunCancellationRegistry>();
    private readonly RunProgressCache _progressCache;

    public ValidationGroupHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid OptGroupId = Guid.NewGuid();
    private static readonly Guid RunId1 = Guid.NewGuid();
    private static readonly Guid RunId2 = Guid.NewGuid();
    private static readonly Guid OptRunId1 = Guid.NewGuid();
    private static readonly Guid OptRunId2 = Guid.NewGuid();

    private static ValidationGroupRecord MakeGroup(
        string status = "Completed", params ValidationRunRecord[] runs) => new()
    {
        Id = GroupId,
        OptimizationGroupId = OptGroupId,
        StrategyName = "TestStrategy",
        ThresholdProfileName = "Crypto-Standard",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        TotalRuns = runs.Length,
        Status = status,
        Runs = runs,
    };

    private static ValidationRunRecord MakeRun(
        Guid id, Guid optRunId, string status = "Completed") => new()
    {
        Id = id,
        OptimizationRunId = optRunId,
        StrategyName = "TestStrategy",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        Status = status,
        ThresholdProfileName = "Crypto-Standard",
        CandidatesIn = 10,
        CandidatesOut = 5,
        Verdict = "Green",
    };

    // ── GetValidationGroupByIdQuery ─────────────────────────────

    [Fact]
    public async Task GetById_ReturnsGroup_WhenFound()
    {
        var group = MakeGroup();
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        var handler = new GetValidationGroupByIdQueryHandler(_repository);
        var result = await handler.HandleAsync(new GetValidationGroupByIdQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(GroupId, result!.Id);
        Assert.Equal(OptGroupId, result.OptimizationGroupId);
    }

    [Fact]
    public async Task GetById_ReturnsNull_WhenNotFound()
    {
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns((ValidationGroupRecord?)null);

        var handler = new GetValidationGroupByIdQueryHandler(_repository);
        var result = await handler.HandleAsync(new GetValidationGroupByIdQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // ── GetValidationGroupStatusQuery ───────────────────────────

    [Fact]
    public async Task GetStatus_ReturnsNull_WhenGroupNotFound()
    {
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns((ValidationGroupRecord?)null);

        var handler = new GetValidationGroupStatusQueryHandler(_repository, _progressCache);
        var result = await handler.HandleAsync(new GetValidationGroupStatusQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatus_ReturnsRunProgress_WhenInCache()
    {
        var group = MakeGroup("InProgress",
            MakeRun(RunId1, OptRunId1, "InProgress"),
            MakeRun(RunId2, OptRunId2, "Completed"));
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        // Seed progress for RunId1
        await _progressCache.SetProgressAsync(RunId1, 3, 10, TestContext.Current.CancellationToken);

        var handler = new GetValidationGroupStatusQueryHandler(_repository, _progressCache);
        var result = await handler.HandleAsync(new GetValidationGroupStatusQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("InProgress", result!.Status);
        Assert.Equal(2, result.Runs.Count);

        var run1 = result.Runs.First(r => r.Id == RunId1);
        Assert.Equal(3, run1.Processed);
        Assert.Equal(10, run1.Total);

        var run2 = result.Runs.First(r => r.Id == RunId2);
        Assert.Equal(1, run2.Total); // validation fallback
    }

    [Fact]
    public async Task GetStatus_InProgressFallback_ReportsZeroProcessed()
    {
        var group = MakeGroup("InProgress",
            MakeRun(RunId1, OptRunId1, "InProgress"));
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        // No progress in cache
        var handler = new GetValidationGroupStatusQueryHandler(_repository, _progressCache);
        var result = await handler.HandleAsync(new GetValidationGroupStatusQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var run = Assert.Single(result!.Runs);
        Assert.Equal(0, run.Processed); // InProgress without cache → 0
        Assert.Equal(1, run.Total);
    }

    [Fact]
    public async Task GetStatus_CompletedFallback_ReportsOneProcessed()
    {
        var group = MakeGroup("Completed",
            MakeRun(RunId1, OptRunId1, "Completed"));
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        var handler = new GetValidationGroupStatusQueryHandler(_repository, _progressCache);
        var result = await handler.HandleAsync(new GetValidationGroupStatusQuery(GroupId), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var run = Assert.Single(result!.Runs);
        Assert.Equal(1, run.Processed); // Completed without cache → 1
        Assert.Equal(1, run.Total);
    }

    // ── CancelValidationGroupCommand ────────────────────────────

    [Fact]
    public async Task Cancel_ReturnsFalse_WhenNotFound()
    {
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns((ValidationGroupRecord?)null);

        var handler = new CancelValidationGroupCommandHandler(_repository, _cancellationRegistry);
        var result = await handler.HandleAsync(new CancelValidationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task Cancel_CancelsInProgressRuns_SkipsCompleted()
    {
        var group = MakeGroup("InProgress",
            MakeRun(RunId1, OptRunId1, "InProgress"),
            MakeRun(RunId2, OptRunId2, "Completed"));
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);

        var handler = new CancelValidationGroupCommandHandler(_repository, _cancellationRegistry);
        var result = await handler.HandleAsync(new CancelValidationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.True(result);
        _cancellationRegistry.Received(1).TryCancel(RunId1);
        _cancellationRegistry.DidNotReceive().TryCancel(RunId2);
    }

    // ── DeleteValidationGroupCommand ────────────────────────────

    [Fact]
    public async Task Delete_ReturnsFalse_WhenNotFound()
    {
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns((ValidationGroupRecord?)null);
        _repository.DeleteValidationGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(false);

        var handler = new DeleteValidationGroupCommandHandler(_repository, _cancellationRegistry);
        var result = await handler.HandleAsync(new DeleteValidationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task Delete_CancelsRunsBeforeDeleting()
    {
        var group = MakeGroup("InProgress",
            MakeRun(RunId1, OptRunId1, "InProgress"));
        _repository.GetValidationGroupByIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(group);
        _repository.DeleteValidationGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new DeleteValidationGroupCommandHandler(_repository, _cancellationRegistry);
        var result = await handler.HandleAsync(new DeleteValidationGroupCommand(GroupId), TestContext.Current.CancellationToken);

        Assert.True(result);
        _cancellationRegistry.Received(1).TryCancel(RunId1);
    }
}

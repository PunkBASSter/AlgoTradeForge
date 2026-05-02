using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Validation;

/// <summary>
/// Unit tests for RunGroupValidationCommandHandler (T060).
/// Verifies: validation group creation, per-DSS runs reference source optimization runs,
/// handler throws on missing group/no completed runs, and group status set correctly.
/// </summary>
public class RunGroupValidationCommandHandlerTests
{
    private readonly IRunRepository _runRepo = Substitute.For<IRunRepository>();
    private readonly IValidationRepository _validationRepo = Substitute.For<IValidationRepository>();
    private readonly IThresholdProfileRepository _profileRepo = Substitute.For<IThresholdProfileRepository>();
    private readonly RunProgressCache _progressCache;
    private readonly ComputeTaskQueue _queue = new();

    public RunGroupValidationCommandHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private RunGroupValidationCommandHandler CreateHandler() => new(
        _runRepo, _validationRepo, _profileRepo, _progressCache, _queue,
        NullLogger<RunGroupValidationCommandHandler>.Instance);

    private static OptimizationGroupRecord MakeOptimizationGroup(
        int completedRuns = 2, int failedRuns = 0)
    {
        var groupId = Guid.NewGuid();
        var runs = new List<OptimizationRunRecord>();
        for (var i = 0; i < completedRuns; i++)
        {
            runs.Add(new OptimizationRunRecord
            {
                Id = Guid.NewGuid(),
                StrategyName = "TestStrategy",
                StrategyVersion = "1",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = 5000,
                TotalCombinations = 100,
                SortBy = "FitnessScore",
                DataSubscriptions = [new TimeBarSubscription($"ASSET{i}", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
                BacktestSettings = new BacktestSettingsDto
                {
                    InitialCash = 10_000m,
                    StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                    EndTime = DateTimeOffset.UtcNow,
                },
                MaxParallelism = 4,
                TrialCount = 50,
                Trials = [],
                Status = OptimizationRunStatus.Completed,
                GroupId = groupId,
                DssIndex = i,
            });
        }
        for (var i = 0; i < failedRuns; i++)
        {
            runs.Add(new OptimizationRunRecord
            {
                Id = Guid.NewGuid(),
                StrategyName = "TestStrategy",
                StrategyVersion = "1",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = 1000,
                TotalCombinations = 100,
                SortBy = "FitnessScore",
                DataSubscriptions = [new TimeBarSubscription($"FAILED{i}", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
                BacktestSettings = new BacktestSettingsDto
                {
                    InitialCash = 10_000m,
                    StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                    EndTime = DateTimeOffset.UtcNow,
                },
                MaxParallelism = 4,
                Trials = [],
                Status = OptimizationRunStatus.Failed,
                GroupId = groupId,
                DssIndex = completedRuns + i,
            });
        }

        return new OptimizationGroupRecord
        {
            Id = groupId,
            StrategyName = "TestStrategy",
            StrategyVersion = "1",
            OptimizationMethod = "BruteForce",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow,
            TotalRuns = completedRuns + failedRuns,
            Status = OptimizationGroupStatus.Completed,
            SubscriptionsJson = "[]",
            BacktestSettingsJson = "{}",
            MaxParallelism = 4,
            Runs = runs,
        };
    }

    [Fact]
    public async Task HandleAsync_CreatesValidationGroupAndChildRuns()
    {
        var optGroup = MakeOptimizationGroup(completedRuns: 2);
        _runRepo.GetOptimizationGroupByIdAsync(optGroup.Id, Arg.Any<CancellationToken>())
            .Returns(optGroup);
        _profileRepo.GetByNameAsync("Crypto-Standard", Arg.Any<CancellationToken>())
            .Returns((ThresholdProfileRecord?)null);
        _validationRepo.CountByOptimizationIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var handler = CreateHandler();
        var command = new RunGroupValidationCommand
        {
            OptimizationGroupId = optGroup.Id,
            ThresholdProfileName = "Crypto-Standard",
        };

        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Validation group created
        Assert.NotEqual(Guid.Empty, result.GroupId);
        Assert.Equal(2, result.Runs.Count);

        // InsertValidationGroupAsync called once
        await _validationRepo.Received(1).InsertValidationGroupAsync(
            Arg.Is<ValidationGroupRecord>(g =>
                g.OptimizationGroupId == optGroup.Id
                && g.StrategyName == "TestStrategy"
                && g.TotalRuns == 2
                && g.Status == ValidationGroupStatus.InProgress),
            Arg.Any<CancellationToken>());

        // InsertPlaceholderAsync called once per completed run
        await _validationRepo.Received(2).InsertPlaceholderAsync(
            Arg.Any<ValidationRunRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ChildRunsReferenceSourceOptimizationRuns()
    {
        var optGroup = MakeOptimizationGroup(completedRuns: 3);
        _runRepo.GetOptimizationGroupByIdAsync(optGroup.Id, Arg.Any<CancellationToken>())
            .Returns(optGroup);
        _profileRepo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ThresholdProfileRecord?)null);
        _validationRepo.CountByOptimizationIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var handler = CreateHandler();
        var result = await handler.HandleAsync(new RunGroupValidationCommand
        {
            OptimizationGroupId = optGroup.Id,
        }, TestContext.Current.CancellationToken);

        // Each validation run should reference a different optimization run
        var completedOptRunIds = optGroup.Runs
            .Where(r => r.Status == OptimizationRunStatus.Completed)
            .Select(r => r.Id)
            .ToHashSet();

        foreach (var valRun in result.Runs)
        {
            Assert.Contains(valRun.OptimizationRunId, completedOptRunIds);
        }

        Assert.Equal(completedOptRunIds.Count, result.Runs.Select(r => r.OptimizationRunId).Distinct().Count());
    }

    [Fact]
    public async Task HandleAsync_SkipsFailedRuns()
    {
        var optGroup = MakeOptimizationGroup(completedRuns: 1, failedRuns: 2);
        _runRepo.GetOptimizationGroupByIdAsync(optGroup.Id, Arg.Any<CancellationToken>())
            .Returns(optGroup);
        _profileRepo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ThresholdProfileRecord?)null);
        _validationRepo.CountByOptimizationIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var handler = CreateHandler();
        var result = await handler.HandleAsync(new RunGroupValidationCommand
        {
            OptimizationGroupId = optGroup.Id,
        }, TestContext.Current.CancellationToken);

        // Only 1 completed run → 1 validation run
        Assert.Single(result.Runs);
    }

    [Fact]
    public async Task HandleAsync_ThrowsWhenGroupNotFound()
    {
        _runRepo.GetOptimizationGroupByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((OptimizationGroupRecord?)null);

        var handler = CreateHandler();
        var command = new RunGroupValidationCommand
        {
            OptimizationGroupId = Guid.NewGuid(),
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_ThrowsWhenNoCompletedRuns()
    {
        var optGroup = MakeOptimizationGroup(completedRuns: 0, failedRuns: 3);
        _runRepo.GetOptimizationGroupByIdAsync(optGroup.Id, Arg.Any<CancellationToken>())
            .Returns(optGroup);

        var handler = CreateHandler();
        var command = new RunGroupValidationCommand
        {
            OptimizationGroupId = optGroup.Id,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_EnqueuesComputeTasksForEachCompletedRun()
    {
        var optGroup = MakeOptimizationGroup(completedRuns: 2);
        _runRepo.GetOptimizationGroupByIdAsync(optGroup.Id, Arg.Any<CancellationToken>())
            .Returns(optGroup);
        _profileRepo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ThresholdProfileRecord?)null);
        _validationRepo.CountByOptimizationIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var handler = CreateHandler();
        await handler.HandleAsync(new RunGroupValidationCommand
        {
            OptimizationGroupId = optGroup.Id,
        }, TestContext.Current.CancellationToken);

        // The queue should have 2 tasks enqueued
        var count = 0;
        while (_queue.Reader.TryRead(out _))
            count++;
        Assert.Equal(2, count);
    }
}

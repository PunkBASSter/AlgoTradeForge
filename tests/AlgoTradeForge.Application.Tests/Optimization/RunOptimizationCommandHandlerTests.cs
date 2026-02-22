using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public class RunOptimizationCommandHandlerTests
{
    private readonly IOptimizationStrategyFactory _strategyFactory = Substitute.For<IOptimizationStrategyFactory>();
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalculator = Substitute.For<IMetricsCalculator>();
    private readonly IOptimizationSpaceProvider _spaceProvider = Substitute.For<IOptimizationSpaceProvider>();
    private readonly ICartesianProductGenerator _cartesianGenerator = Substitute.For<ICartesianProductGenerator>();
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly IRunCancellationRegistry _cancellationRegistry = new InMemoryRunCancellationRegistry();
    private readonly RunProgressCache _progressCache;

    public RunOptimizationCommandHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private RunOptimizationCommandHandler CreateHandler()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(),
            Substitute.For<IRiskEvaluator>());

        return new RunOptimizationCommandHandler(
            engine, _strategyFactory, _assetRepository, _historyRepository,
            _metricsCalculator, _spaceProvider, new OptimizationAxisResolver(),
            _cartesianGenerator, _runRepository, _progressCache,
            _cancellationRegistry,
            NullLogger<RunOptimizationCommandHandler>.Instance);
    }

    private static RunOptimizationCommand CreateCommand() => new()
    {
        StrategyName = "TestStrategy",
        InitialCash = 10_000m,
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        DataSubscriptions =
        [
            new DataSubscriptionDto { Asset = "BTCUSDT", Exchange = "Binance", TimeFrame = "00:01:00" }
        ],
        Axes = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Period"] = new RangeOverride(10, 20, 5)
        }
    };

    private void SetupStandardMocks(long estimatedCount = 3)
    {
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));

        var descriptor = new OptimizationSpaceDescriptor(
            "TestStrategy",
            typeof(object),
            typeof(object),
            new List<ParameterAxis>
            {
                new NumericRangeAxis("Period", 1, 100, 1, typeof(int))
            });
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var series = TestBars.CreateSeries(10);
        _historyRepository.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(series);

        _cartesianGenerator.EstimateCount(Arg.Any<IReadOnlyList<ResolvedAxis>>())
            .Returns(estimatedCount);
        _cartesianGenerator.Enumerate(Arg.Any<IReadOnlyList<ResolvedAxis>>())
            .Returns(new List<ParameterCombination>
            {
                new(new Dictionary<string, object> { ["Period"] = 10 }),
                new(new Dictionary<string, object> { ["Period"] = 15 }),
                new(new Dictionary<string, object> { ["Period"] = 20 }),
            });
    }

    [Fact]
    public async Task HandleAsync_DedupHit_ReturnsExistingIdWithIsDedupTrue()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        var existingId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, existingId);
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = existingId,
            Status = RunStatus.Running,
            Processed = 1,
            Failed = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.Equal(existingId, result.Id);
        Assert.True(result.IsDedup);
    }

    [Fact]
    public async Task HandleAsync_NewRun_ReturnsSubmissionWithCorrectTotalCombinations()
    {
        // Arrange
        SetupStandardMocks(estimatedCount: 3);
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.False(result.IsDedup);
        Assert.Equal(3, result.TotalCombinations);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task HandleAsync_NewRun_CreatesProgressEntryAndSetsRunKey()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        var entry = await _progressCache.GetAsync(result.Id);
        Assert.NotNull(entry);
        Assert.True(entry.Status is RunStatus.Pending or RunStatus.Running);
        Assert.Equal(3, entry.Total);

        var runKey = RunKeyBuilder.Build(command);
        var mappedId = await _progressCache.TryGetRunIdByKeyAsync(runKey);
        Assert.Equal(result.Id, mappedId);
    }

    [Fact]
    public async Task HandleAsync_StrategyNotFound_ThrowsArgumentException()
    {
        // Arrange — spaceProvider returns null for unknown strategy
        _spaceProvider.GetDescriptor("TestStrategy").Returns((IOptimizationSpaceDescriptor?)null);
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));
    }

    [Fact]
    public async Task HandleAsync_NoDataSubscriptions_ThrowsArgumentException()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand() with { DataSubscriptions = [] };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));
    }

    [Fact]
    public async Task HandleAsync_MaxCombinationsExceeded_ThrowsArgumentException()
    {
        // Arrange
        SetupStandardMocks(estimatedCount: 200_000);
        var handler = CreateHandler();
        var command = CreateCommand() with { MaxCombinations = 100_000 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));
    }

    [Fact]
    public async Task HandleAsync_StaleDedup_CleansUpAndCreatesNewRun()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        var staleId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, staleId);
        // No progress entry for staleId — stale mapping

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.NotEqual(staleId, result.Id);
        Assert.False(result.IsDedup);
    }
}

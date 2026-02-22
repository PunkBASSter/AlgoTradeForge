using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

public class RunBacktestCommandHandlerTests
{
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IStrategyFactory _strategyFactory = Substitute.For<IStrategyFactory>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalculator = Substitute.For<IMetricsCalculator>();
    private readonly IRunSinkFactory _runSinkFactory = Substitute.For<IRunSinkFactory>();
    private readonly IPostRunPipeline _postRunPipeline = Substitute.For<IPostRunPipeline>();
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly IRunCancellationRegistry _cancellationRegistry = new InMemoryRunCancellationRegistry();
    private readonly RunProgressCache _progressCache;
    private readonly IDistributedCache _distributedCache;

    public RunBacktestCommandHandlerTests()
    {
        _distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(_distributedCache);
    }

    private RunBacktestCommandHandler CreateHandler()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(),
            Substitute.For<IRiskEvaluator>());

        var preparer = new BacktestPreparer(
            _assetRepository, _strategyFactory, _historyRepository);

        return new RunBacktestCommandHandler(
            engine, preparer, _metricsCalculator,
            _runSinkFactory, _postRunPipeline, _runRepository,
            _progressCache, _cancellationRegistry,
            NullLogger<RunBacktestCommandHandler>.Instance);
    }

    private static RunBacktestCommand CreateCommand() => new()
    {
        AssetName = "BTCUSDT",
        Exchange = "Binance",
        StrategyName = "TestStrategy",
        InitialCash = 10_000m,
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private void SetupPreparerMocks()
    {
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Domain.Asset?>(asset));

        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.Version.Returns("1.0");
        strategy.DataSubscriptions.Returns(new List<DataSubscription>
        {
            new(asset, TimeSpan.FromMinutes(1))
        });

        _strategyFactory.Create("TestStrategy", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var series = TestBars.CreateSeries(10);
        _historyRepository.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(series);
    }

    [Fact]
    public async Task HandleAsync_DedupHit_ReturnsExistingIdWithIsDedupTrue()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        var existingId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, existingId);
        await _progressCache.SetAsync(new RunProgressEntry
        {
            Id = existingId,
            Status = RunStatus.Running,
            Processed = 5,
            Failed = 0,
            Total = 10,
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.Equal(existingId, result.Id);
        Assert.True(result.IsDedup);
    }

    [Fact]
    public async Task HandleAsync_NewRun_ReturnsSubmissionWithIsDedupFalse()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.False(result.IsDedup);
        Assert.Equal(10, result.TotalBars);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task HandleAsync_NewRun_CreatesProgressEntryInCache()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        var entry = await _progressCache.GetAsync(result.Id);
        Assert.NotNull(entry);
        Assert.Equal(result.Id, entry.Id);
        Assert.True(entry.Status is RunStatus.Pending or RunStatus.Running);
        Assert.Equal(10, entry.Total);
    }

    [Fact]
    public async Task HandleAsync_NewRun_SetsRunKeyMapping()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        var runKey = RunKeyBuilder.Build(command);
        var mappedId = await _progressCache.TryGetRunIdByKeyAsync(runKey);
        Assert.Equal(result.Id, mappedId);
    }

    [Fact]
    public async Task HandleAsync_StaleDedup_CleansUpAndCreatesNewRun()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Set up a stale RunKey mapping (no matching progress entry)
        var staleId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, staleId);

        // Act
        var result = await handler.HandleAsync(command);

        // Assert — new run created, not the stale ID
        Assert.NotEqual(staleId, result.Id);
        Assert.False(result.IsDedup);
    }

    [Fact]
    public async Task HandleAsync_AssetNotFound_ThrowsArgumentException()
    {
        // Arrange — don't set up asset repository mock (returns null)
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));
    }
}
